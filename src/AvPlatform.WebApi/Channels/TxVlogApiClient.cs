using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AvPlatform.WebApi.Channels;

/// <summary>封装 TXVLOG 的 AES-ECB JSON 信封和节点切换。</summary>
public sealed class TxVlogApiClient(
    HttpClient httpClient,
    YueShuGeConfigProvider configProvider,
    IConfiguration configuration,
    ILogger<TxVlogApiClient> logger)
{
    private const string UserAgent = "Mozilla/5.0 (iPhone; CPU iPhone OS 14_6 like Mac OS X) " +
                                     "AppleWebKit/605.1.15 (KHTML, like Gecko) Version/14.0.3 " +
                                     "Mobile/15E148 Safari/604.1";
    private static readonly byte[] Key = Encoding.ASCII.GetBytes("fd14f9f8e38808fa");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<TxVlogApiResponse> PostAsync(
        string path,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken)
    {
        var snapshot = await configProvider.GetSnapshotAsync(false, cancellationToken);
        var token = snapshot.Tokens.GetValueOrDefault("token_txvlog") ??
                    configuration.GetValue<string>("Channels:TxVlog:Token") ?? string.Empty;
        var endpoints = Endpoints(snapshot.Hosts.GetValueOrDefault("tx_video"));

        Exception? lastError = null;
        foreach (var endpoint in endpoints)
        {
            var stopwatch = Stopwatch.StartNew();
            var statusCode = 0;
            try
            {
                var baseUri = new Uri(EnsureTrailingSlash(endpoint));
                var requestUri = new Uri(baseUri, path.TrimStart('/'));
                using var request = CreateRequest(requestUri, parameters, token);
                using var response = await httpClient.SendAsync(request, cancellationToken);
                statusCode = (int)response.StatusCode;
                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                var document = Decrypt(body);
                if (!IsSuccess(document.RootElement))
                {
                    document.Dispose();
                    throw new InvalidOperationException("TXVLOG 上游返回业务失败。");
                }

                logger.LogInformation(
                    "TXVLOG 节点调用成功：{Host}，状态码：{StatusCode}，耗时：{ElapsedMilliseconds}ms",
                    requestUri.Host,
                    statusCode,
                    stopwatch.ElapsedMilliseconds);
                return new TxVlogApiResponse(document, baseUri);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                lastError = exception;
                logger.LogWarning(
                    exception,
                    "TXVLOG 节点调用失败：{Host}，状态码：{StatusCode}，耗时：{ElapsedMilliseconds}ms",
                    Host(endpoint),
                    statusCode == 0 ? null : statusCode,
                    stopwatch.ElapsedMilliseconds);
            }
        }

        throw new HttpRequestException("所有 TXVLOG API 节点均调用失败。", lastError);
    }

    internal static string Encrypt(
        IReadOnlyDictionary<string, object?> parameters,
        string token)
    {
        var envelope = new Dictionary<string, object?>
        {
            ["token"] = token,
            ["deviceId"] = "web_683e842e44767",
            ["language"] = "zh",
            ["device"] = "web",
            ["source"] = "Apple Computer, Inc.",
            ["driver"] = false,
            ["data"] = parameters
        };
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
        using var aes = CreateAes();
        using var encryptor = aes.CreateEncryptor();
        return Convert.ToBase64String(encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length));
    }

    internal static JsonDocument Decrypt(string body)
    {
        var cipher = Convert.FromBase64String(body.Trim());
        using var aes = CreateAes();
        using var decryptor = aes.CreateDecryptor();
        var plaintext = decryptor.TransformFinalBlock(cipher, 0, cipher.Length);
        return JsonDocument.Parse(plaintext);
    }

    private static HttpRequestMessage CreateRequest(
        Uri requestUri,
        IReadOnlyDictionary<string, object?> parameters,
        string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = new StringContent(Encrypt(parameters, token), Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("deviceType", "web");
        request.Headers.TryAddWithoutValidation(
            "time",
            DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("Cookie", "_c=ozxxoo3; language=zh");
        request.Headers.TryAddWithoutValidation("version", "1.0.0");
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        return request;
    }

    private string[] Endpoints(string? dynamicEndpoint)
    {
        var configured = configuration.GetSection("Channels:TxVlog:Endpoints").Get<string[]>() ?? [];
        return configured.Concat([dynamicEndpoint])
            .Where(endpoint => !string.IsNullOrWhiteSpace(endpoint))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToArray();
    }

    private static Aes CreateAes()
    {
        var aes = Aes.Create();
        aes.Key = Key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        return aes;
    }

    private static bool IsSuccess(JsonElement root) =>
        root.TryGetProperty("status", out var status) && status.GetString() == "y";

    private static string EnsureTrailingSlash(string endpoint) =>
        endpoint.EndsWith("/", StringComparison.Ordinal) ? endpoint : endpoint + "/";

    private static string Host(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? uri.Host : "invalid-endpoint";
}

public sealed class TxVlogApiResponse(JsonDocument document, Uri baseUri) : IDisposable
{
    public JsonDocument Document { get; } = document;
    public Uri BaseUri { get; } = baseUri;
    public void Dispose() => Document.Dispose();
}
