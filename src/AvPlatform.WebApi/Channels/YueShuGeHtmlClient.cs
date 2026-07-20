using System.Diagnostics;
using System.Text.Json;

namespace AvPlatform.WebApi.Channels;

/// <summary>读取阅姝阁动态 HTML 渠道，统一处理节点切换、Cookie 和浏览器请求头。</summary>
public sealed class YueShuGeHtmlClient(
    HttpClient httpClient,
    YueShuGeConfigProvider configProvider,
    IConfiguration configuration,
    ILogger<YueShuGeHtmlClient> logger)
{
    internal async Task<HtmlChannelPage> LoadAsync(
        YueShuGeHtmlChannelProfile profile,
        string path,
        CancellationToken cancellationToken)
    {
        var snapshot = await configProvider.GetSnapshotAsync(false, cancellationToken);
        var endpoints = Endpoints(profile, snapshot);
        var cookie = profile.TokenName is null
            ? null
            : snapshot.Tokens.GetValueOrDefault(profile.TokenName);
        Exception? lastError = null;
        foreach (var endpoint in endpoints)
        {
            try
            {
                var uri = BuildUri(endpoint, path);
                return await LoadAbsoluteAsync(profile, uri, cookie, null, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                lastError = exception;
            }
        }

        throw new HttpRequestException($"所有 {profile.DisplayName} HTML 节点均调用失败。", lastError);
    }

    internal async Task<HtmlChannelPage> LoadAbsoluteAsync(
        YueShuGeHtmlChannelProfile profile,
        Uri uri,
        string? referrer,
        CancellationToken cancellationToken)
    {
        var token = profile.TokenName is null
            ? null
            : await configProvider.GetTokenAsync(profile.TokenName, cancellationToken);
        return await LoadAbsoluteAsync(profile, uri, token, referrer, cancellationToken);
    }

    internal async Task<JsonDocument> PostFormAsync(
        YueShuGeHtmlChannelProfile profile,
        string path,
        IReadOnlyDictionary<string, string> form,
        string? referrer,
        CancellationToken cancellationToken)
    {
        var snapshot = await configProvider.GetSnapshotAsync(false, cancellationToken);
        var endpoints = Endpoints(profile, snapshot);
        var cookie = profile.TokenName is null
            ? null
            : snapshot.Tokens.GetValueOrDefault(profile.TokenName);
        Exception? lastError = null;
        foreach (var endpoint in endpoints)
        {
            var stopwatch = Stopwatch.StartNew();
            var statusCode = 0;
            try
            {
                var uri = BuildUri(endpoint, path);
                using var request = CreateRequest(HttpMethod.Post, uri, profile, cookie, referrer);
                request.Content = new FormUrlEncodedContent(form);
                using var response = await httpClient.SendAsync(request, cancellationToken);
                statusCode = (int)response.StatusCode;
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                response.EnsureSuccessStatusCode();
                logger.LogInformation(
                    "{Channel} 表单调用成功：{Host}，状态码：{StatusCode}，耗时：{ElapsedMilliseconds}ms",
                    profile.DisplayName,
                    uri.Host,
                    statusCode,
                    stopwatch.ElapsedMilliseconds);
                return JsonDocument.Parse(json);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                lastError = exception;
                logger.LogWarning(
                    exception,
                    "{Channel} 表单调用失败：{Host}，状态码：{StatusCode}，耗时：{ElapsedMilliseconds}ms",
                    profile.DisplayName,
                    Host(endpoint),
                    statusCode == 0 ? null : statusCode,
                    stopwatch.ElapsedMilliseconds);
            }
        }

        throw new HttpRequestException($"所有 {profile.DisplayName} HTML 节点均调用失败。", lastError);
    }

    private async Task<HtmlChannelPage> LoadAbsoluteAsync(
        YueShuGeHtmlChannelProfile profile,
        Uri uri,
        string? cookie,
        string? referrer,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var request = CreateRequest(HttpMethod.Get, uri, profile, cookie, referrer);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();
        logger.LogInformation(
            "{Channel} 页面读取成功：{Host}，状态码：{StatusCode}，耗时：{ElapsedMilliseconds}ms",
            profile.DisplayName,
            uri.Host,
            (int)response.StatusCode,
            stopwatch.ElapsedMilliseconds);
        return await HtmlChannelUtilities.ParseAsync(
            html,
            response.RequestMessage?.RequestUri ?? uri,
            cancellationToken);
    }

    private IReadOnlyList<string> Endpoints(
        YueShuGeHtmlChannelProfile profile,
        YueShuGeConfigSnapshot snapshot) =>
        new[] { snapshot.Hosts.GetValueOrDefault(profile.HostName) }
            .Concat(configuration.GetSection($"Channels:{profile.ConfigurationName}:Endpoints").Get<string[]>() ?? [])
            .Where(endpoint => !string.IsNullOrWhiteSpace(endpoint))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToArray();

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        Uri uri,
        YueShuGeHtmlChannelProfile profile,
        string? cookie,
        string? referrer)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.TryAddWithoutValidation("User-Agent", profile.UserAgent);
        request.Headers.TryAddWithoutValidation("Accept", profile.Accept);
        request.Headers.TryAddWithoutValidation("Accept-Language", profile.AcceptLanguage);
        if (!string.IsNullOrWhiteSpace(cookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }
        if (!string.IsNullOrWhiteSpace(referrer))
        {
            request.Headers.Referrer = new Uri(referrer, UriKind.Absolute);
        }
        return request;
    }

    private static Uri BuildUri(string endpoint, string path)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("HTML 渠道节点地址无效。");
        }
        var normalized = baseUri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? baseUri
            : new Uri(baseUri.AbsoluteUri + '/', UriKind.Absolute);
        return new Uri(normalized, path.TrimStart('/'));
    }

    private static string Host(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? uri.Host : "invalid-endpoint";
}

/// <summary>动态 HTML 渠道的纯数据配置。</summary>
public sealed record YueShuGeHtmlChannelProfile(
    string DisplayName,
    string HostName,
    string ConfigurationName,
    string? TokenName,
    string UserAgent,
    string AcceptLanguage,
    string Accept = "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
