using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AvPlatform.WebApi.Channels;

/// <summary>封装 GDAPI 系列接口的签名、加解密和节点故障切换。</summary>
public sealed class GdApiClient(
    HttpClient httpClient,
    YueShuGeConfigProvider configProvider,
    IConfiguration configuration,
    ILogger<GdApiClient> logger)
{
    private const string RequestSuffix = "NWSdef";
    private const string ResponseIvPrefix = "RWf23muavY";
    private const string SignSalt = "&NRkw0g3iJLDvw5tJ5PuVt5276z0SOuyL";
    private static readonly byte[] Key = Encoding.ASCII.GetBytes("0XxdjmI55ZjjqQLO3nI7gGqrBP0Vz9jS");
    private static readonly byte[] RequestIv = Encoding.ASCII.GetBytes("RWf23muavYNWSdef");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<JsonDocument> PostAsync(
        string path,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken) =>
        PostAsync(
            new GdApiChannelProfile("AIJAV", "aijav", "AiJav"),
            path,
            parameters,
            cancellationToken);

    internal async Task<JsonDocument> PostAsync(
        GdApiChannelProfile profile,
        string path,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken)
    {
        var snapshot = await configProvider.GetSnapshotAsync(false, cancellationToken);
        var endpoints = new[] { snapshot.Hosts.GetValueOrDefault(profile.HostName) }
            .Concat(configuration.GetSection($"Channels:{profile.ConfigurationName}:Endpoints").Get<string[]>() ?? [])
            .Where(endpoint => !string.IsNullOrWhiteSpace(endpoint))
            .Select(endpoint => profile.NormalizeEndpoint?.Invoke(endpoint!) ?? endpoint!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (endpoints.Length == 0)
        {
            throw new InvalidOperationException($"未配置 {profile.DisplayName} API 节点。");
        }

        Exception? lastError = null;
        foreach (var endpoint in endpoints)
        {
            var stopwatch = Stopwatch.StartNew();
            var statusCode = 0;
            try
            {
                var requestUri = BuildRequestUri(endpoint, path);
                using var request = CreateRequest(requestUri, parameters);
                using var response = await httpClient.SendAsync(request, cancellationToken);
                statusCode = (int)response.StatusCode;
                response.EnsureSuccessStatusCode();

                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                var document = Decrypt(body);
                if (!IsSuccess(document.RootElement))
                {
                    document.Dispose();
                    throw new InvalidOperationException($"{profile.DisplayName} 上游返回业务失败。");
                }

                logger.LogInformation(
                    "{Channel} 节点调用成功：{Host}，状态码：{StatusCode}，耗时：{ElapsedMilliseconds}ms",
                    profile.DisplayName,
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
                    "{Channel} 节点调用失败：{Host}，状态码：{StatusCode}，耗时：{ElapsedMilliseconds}ms",
                    profile.DisplayName,
                    Host(endpoint),
                    statusCode == 0 ? null : statusCode,
                    stopwatch.ElapsedMilliseconds);
            }
        }

        throw new HttpRequestException($"所有 {profile.DisplayName} API 节点均调用失败。", lastError);
    }

    internal static string Encrypt(
        IReadOnlyDictionary<string, object?> parameters,
        long? timestamp = null)
    {
        var signed = new Dictionary<string, object?>(parameters, StringComparer.Ordinal)
        {
            ["timestamp"] = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        var signSource = string.Join(
            '&',
            signed.OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => $"{Uri.EscapeDataString(item.Key)}={SignValue(item.Value)}"));
        signed["encode_sign"] = Convert.ToHexString(
                MD5.HashData(Encoding.UTF8.GetBytes(signSource + SignSalt)))
            .ToLowerInvariant();

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(signed, JsonOptions);
        using var aes = Aes.Create();
        aes.Key = Key;
        aes.IV = RequestIv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using var encryptor = aes.CreateEncryptor();
        return Convert.ToBase64String(encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length));
    }

    internal static JsonDocument Decrypt(string envelopeJson)
    {
        using var envelope = JsonDocument.Parse(envelopeJson);
        var root = envelope.RootElement;
        var suffix = root.TryGetProperty("suffix", out var suffixElement)
            ? suffixElement.GetString()
            : null;
        var encrypted = root.TryGetProperty("data", out var dataElement)
            ? dataElement.GetString()
            : null;
        if (suffix is null || suffix.Length != 6 || suffix.Any(character => character > 127) ||
            string.IsNullOrWhiteSpace(encrypted))
        {
            throw new JsonException("GDAPI 响应信封无效。");
        }

        var cipher = Convert.FromBase64String(encrypted);
        using var aes = Aes.Create();
        aes.Key = Key;
        aes.IV = Encoding.ASCII.GetBytes(ResponseIvPrefix + suffix);
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using var decryptor = aes.CreateDecryptor();
        var plaintext = decryptor.TransformFinalBlock(cipher, 0, cipher.Length);
        return JsonDocument.Parse(plaintext);
    }

    private static HttpRequestMessage CreateRequest(
        Uri requestUri,
        IReadOnlyDictionary<string, object?> parameters)
    {
        var envelope = JsonSerializer.Serialize(
            new Dictionary<string, string> { ["post-data"] = Encrypt(parameters) },
            JsonOptions);
        var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = new StringContent(envelope, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("suffix", RequestSuffix);
        return request;
    }

    private static Uri BuildRequestUri(string endpoint, string path)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("GDAPI 节点地址无效。");
        }

        var normalizedBase = baseUri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? baseUri
            : new Uri(baseUri.AbsoluteUri + '/', UriKind.Absolute);
        return new Uri(normalizedBase, path.TrimStart('/'));
    }

    private static string SignValue(object? value) => value switch
    {
        null => string.Empty,
        bool boolean => boolean ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };

    private static bool IsSuccess(JsonElement root) =>
        root.TryGetProperty("code", out var code) &&
        ((code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var number) && number == 1) ||
         (code.ValueKind == JsonValueKind.String && code.GetString() == "1"));

    private static string Host(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? uri.Host : "invalid-endpoint";
}

/// <summary>GDAPI 同协议渠道的节点数据。</summary>
internal sealed record GdApiChannelProfile(
    string DisplayName,
    string HostName,
    string ConfigurationName,
    Func<string, string>? NormalizeEndpoint = null);
