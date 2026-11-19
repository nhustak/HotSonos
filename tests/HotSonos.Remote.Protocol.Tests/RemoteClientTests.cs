using System.Net;
using System.Text;
using HotSonos.Remote;
using HotSonos.Remote.Protocol;

namespace HotSonos.Remote.Protocol.Tests;

public class RemoteClientTests
{
    [Fact]
    public async Task Apply_AfterARequest_UsesTheNewAddressAndPin()
    {
        var handler = new ScriptedHandler();
        using var client = new RemoteClient(handler);

        client.Apply("192.168.1.129", 42342, "111111");
        var first = await client.GetStatusAsync(CancellationToken.None);

        client.Apply("10.0.0.8", 42400, "222222");
        var second = await client.GetStatusAsync(CancellationToken.None);

        Assert.Equal(RemoteCallKind.Ok, first.Kind);
        Assert.Equal("Shake It Up", first.Value?.NowPlaying?.Title);
        Assert.Equal(RemoteCallKind.Ok, second.Kind);
        Assert.Equal("Other Song", second.Value?.NowPlaying?.Title);

        Assert.Equal(2, handler.Calls.Count);
        Assert.Equal("http://192.168.1.129:42342/api/status", handler.Calls[0].Uri);
        Assert.Equal("111111", handler.Calls[0].Pin);
        Assert.Equal("http://10.0.0.8:42400/api/status", handler.Calls[1].Uri);
        Assert.Equal("222222", handler.Calls[1].Pin);
    }

    sealed class ScriptedHandler : HttpMessageHandler
    {
        public List<(string Uri, string? Pin)> Calls { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var pin = request.Headers.TryGetValues(RemoteApi.PinHeader, out var values)
                ? string.Join(",", values)
                : null;
            Calls.Add((request.RequestUri!.AbsoluteUri, pin));
            var title = Calls.Count == 1 ? "Shake It Up" : "Other Song";
            var json = StatusJson(title);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }

        static string StatusJson(string title) =>
            "{\"ok\":true,\"version\":\"1\",\"groups\":[],\"offline\":[],\"wakeActive\":false,\"busy\":false,"
            + "\"nowPlaying\":{\"title\":\"" + title + "\",\"state\":\"PLAYING\",\"hasArt\":false,\"artRevision\":\"\"}}";
    }
}
