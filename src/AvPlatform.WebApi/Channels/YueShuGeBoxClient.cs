using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace AvPlatform.WebApi.Channels;

/// <summary>调用阅姝阁 Box 公共解析接口。</summary>
public sealed class YueShuGeBoxClient(
    HttpClient httpClient,
    IConfiguration configuration,
    ILogger<YueShuGeBoxClient> logger)
{
    public async Task<string?> ResolveVideoUrlAsync(
        string sourcePageUrl,
        CancellationToken cancellationToken)
    {
        var endpoints = configuration.GetSection("Channels:Box:Endpoints").Get<string[]>() ?? [];
        Exception? lastError = null;
        foreach (var endpoint in endpoints.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            var stopwatch = Stopwatch.StartNew();
            var statusCode = 0;
            try
            {
                var requestUri = BuildUri(endpoint, "box/api/1024/video/url", sourcePageUrl);
                using var response = await httpClient.GetAsync(requestUri, cancellationToken);
                statusCode = (int)response.StatusCode;
                response.EnsureSuccessStatusCode();
                var encrypted = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                using var envelope = YueShuGeBoxCodec.Decrypt(encrypted);
                var root = envelope.RootElement;
                if (!root.TryGetProperty("code", out var code) || !code.TryGetInt32(out var number) || number != 0 ||
                    !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String)
                {
                    throw new JsonException("Box 视频解析响应无效。");
                }

                using var payload = JsonDocument.Parse(data.GetString()!);
                var url = payload.RootElement.TryGetProperty("url", out var urlElement)
                    ? urlElement.GetString()
                    : null;
                logger.LogInformation(
                    "Box 视频解析成功：{Host}，状态码：{StatusCode}，耗时：{ElapsedMilliseconds}ms",
                    requestUri.Host,
                    statusCode,
                    stopwatch.ElapsedMilliseconds);
                return string.IsNullOrWhiteSpace(url) ? null : url;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                lastError = exception;
                logger.LogWarning(
                    exception,
                    "Box 视频解析失败：{Host}，状态码：{StatusCode}，耗时：{ElapsedMilliseconds}ms",
                    Host(endpoint),
                    statusCode == 0 ? null : statusCode,
                    stopwatch.ElapsedMilliseconds);
            }
        }

        throw new HttpRequestException("所有 Box 视频解析节点均调用失败。", lastError);
    }

    private static Uri BuildUri(string endpoint, string path, string sourcePageUrl)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("Box API 节点地址无效。");
        }

        var normalized = baseUri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? baseUri
            : new Uri(baseUri.AbsoluteUri + '/', UriKind.Absolute);
        var url = QueryHelpers.AddQueryString(new Uri(normalized, path).ToString(), "url", sourcePageUrl);
        return new Uri(url, UriKind.Absolute);
    }

    private static string Host(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? uri.Host : "invalid-endpoint";
}
