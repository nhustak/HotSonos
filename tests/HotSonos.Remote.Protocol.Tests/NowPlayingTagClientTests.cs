using System.Net;
using System.Text;
using HotSonos.Remote;
using HotSonos.Remote.Protocol;

namespace HotSonos.Remote.Protocol.Tests;

public class NowPlayingTagClientTests
{
    [Fact]
    public async Task Now_tag_routes_send_the_house_pin()
    {
        var handler = new RecordingHandler("""{"ok":true,"canTag":true,"path":"\\\\nas\\a.flac","title":"Song","tags":[{"key":"dinner","label":"Dinner","on":false}]}""");
        using var client = new RemoteClient(handler);
        client.Apply("192.168.1.20", 42342, "654321");

        var listed = await client.GetNowTagsAsync(CancellationToken.None);
        Assert.Equal(RemoteCallKind.Ok, listed.Kind);
        Assert.True(listed.Value?.CanTag);
        Assert.Equal("Dinner", listed.Value?.Tags[0].Label);
        Assert.Equal("GET", handler.Calls[0].Method);
        Assert.Equal("http://192.168.1.20:42342/api/now/tags", handler.Calls[0].Uri);
        Assert.Equal("654321", handler.Calls[0].Pin);

        handler.Json = """{"ok":true,"message":"Tag “Dinner” on","error":null}""";
        var toggled = await client.ToggleNowTagAsync("dinner", CancellationToken.None);
        Assert.Equal(RemoteCallKind.Ok, toggled.Kind);
        Assert.Equal("POST", handler.Calls[1].Method);
        Assert.Equal("http://192.168.1.20:42342/api/now/tags", handler.Calls[1].Uri);
        Assert.Contains("dinner", handler.Calls[1].Body, StringComparison.Ordinal);
        Assert.Equal("654321", handler.Calls[1].Pin);
    }

    sealed class RecordingHandler : HttpMessageHandler
    {
        public string Json;
        public List<(string Method, string Uri, string? Pin, string Body)> Calls { get; } = new();

        public RecordingHandler(string json) => Json = json;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var pin = request.Headers.TryGetValues(RemoteApi.PinHeader, out var values)
                ? string.Join(",", values)
                : null;
            var body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            Calls.Add((request.Method.Method, request.RequestUri!.AbsoluteUri, pin, body));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Json, Encoding.UTF8, "application/json"),
            };
        }
    }
}
