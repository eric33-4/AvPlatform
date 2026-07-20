using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AvPlatform.WebApi.Channels;

/// <summary>封装 YXFM 的加密请求、响应解密和节点故障切换。</summary>
public sealed class YxfmApiClient(
    HttpClient httpClient,
    IConfiguration configuration,
    ILogger<YxfmApiClient> logger)
{
    private static readonly byte[] Key = Encoding.ASCII.GetBytes("Af234dfdf0io@#$*");
    private static readonly byte[] Iv = Encoding.ASCII.GetBytes("1234567890123456");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<JsonDocument> PostAsync(object payload, CancellationToken cancellationToken)
    {
        var endpoints = configuration.GetSection("Channels:Yxfm:Endpoints").Get<string[]>() ?? [];
        if (endpoints.Length == 0)
        {
            throw new InvalidOperationException("未配置 YXFM API 节点。");
        }

        Exception? lastError = null;
        foreach (var endpoint in endpoints)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var request = CreateRequest(endpoint, payload);
                using var response = await httpClient.SendAsync(request, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                response.EnsureSuccessStatusCode();

                var document = Decrypt(body);
                if (!IsSuccess(document.RootElement))
                {
                    document.Dispose();
                    throw new InvalidOperationException("YXFM 上游返回业务失败。");
                }

                logger.LogInformation(
                    "YXFM 节点调用成功：{Host}，状态码：{StatusCode}，耗时：{ElapsedMilliseconds}ms",
                    new Uri(endpoint).Host,
                    (int)response.StatusCode,
                    stopwatch.ElapsedMilliseconds);
                return document;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                lastError = exception;
                logger.LogWarning(
                    exception,
                    "YXFM 节点调用失败：{Host}，耗时：{ElapsedMilliseconds}ms",
                    new Uri(endpoint).Host,
                    stopwatch.ElapsedMilliseconds);
            }
        }

        throw new HttpRequestException("所有 YXFM API 节点均调用失败。", lastError);
    }

    internal static string Encrypt(object payload)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        var paddingLength = (16 - plaintext.Length % 16) % 16;
        var padded = new byte[plaintext.Length + paddingLength];
        plaintext.CopyTo(padded, 0);

        using var aes = Aes.Create();
        aes.Key = Key;
        aes.IV = Iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        using var encryptor = aes.CreateEncryptor();
        return Convert.ToBase64String(encryptor.TransformFinalBlock(padded, 0, padded.Length));
    }

    internal static JsonDocument Decrypt(string encrypted)
    {
        var cipher = Convert.FromBase64String(encrypted.Trim());
        using var aes = Aes.Create();
        aes.Key = Key;
        aes.IV = Iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        using var decryptor = aes.CreateDecryptor();
        var plaintext = decryptor.TransformFinalBlock(cipher, 0, cipher.Length);
        var length = plaintext.Length;
        while (length > 0 && plaintext[length - 1] == 0)
        {
            length--;
        }

        return JsonDocument.Parse(plaintext.AsMemory(0, length));
    }

    private static HttpRequestMessage CreateRequest(string endpoint, object payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["request"] = Encrypt(payload)
            })
        };
        request.Headers.TryAddWithoutValidation("APPUID", string.Empty);
        request.Headers.TryAddWithoutValidation("APPTOKEN", string.Empty);
        request.Headers.TryAddWithoutValidation("APPIMAGE", "100");
        request.Headers.TryAddWithoutValidation("PACKAGENAME", "com.bbs.radio.web");
        request.Headers.TryAddWithoutValidation("VERSIONCODE", "19X2");
        request.Headers.TryAddWithoutValidation("VERSIONNAME", "1.0.0");
        request.Headers.TryAddWithoutValidation("DOMAIN", string.Empty);
        return request;
    }

    private static bool IsSuccess(JsonElement root) =>
        root.TryGetProperty("status", out var status) &&
        ((status.ValueKind == JsonValueKind.Number && status.TryGetInt32(out var number) && number == 1) ||
         (status.ValueKind == JsonValueKind.String && status.GetString() == "1"));
}
