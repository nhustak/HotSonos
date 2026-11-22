using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using HotSonos.Activity;
using HotSonos.App.Mcp;
using HotSonos.App.Models;
using HotSonos.App.Remote;
using HotSonos.App.Services;
using HotSonos.Remote.Protocol;

namespace HotSonos.App.Wpf.Tests;

public class RemoteTopologyRouteTests
{
    [Fact]
    public async Task GetTopology_ReturnsNonEmptyJson()
    {
        var dir = Directory.CreateTempSubdirectory("hotsonos-topology-api").FullName;
        var poll = SonosManager.UseNowPlayingPoll;
        SonosManager.UseNowPlayingPoll = false;
        HotSonosRemoteHost? host = null;
        try
        {
            var settings = AppSettings.CreateDefault();
            settings.RemotePin = "123456";
            File.WriteAllText(Path.Combine(dir, "heard.json"), "{}");
            var sonos = new SonosManager(
                () => settings,
                new PlayHistoryStore(() => 14, Path.Combine(dir, "play-history.json")),
                new PlayEventLog(Path.Combine(dir, "play-events.jsonl")),
                new HeardOutsideLibraryLog(Path.Combine(dir, "heard.json"), Path.Combine(dir, "play-events.jsonl")),
                new TopologyEventLog(Path.Combine(dir, "topology-events.jsonl")),
                new ShuffleQueueStateStore(Path.Combine(dir, "shuffle-queue-state.json")),
                ActivityLog.Empty());
            var state = new HotSonosMcpState
            {
                Sonos = sonos,
                Settings = () => settings,
            };
            host = new HotSonosRemoteHost();
            var port = FreeTcpPort();
            await host.StartAsync(
                state,
                new RemoteHooks
                {
                    IsBusy = () => false,
                    Version = () => "test",
                    SetLogicalVolumeAsync = (_, _) => Task.FromResult(0),
                    SampleVolumeAsync = _ => Task.FromResult<(int? Volume, bool? Muted)>((null, null)),
                },
                "127.0.0.1",
                port);

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var url = $"http://127.0.0.1:{port}/api/topology";
            HttpResponseMessage response;
            string body;
            var attempt = 0;
            while (true)
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.TryAddWithoutValidation(RemoteApi.PinHeader, "123456");
                    response = await http.SendAsync(request);
                    body = await response.Content.ReadAsStringAsync();
                    break;
                }
                catch (HttpRequestException) when (attempt++ < 20)
                {
                    await Task.Delay(100);
                }
            }

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.False(string.IsNullOrWhiteSpace(body));
            if (response.Content.Headers.ContentLength is long length)
                Assert.True(length > 0, "topology response had Content-Length 0");

            using var doc = JsonDocument.Parse(body);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("members").ValueKind);
            Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("groups").ValueKind);
            Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("layout").ValueKind);
            Assert.Equal("No topology snapshot yet.", doc.RootElement.GetProperty("error").GetString());
        }
        finally
        {
            SonosManager.UseNowPlayingPoll = poll;
            if (host is not null)
                await host.DisposeAsync();
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
        }
    }

    static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
