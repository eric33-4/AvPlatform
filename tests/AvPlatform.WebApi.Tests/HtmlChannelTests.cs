using System.Net;
using System.Text;
using System.Text.Json;
using AvPlatform.WebApi.Channels;
using AvPlatform.WebApi.Models;
using AvPlatform.WebApi.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AvPlatform.WebApi.Tests;

[TestClass]
public sealed class HtmlChannelTests
{
    private readonly TestContext _testContext;

    public HtmlChannelTests(TestContext testContext) => _testContext = testContext;

    [TestMethod]
    public void EncodePath_WithEllipsisSlug_RoundTrips()
    {
        const string path = "/video.foo123/49896676/0/title_with_ellipsis_..._action_?from=home";

        var decoded = HtmlChannelUtilities.DecodePath(HtmlChannelUtilities.EncodePath(path));

        Assert.AreEqual(path, decoded);
    }

    [TestMethod]
    public void DecodePath_WithTraversalSegment_ThrowsFormatException()
    {
        var encoded = HtmlChannelUtilities.EncodePath("/videos/../private/file");

        Assert.ThrowsExactly<FormatException>(() => HtmlChannelUtilities.DecodePath(encoded));
    }

    [TestMethod]
    public void DecodePath_WithEncodedTraversalSegment_ThrowsFormatException()
    {
        var encoded = HtmlChannelUtilities.EncodePath("/videos/%2e%2e/private/file");

        Assert.ThrowsExactly<FormatException>(() => HtmlChannelUtilities.DecodePath(encoded));
    }

    [TestMethod]
    public void UnpackFirstMediaUrl_WithPackedScript_ReturnsHlsUrl()
    {
        const string html = """
            <script>
            eval(function(p,a,c,k,e,d){return p;}('0=\'1\'',10,2,'source|https://media.example/video.m3u8'.split('|'),0,{}))
            </script>
            """;

        var source = HtmlChannelUtilities.UnpackFirstMediaUrl(html);

        Assert.AreEqual("https://media.example/video.m3u8", source);
    }

    [TestMethod]
    public async Task XvideosGetPlay_WithHlsScript_ExtractsVideoSource()
    {
        const string html = """
            <html><head><meta property="og:title" content="测试视频"></head><body>
            <script>html5player.setVideoHLS('https:\/\/cdn.example\/video\/hls.m3u8');</script>
            </body></html>
            """;
        using var httpClient = CreateHttpClient(html, "https://www.xvideos.com/");
        var adapter = new XvideosChannelAdapter(httpClient);

        var source = await adapter.GetPlayAsync(
            HtmlChannelUtilities.EncodePath("/video.example/123/legal_slug_..._end"),
            "main",
            CancellationToken.None);

        Assert.IsNotNull(source);
        Assert.AreEqual("https://cdn.example/video/hls.m3u8", source.SourceUrl);
        Assert.AreEqual("application/vnd.apple.mpegurl", source.MediaType);
        Assert.AreEqual("video", source.MediaKind);
        Assert.AreEqual("http", source.Transport);
    }

    [TestMethod]
    public async Task MissAvGetPlay_WithPackedScript_UsesCurlTransportAndReferrer()
    {
        const string html = """
            <html><head><meta property="og:title" content="测试视频"></head><body>
            <script>eval(function(p,a,c,k,e,d){return p;}('0=\'1\'',10,2,'source|https://surrit.com/media/playlist.m3u8'.split('|'),0,{}))</script>
            </body></html>
            """;
        using var httpClient = CreateHttpClient(html);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Channels:MissAv:Endpoints:0"] = "https://missav.ws/"
            })
            .Build();
        var adapter = new MissAvChannelAdapter(
            httpClient,
            configuration,
            NullLogger<MissAvChannelAdapter>.Instance);

        var source = await adapter.GetPlayAsync(
            HtmlChannelUtilities.EncodePath("/en/test-001"),
            "main",
            CancellationToken.None);

        Assert.IsNotNull(source);
        Assert.AreEqual("curl", source.Transport);
        Assert.AreEqual("https://missav.ws/en/test-001", source.ReferrerUrl);
        Assert.AreEqual("video", source.MediaKind);
    }

    [TestMethod]
    public void RewritePlaylist_WithNestedPlaylistKeyAndSegment_RewritesEveryResource()
    {
        const string playlist = """
            #EXTM3U
            #EXT-X-KEY:METHOD=AES-128,URI="key.bin"
            #EXT-X-STREAM-INF:BANDWIDTH=800000
            640x360/video.m3u8
            #EXTINF:4,
            video0.ts
            """;
        var playlistUri = new Uri("https://cdn.example/root/master.m3u8");
        const string publicPath = "/api/channels/test/items/1/episodes/main/stream";

        var rewritten = ChannelMediaProxy.RewritePlaylist(playlist, playlistUri, publicPath);

        Assert.Contains(
            $"URI=\"{publicPath}/resources/{ChannelMediaProxy.EncodeResource(new Uri(playlistUri, "key.bin"))}\"",
            rewritten);
        Assert.Contains(
            $"{publicPath}/resources/{ChannelMediaProxy.EncodeResource(new Uri(playlistUri, "640x360/video.m3u8"))}",
            rewritten);
        Assert.Contains(
            $"{publicPath}/resources/{ChannelMediaProxy.EncodeResource(new Uri(playlistUri, "video0.ts"))}",
            rewritten);
    }

    [TestMethod]
    public async Task ProxyPlaylist_WithProtectedResourceToken_BindsTokenToStreamPath()
    {
        const string playlist = "#EXTM3U\n#EXTINF:4,\nvideo0.ts\n";
        const string sourceUrl = "https://cdn.example/root/video.m3u8";
        const string publicPath = "/api/channels/test/items/1/episodes/main/stream";
        using var httpClient = CreateHttpClient(playlist);
        var proxy = new ChannelMediaProxy(
            new StaticHttpClientFactory(httpClient),
            new EphemeralDataProtectionProvider(),
            NullLogger<ChannelMediaProxy>.Instance);
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await proxy.ProxyPlaylistAsync(
            context,
            sourceUrl,
            publicPath,
            null,
            "http",
            _testContext.CancellationToken);
        context.Response.Body.Position = 0;
        var rewritten = await new StreamReader(context.Response.Body)
            .ReadToEndAsync(_testContext.CancellationToken);
        var resourcePath = rewritten.Split('\n', StringSplitOptions.RemoveEmptyEntries)[2];
        var token = resourcePath[(resourcePath.LastIndexOf('/') + 1)..];

        Assert.IsTrue(proxy.TryDecodeResource(token, publicPath, out var resourceUri));
        Assert.AreEqual("https://cdn.example/root/video0.ts", resourceUri.ToString());
        Assert.IsFalse(proxy.TryDecodeResource(token, publicPath + "/other", out _));
    }

    [TestMethod]
    public void ChannelPlayResponse_WithVideoKind_SerializesMediaKind()
    {
        var response = new ChannelPlayResponse(
            "xvideos",
            "item",
            "main",
            "title",
            "/stream",
            "application/vnd.apple.mpegurl",
            "video");

        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"mediaKind\":\"video\"", json);
    }

    private static HttpClient CreateHttpClient(string html, string? baseAddress = null)
    {
        var client = new HttpClient(new StaticHtmlHandler(html));
        if (baseAddress is not null)
        {
            client.BaseAddress = new Uri(baseAddress);
        }

        return client;
    }

    private sealed class StaticHtmlHandler(string html) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(html, Encoding.UTF8, "text/html")
            });
    }

    private sealed class StaticHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => httpClient;
    }
}
