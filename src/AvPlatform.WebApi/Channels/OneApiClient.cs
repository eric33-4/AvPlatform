using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AvPlatform.WebApi.Channels;

/// <summary>封装 ONE 的双 MD5 签名、AES-CBC 表单加密和响应解密。</summary>
public sealed class OneApiClient(
    HttpClient httpClient,
    YueShuGeConfigProvider configProvider,
    IConfiguration configuration,
    ILogger<OneApiClient> logger)
{
    private const string Uuid = "48b067ec-6cfd-3491-84f5-023eb1e7d562";
    private const string UserKey = "563e8eeef42931cc858dc0d1080f4f6f";
    private const string SignSalt = "m4n2hjPeYWkD6tFpqKF^3HO^h24P@idT";
    private static readonly byte[] Key = Encoding.ASCII.GetBytes("l*bv%Ziq000Biaog");
    private static readonly byte[] Iv = Encoding.ASCII.GetBytes("8597506002939249");

    public async Task<JsonDocument> PostAsync(
        string path,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken)
    {
        var snapshot = await configProvider.GetSnapshotAsync(false, cancellationToken);
        var token = snapshot.Tokens.GetValueOrDefault("token_one") ??
                    configuration.GetValue<string>("Channels:One:Token") ?? string.Empty;
        var endpoints = Endpoints(snapshot.Hosts.GetValueOrDefault("one"));

        Exception? lastError = null;
        foreach (var endpoint in endpoints)
        {
            var stopwatch = Stopwatch.StartNew();
            var statusCode = 0;
            try
            {
                var requestUri = new Uri(new Uri(EnsureTrailingSlash(endpoint)), path.TrimStart('/'));
                using var request = CreateRequest(requestUri, parameters, token);
                using var response = await httpClient.SendAsync(request, cancellationToken);
                statusCode = (int)response.StatusCode;
                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                var document = Decrypt(body);
                if (!IsSuccess(document.RootElement))
                {
                    document.Dispose();
                    throw new InvalidOperationException("ONE 上游返回业务失败。");
                }

                logger.LogInformation(
                    "ONE 节点调用成功：{Host}，状态码：{StatusCode}，耗时：{ElapsedMilliseconds}ms",
                    requestUri.Host,
                    statusCode,
                    stopwatch.ElapsedMilliseconds);
                return document;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                lastError = exception;
                logger.LogWarning(
                    exception,
                    "ONE 节点调用失败：{Host}，状态码：{StatusCode}，耗时：{ElapsedMilliseconds}ms",
                    Host(endpoint),
                    statusCode == 0 ? null : statusCode,
                    stopwatch.ElapsedMilliseconds);
            }
        }

        throw new HttpRequestException("所有 ONE API 节点均调用失败。", lastError);
    }

    internal static string Encrypt(IReadOnlyDictionary<string, object?> parameters)
    {
        var plaintext = string.Join(
            '&',
            parameters.OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => $"{item.Key}={Value(item.Value)}"));
        return EncryptText(plaintext);
    }

    internal static JsonDocument Decrypt(string body)
    {
        var trimmed = body.Trim();
        if (trimmed.StartsWith('{'))
        {
            return JsonDocument.Parse(trimmed);
        }

        var cipher = Convert.FromBase64String(trimmed);
        using var aes = CreateAes();
        using var decryptor = aes.CreateDecryptor();
        var plaintext = decryptor.TransformFinalBlock(cipher, 0, cipher.Length);
        return JsonDocument.Parse(plaintext);
    }

    private HttpRequestMessage CreateRequest(
        Uri requestUri,
        IReadOnlyDictionary<string, object?> parameters,
        string token)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var first = Md5($"0.0.0.0.3.{timestamp}.{UserKey}.{Uuid}");
        var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = new StringContent(Encrypt(parameters), Encoding.UTF8, "application/x-www-form-urlencoded")
        };
        request.Headers.TryAddWithoutValidation("uuid", Uuid);
        request.Headers.TryAddWithoutValidation("user-key", UserKey);
        request.Headers.TryAddWithoutValidation("token", token);
        request.Headers.TryAddWithoutValidation("timestamp", timestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("platform", "3");
        request.Headers.TryAddWithoutValidation("ip", "0.0.0.0");
        request.Headers.TryAddWithoutValidation("app-version", "2.6.3.1");
        request.Headers.TryAddWithoutValidation("sign", Md5(first + SignSalt));
        return request;
    }

    private string[] Endpoints(string? dynamicEndpoint)
    {
        var configured = configuration.GetSection("Channels:One:Endpoints").Get<string[]>() ?? [];
        return new[] { dynamicEndpoint }
            .Concat(configured)
            .Where(endpoint => !string.IsNullOrWhiteSpace(endpoint))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToArray();
    }

    private static string EncryptText(string plaintext)
    {
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        using var aes = CreateAes();
        using var encryptor = aes.CreateEncryptor();
        return Convert.ToBase64String(encryptor.TransformFinalBlock(bytes, 0, bytes.Length));
    }

    private static Aes CreateAes()
    {
        var aes = Aes.Create();
        aes.Key = Key;
        aes.IV = Iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        return aes;
    }

    private static string Value(object? value) => value switch
    {
        null => string.Empty,
        bool boolean => boolean ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };

    private static string Md5(string value) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static bool IsSuccess(JsonElement root) =>
        root.TryGetProperty("code", out var code) && code.TryGetInt32(out var value) && value == 200;

    private static string EnsureTrailingSlash(string endpoint) =>
        endpoint.EndsWith("/", StringComparison.Ordinal) ? endpoint : endpoint + "/";

    private static string Host(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? uri.Host : "invalid-endpoint";
}
