using Microsoft.Extensions.Hosting;

namespace HotSonos.App.Mcp;

/// <summary>
/// Isolation item #2 — host lifetime for in-process MCP inside the WPF tray app.
/// Implements <see cref="IHostLifetime"/> only — .NET 10 Host rejects replacing
/// <see cref="IHostApplicationLifetime"/> ("Replacing IHostApplicationLifetime is not supported").
/// Does not use ConsoleLifetime (Ctrl+C / stdin), which is wrong for a tray process.
/// Shutdown is via <see cref="WebApplication.StopAsync"/>.
/// </summary>
internal sealed class ManualHostLifetime : IHostLifetime
{
    public Task WaitForStartAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled(cancellationToken);
        // Let the host start Kestrel immediately; we stop only via StopAsync on the WebApplication.
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
