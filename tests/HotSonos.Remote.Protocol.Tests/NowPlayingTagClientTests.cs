using System.Net;
using System.Text;
using HotSonos.Remote;
using HotSonos.Remote.Protocol;

namespace HotSonos.Remote.Protocol.Tests;

public class NowPlayingTagClientTests
{
    [Fact]
    public async Task NowTags_sends_the_pin_on_get_and_post()
    {
        var handler = new RecordingHandler();
        using var client = new RemoteClient(handler);
        client.Apply("192.168.1.20", 42342, "654321");

        var page = await client.GetNowTagsAsync(CancellationToken.None);
        var toggle = await client.ToggleNowTagAsync("dinner", CancellationToken.None);

        Assert.Equal(RemoteCallKind.Ok, page.Kind);
        Assert.True(page.Value?.CanTag);
        Assert.Equal("Dinner", page.Value?.Tags[0].Label);
        Assert.True(page.Value?.Tags[0].On);
        Assert.Equal(RemoteCallKind.Ok, toggle.Kind);
        Assert.Equal("Tag “Dinner” on", toggle.Value?.Message);

        Assert.Equal("http://192.168.1.20:42342/api/now/tags", handler.Calls[0].Uri);
        Assert.Equal(HttpMethod.Get, handler.Calls[0].Method);
        Assert.Equal("654321", handler.Calls[0].Pin);
        Assert.Equal(HttpMethod.Post, handler.Calls[1].Method);
        Assert.Equal("654321", handler.Calls[1].Pin);
        Assert.Contains("\"key\":\"dinner\"", handler.Calls[1].Body);
    }

    sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(string Uri, HttpMethod Method, string? Pin, string? Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var pin = request.Headers.TryGetValues(RemoteApi.PinHeader, out var values)
                ? string.Join(",", values)
                : null;
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add((request.RequestUri!.AbsoluteUri, request.Method, pin, body));
            var json = request.Method == HttpMethod.Get
                ? """{"ok":true,"canTag":true,"path":"\\\\nas\\a.flac","message":null,"tags":[{"key":"dinner","label":"Dinner","on":true}]}"""
                : """{"ok":true,"message":"Tag \u201cDinner\u201d on","error":null}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }
    }
}
