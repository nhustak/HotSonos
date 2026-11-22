using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using HotSonos.Activity;
using HotSonos.App.Library;
using HotSonos.App.Mcp;
using HotSonos.App.Models;
using HotSonos.App.Remote;
using HotSonos.App.Services;
using HotSonos.Core.Models;
using HotSonos.Remote.Protocol;

namespace HotSonos.App.Wpf.Tests;

public class NowPlayingTagRouteTests
{
    [Fact]
    public async Task NowTags_requires_the_pin_and_refuses_a_track_it_cannot_write()
    {
        var dir = Directory.CreateTempSubdirectory("hotsonos-now-tags").FullName;
        var poll = SonosManager.UseNowPlayingPoll;
        SonosManager.UseNowPlayingPoll = false;
        HotSonosRemoteHost? host = null;
        LibraryService? library = null;
        try
        {
            var settings = AppSettings.CreateDefault();
            settings.RemotePin = "123456";
            File.WriteAllText(Path.Combine(dir, "heard.json"), "{}");
            library = new LibraryService(() => settings, databasePath: Path.Combine(dir, "library.db"));
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
                Library = library,
                GetLastNowPlaying = () => new NowPlaying
                {
                    Title = "Stream",
                    Artist = "Radio",
                    TrackUri = "x-sonosapi-stream:radio",
                    State = SonosTransportState.Playing,
                },
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
            var url = $"http://127.0.0.1:{port}/api/now/tags";

            var denied = await SendAsync(http, url, pin: null, method: HttpMethod.Get, body: null);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.Status);

            var page = await SendAsync(http, url, pin: "123456", method: HttpMethod.Get, body: null);
            Assert.Equal(HttpStatusCode.OK, page.Status);
            using (var doc = JsonDocument.Parse(page.Body))
            {
                Assert.False(doc.RootElement.GetProperty("canTag").GetBoolean());
                Assert.Equal(NowPlayingTagResolve.NotInCache, doc.RootElement.GetProperty("message").GetString());
                Assert.True(doc.RootElement.GetProperty("tags").GetArrayLength() >= 6);
                Assert.Equal(
                    "Dinner",
                    doc.RootElement.GetProperty("tags").EnumerateArray().First(t => t.GetProperty("label").GetString() == "Dinner").GetProperty("label").GetString());
            }

            var missingKey = await SendAsync(http, url, pin: "123456", method: HttpMethod.Post, body: "{}");
            Assert.Equal(HttpStatusCode.BadRequest, missingKey.Status);

            var toggle = await SendAsync(http, url, pin: "123456", method: HttpMethod.Post, body: """{"key":"dinner"}""");
            Assert.Equal(HttpStatusCode.BadRequest, toggle.Status);
            using (var doc = JsonDocument.Parse(toggle.Body))
            {
                Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
                Assert.Equal(NowPlayingTagResolve.NotInCache, doc.RootElement.GetProperty("error").GetString());
            }
        }
        finally
        {
            SonosManager.UseNowPlayingPoll = poll;
            if (host is not null)
                await host.DisposeAsync();
            library?.Dispose();
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
        }
    }

    static async Task<(HttpStatusCode Status, string Body)> SendAsync(
        HttpClient http,
        string url,
        string? pin,
        HttpMethod method,
        string? body)
    {
        var attempt = 0;
        while (true)
        {
            try
            {
                using var request = new HttpRequestMessage(method, url);
                if (pin is not null)
                    request.Headers.TryAddWithoutValidation(RemoteApi.PinHeader, pin);
                if (body is not null)
                    request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                using var response = await http.SendAsync(request);
                return (response.StatusCode, await response.Content.ReadAsStringAsync());
            }
            catch (HttpRequestException) when (attempt++ < 20)
            {
                await Task.Delay(100);
            }
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
