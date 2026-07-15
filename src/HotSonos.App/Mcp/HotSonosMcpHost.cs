using HotSonos.App.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace HotSonos.App.Mcp;

/// <summary>
/// Hosts the loopback HTTP MCP endpoint inside the tray process
/// (http://127.0.0.1:{port}/mcp). Same pattern as HotSSC / SmartInspect.
/// </summary>
public sealed class HotSonosMcpHost : IAsyncDisposable
{
    private WebApplication? _app;
    private CancellationTokenSource? _cts;
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
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.Services.AddSingleton(state);
        builder.Services
            .AddMcpServer()
            .WithHttpTransport()
            .WithTools<HotSonosDebugTools>();

        _app = builder.Build();
        // Prefix /mcp so clients use http://127.0.0.1:{port}/mcp (SSE: /mcp/sse, messages: /mcp/message).
        _app.MapMcp("/mcp");

        _runTask = _app.RunAsync();
        state.IsRunning = true;

        // Brief yield so bind failures surface before we claim success.
        await Task.Delay(150, ct).ConfigureAwait(false);
        if (_runTask.IsFaulted)
        {
            state.IsRunning = false;
            var ex = _runTask.Exception?.GetBaseException() ?? new InvalidOperationException("MCP host failed to start.");
            AppLog.Error($"MCP host failed on port {port}", ex);
            throw ex;
        }

        AppLog.Info($"MCP listening at {Endpoint}");
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
            _cts?.Dispose();
            _cts = null;
            Endpoint = null;
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
