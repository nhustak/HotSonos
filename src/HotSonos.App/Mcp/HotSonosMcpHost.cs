using HotSonos.App.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace HotSonos.App.Mcp;

/// <summary>
/// Hosts the loopback HTTP MCP endpoint inside the tray process
/// (http://127.0.0.1:{port}/mcp). Isolation item #2: <see cref="ManualHostLifetime"/>,
/// HostOptions ignore background faults, Kestrel connection limits.
/// </summary>
public sealed class HotSonosMcpHost : IAsyncDisposable
{
    private WebApplication? _app;
    private ManualHostLifetime? _lifetime;
    private Task? _runTask;

    public string? Endpoint { get; private set; }
    public bool IsRunning => _app is not null && _runTask is { IsCompleted: false };

    public async Task StartAsync(HotSonosMcpState state, int port, CancellationToken ct = default)
    {
        await StopAsync().ConfigureAwait(false);

        port = Math.Clamp(port, 1024, 65535);
        Endpoint = $"http://127.0.0.1:{port}/mcp";
        state.Endpoint = Endpoint;

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            ApplicationName = "HotSonos.Mcp",
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Limits.MaxConcurrentConnections = 40;
            k.Limits.MaxRequestBodySize = 2 * 1024 * 1024;
            k.AddServerHeader = false;
        });
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        // Route /mcp vs /mcp/ was matching two endpoints → AmbiguousMatchException on GET probes.
        builder.Services.Configure<RouteOptions>(o => o.AppendTrailingSlash = false);

        // Background MCP service faults must not stop the host (or the tray).
        builder.Services.Configure<HostOptions>(o =>
        {
            o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
            o.ShutdownTimeout = TimeSpan.FromSeconds(2);
        });

        // IHostLifetime only — never replace IHostApplicationLifetime (.NET 10 throws).
        _lifetime = new ManualHostLifetime();
        builder.Services.AddSingleton<IHostLifetime>(_lifetime);

        builder.Services.AddSingleton(state);
        builder.Services
            .AddMcpServer()
            .WithHttpTransport()
            .WithTools<HotSonosDebugTools>();

        try
        {
            _app = builder.Build();
        }
        catch (Exception ex)
        {
            _lifetime = null;
            AppLog.Error($"MCP host Build() failed on port {port}", ex);
            throw;
        }

        // Never let a single bad MCP request take down the host (or the tray app).
        _app.Use(async (ctx, next) =>
        {
            try
            {
                await next().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Error($"MCP request failed {ctx.Request.Method} {ctx.Request.Path}", ex);
                if (!ctx.Response.HasStarted)
                {
                    ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    await ctx.Response.WriteAsync("MCP error").ConfigureAwait(false);
                }
            }
        });

        // Prefix /mcp so clients use http://127.0.0.1:{port}/mcp (SSE: /mcp/sse, messages: /mcp/message).
        _app.MapMcp("/mcp");

        _runTask = _app.RunAsync();
        _ = _runTask.ContinueWith(
            t =>
            {
                state.IsRunning = false;
                if (t.IsFaulted)
                {
                    AppLog.Error("MCP host run task faulted", t.Exception?.GetBaseException());
                    AppLog.Lifecycle(
                        $"MCP run faulted: {t.Exception?.GetBaseException()?.GetType().Name}: " +
                        $"{t.Exception?.GetBaseException()?.Message}");
                }
                else if (t.IsCanceled)
                    AppLog.Info("MCP host run task canceled");
                else
                    AppLog.Info("MCP host run task completed (tray should keep running)");
            },
            TaskScheduler.Default);

        state.IsRunning = true;

        // Brief yield so bind failures surface before we claim success.
        await Task.Delay(200, ct).ConfigureAwait(false);
        if (_runTask.IsFaulted)
        {
            state.IsRunning = false;
            var ex = _runTask.Exception?.GetBaseException()
                     ?? new InvalidOperationException("MCP host failed to start.");
            AppLog.Error($"MCP host failed on port {port}", ex);
            AppLog.Lifecycle($"MCP start FAILED: {ex.GetType().Name}: {ex.Message}");
            try { await StopAsync().ConfigureAwait(false); } catch { /* ignore */ }
            throw ex;
        }

        AppLog.Info($"MCP listening at {Endpoint} (ManualHostLifetime / isolation #2)");
        AppLog.Lifecycle($"MCP listening {Endpoint} (isolation #2)");
    }

    public async Task StopAsync()
    {
        if (_app is null)
            return;

        try
        {
            await _app.StopAsync().ConfigureAwait(false);
            if (_runTask is not null)
            {
                try { await _runTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { AppLog.Warn("MCP host run ended with error", ex); }
            }

            await _app.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warn("MCP host stop failed", ex);
        }
        finally
        {
            _app = null;
            _runTask = null;
            _lifetime = null;
            Endpoint = null;
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
