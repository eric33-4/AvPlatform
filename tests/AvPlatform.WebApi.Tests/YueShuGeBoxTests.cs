using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using AvPlatform.WebApi.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AvPlatform.WebApi.Tests;

[TestClass]
public sealed class YueShuGeBoxTests
{
    private static readonly byte[] Key = Encoding.ASCII.GetBytes("dnf45as45fs1ace1");
    private static readonly byte[] Iv = Encoding.ASCII.GetBytes("dn5as4fs1ac5f4e1");
    private readonly TestContext _testContext;

    public YueShuGeBoxTests(TestContext testContext) => _testContext = testContext;

    [TestMethod]
    public void Decrypt_WithEncryptedZlibEnvelope_ReturnsJsonDocument()
    {
        const string json = """
            {"code":0,"data":"{\"url\":\"https://media.example/video.m3u8\"}"}
            """;

        using var document = YueShuGeBoxCodec.Decrypt(Encrypt(json));

        Assert.AreEqual(0, document.RootElement.GetProperty("code").GetInt32());
        var data = document.RootElement.GetProperty("data").GetString();
        Assert.IsNotNull(data);
        Assert.Contains("video.m3u8", data);
    }

    [TestMethod]
    [Timeout(5000, CooperativeCancellation = true)]
    public async Task ResolveVideoUrlAsync_FirstNodeFails_UsesNextNodeAndEncodesSourceUrl()
    {
        const string sourceUrl = "https://rryy.example/video/123?from=home&quality=hd";
        const string payload = """
            {"code":0,"data":"{\"url\":\"https://media.example/rryy/master.m3u8\"}"}
            """;
        var handler = new SequentialHandler(Encrypt(payload));
        using var client = new HttpClient(handler);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Channels:Box:Endpoints:0"] = "https://box-a.example/",
                ["Channels:Box:Endpoints:1"] = "https://box-b.example/base"
            })
            .Build();
        var boxClient = new YueShuGeBoxClient(
            client,
            configuration,
            NullLogger<YueShuGeBoxClient>.Instance);

        var result = await boxClient.ResolveVideoUrlAsync(sourceUrl, _testContext.CancellationToken);

        Assert.AreEqual("https://media.example/rryy/master.m3u8", result);
        Assert.HasCount(2, handler.Requests);
        Assert.AreEqual("box-a.example", handler.Requests[0].Host);
        Assert.AreEqual("box-b.example", handler.Requests[1].Host);
        Assert.AreEqual("/base/box/api/1024/video/url", handler.Requests[1].AbsolutePath);
        Assert.Contains("url=https%3A%2F%2Frryy.example%2Fvideo%2F123%3Ffrom%3Dhome%26quality%3Dhd", handler.Requests[1].Query);
    }

    private static byte[] Encrypt(string json)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, true))
        {
            zlib.Write(Encoding.UTF8.GetBytes(json));
        }

        using var aes = Aes.Create();
        aes.Key = Key;
        aes.IV = Iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using var encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(compressed.ToArray(), 0, checked((int)compressed.Length));
    }

    private sealed class SequentialHandler(byte[] encrypted) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var requestUri = request.RequestUri;
            Assert.IsNotNull(requestUri);
            Requests.Add(requestUri);
            if (Requests.Count == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)
                {
                    RequestMessage = request
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(encrypted)
            });
        }
    }
}
