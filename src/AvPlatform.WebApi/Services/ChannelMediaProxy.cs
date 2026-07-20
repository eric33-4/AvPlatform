using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;

namespace AvPlatform.WebApi.Services;

/// <summary>把上游媒体流转成同源响应，避免浏览器跨域拦截。</summary>
public interface IChannelMediaProxy
{
    Task ProxyPlaylistAsync(
        HttpContext context,
        string sourceUrl,
        string publicStreamPath,
        string? referrerUrl,
        string transport,
        CancellationToken cancellationToken);

    Task ProxyBinaryAsync(
        HttpContext context,
        string sourceUrl,
        string fallbackMediaType,
        string? referrerUrl,
        string transport,
        CancellationToken cancellationToken);

    bool TryDecodeResource(string token, string publicStreamPath, out Uri resourceUri);
}

/// <summary>只代理已经由渠道适配器确认可播放的媒体地址。</summary>
public sealed partial class ChannelMediaProxy(
    IHttpClientFactory httpClientFactory,
    IDataProtectionProvider dataProtectionProvider,
    ILogger<ChannelMediaProxy> logger) : IChannelMediaProxy
{
    private readonly ITimeLimitedDataProtector _resourceProtector = dataProtectionProvider
        .CreateProtector("AvPlatform.ChannelMediaResource.v1")
        .ToTimeLimitedDataProtector();

    public async Task ProxyPlaylistAsync(
        HttpContext context,
        string sourceUrl,
        string publicStreamPath,
        string? referrerUrl,
        string transport,
        CancellationToken cancellationToken)
    {
        var playlist = transport == "curl"
            ? Encoding.UTF8.GetString((await SendWithCurlAsync(
                sourceUrl,
                referrerUrl,
                null,
                cancellationToken)).Body)
            : await ReadPlaylistWithHttpClientAsync(sourceUrl, referrerUrl, cancellationToken);
        var rewritten = RewritePlaylist(
            playlist,
            new Uri(sourceUrl),
            publicStreamPath,
            uri => ProtectResource(publicStreamPath, uri));

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/vnd.apple.mpegurl";
        context.Response.Headers.CacheControl = "private, max-age=60";
        await context.Response.WriteAsync(rewritten, Encoding.UTF8, cancellationToken);

        LogResponse(sourceUrl, context.Response.StatusCode, context.Response.ContentType);
    }

    public async Task ProxyBinaryAsync(
        HttpContext context,
        string sourceUrl,
        string fallbackMediaType,
        string? referrerUrl,
        string transport,
        CancellationToken cancellationToken)
    {
        if (transport == "curl")
        {
            await ProxyBinaryWithCurlAsync(
                context,
                sourceUrl,
                fallbackMediaType,
                referrerUrl,
                cancellationToken);
            return;
        }

        using var request = CreateRequest(sourceUrl, referrerUrl);
        if (context.Request.Headers.TryGetValue("Range", out var range) &&
            RangeHeaderValue.TryParse(range, out var rangeHeader))
        {
            request.Headers.Range = rangeHeader;
        }

        var client = httpClientFactory.CreateClient("channel-media");
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        context.Response.StatusCode = (int)response.StatusCode;
        context.Response.ContentType = SelectMediaType(
            response.Content.Headers.ContentType?.ToString(),
            fallbackMediaType);
        if (response.Content.Headers.ContentLength is long length)
        {
            context.Response.ContentLength = length;
        }
        if (response.Content.Headers.ContentRange is not null)
        {
            context.Response.Headers.ContentRange = response.Content.Headers.ContentRange.ToString();
        }
        if (response.Headers.AcceptRanges.Count > 0)
        {
            context.Response.Headers.AcceptRanges = string.Join(",", response.Headers.AcceptRanges);
        }

        LogResponse(sourceUrl, context.Response.StatusCode, context.Response.ContentType);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await stream.CopyToAsync(context.Response.Body, cancellationToken);
    }

    public bool TryDecodeResource(string token, string publicStreamPath, out Uri resourceUri)
    {
        resourceUri = null!;
        try
        {
            var protectedPayload = WebEncoders.Base64UrlDecode(token);
            var payload = Encoding.UTF8.GetString(_resourceProtector.Unprotect(protectedPayload, out _));
            var separator = payload.IndexOf('\n');
            if (separator < 0 ||
                !payload[..separator].Equals(publicStreamPath, StringComparison.Ordinal) ||
                !Uri.TryCreate(payload[(separator + 1)..], UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https"))
            {
                return false;
            }

            resourceUri = uri;
            return true;
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            return false;
        }
    }

    private async Task<string> ReadPlaylistWithHttpClientAsync(
        string sourceUrl,
        string? referrerUrl,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("channel-media");
        using var request = CreateRequest(sourceUrl, referrerUrl);
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private async Task ProxyBinaryWithCurlAsync(
        HttpContext context,
        string sourceUrl,
        string fallbackMediaType,
        string? referrerUrl,
        CancellationToken cancellationToken)
    {
        var range = context.Request.Headers.TryGetValue("Range", out var value) ? value.ToString() : null;
        var response = await SendWithCurlAsync(sourceUrl, referrerUrl, range, cancellationToken);

        context.Response.StatusCode = response.StatusCode;
        context.Response.ContentType = SelectMediaType(response.MediaType, fallbackMediaType);
        context.Response.ContentLength = response.Body.Length;
        if (!string.IsNullOrWhiteSpace(response.ContentRange))
        {
            context.Response.Headers.ContentRange = response.ContentRange;
        }
        if (!string.IsNullOrWhiteSpace(response.AcceptRanges))
        {
            context.Response.Headers.AcceptRanges = response.AcceptRanges;
        }

        LogResponse(sourceUrl, response.StatusCode, context.Response.ContentType);
        await context.Response.Body.WriteAsync(response.Body, cancellationToken);
    }

    private static async Task<CurlResponse> SendWithCurlAsync(
        string sourceUrl,
        string? referrerUrl,
        string? range,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "curl.exe" : "curl",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--silent");
        startInfo.ArgumentList.Add("--show-error");
        startInfo.ArgumentList.Add("--location");
        startInfo.ArgumentList.Add("--retry");
        startInfo.ArgumentList.Add("3");
        startInfo.ArgumentList.Add("--retry-all-errors");
        startInfo.ArgumentList.Add("--retry-delay");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("--user-agent");
        startInfo.ArgumentList.Add(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124 Safari/537.36");
        if (!string.IsNullOrWhiteSpace(referrerUrl))
        {
            startInfo.ArgumentList.Add("--referer");
            startInfo.ArgumentList.Add(referrerUrl);
        }
        if (!string.IsNullOrWhiteSpace(range))
        {
            startInfo.ArgumentList.Add("--range");
            startInfo.ArgumentList.Add(range.Replace("bytes=", string.Empty, StringComparison.OrdinalIgnoreCase));
        }
        startInfo.ArgumentList.Add("--write-out");
        startInfo.ArgumentList.Add(
            "%{stderr}\nAVP_META:%{http_code}\t%{content_type}\t%header{content-range}\t%header{accept-ranges}\n");
        startInfo.ArgumentList.Add(sourceUrl);

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        await using var body = new MemoryStream();
        var copyTask = process.StandardOutput.BaseStream.CopyToAsync(body, cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await Task.WhenAll(copyTask, process.WaitForExitAsync(cancellationToken));
        }
        catch (OperationCanceledException)
        {
            process.Kill(true);
            throw;
        }

        var stderr = await errorTask;
        var metadata = ParseCurlMetadata(stderr);
        if (process.ExitCode != 0 || metadata.StatusCode is < 200 or >= 400)
        {
            throw new HttpRequestException(
                $"curl 媒体请求失败，退出码 {process.ExitCode}，HTTP {metadata.StatusCode}。");
        }

        return metadata with { Body = body.ToArray() };
    }

    private static CurlResponse ParseCurlMetadata(string stderr)
    {
        var marker = stderr.LastIndexOf("AVP_META:", StringComparison.Ordinal);
        if (marker < 0)
        {
            throw new HttpRequestException("curl 未返回媒体响应元数据。");
        }

        var values = stderr[(marker + "AVP_META:".Length)..]
            .Split(['\t', '\r', '\n'], StringSplitOptions.None);
        if (values.Length < 4 || !int.TryParse(values[0], out var statusCode))
        {
            throw new HttpRequestException("curl 返回的媒体响应元数据无效。");
        }

        return new CurlResponse(statusCode, values[1], values[2], values[3], []);
    }

    internal static string RewritePlaylist(
        string playlist,
        Uri playlistUri,
        string publicStreamPath,
        Func<Uri, string>? encodeResource = null)
    {
        encodeResource ??= EncodeResource;
        var lines = playlist.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('#'))
            {
                lines[index] = PlaylistUriRegex().Replace(lines[index], match =>
                    $"URI=\"{ResourcePath(
                        publicStreamPath,
                        new Uri(playlistUri, match.Groups["value"].Value),
                        encodeResource)}\"");
                continue;
            }

            lines[index] = ResourcePath(publicStreamPath, new Uri(playlistUri, line), encodeResource);
        }

        return string.Join('\n', lines);
    }

    internal static string EncodeResource(Uri uri) =>
        WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(uri.AbsoluteUri));

    internal static Uri DecodeResource(string token) =>
        new(Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(token)), UriKind.Absolute);

    private static string ResourcePath(string publicStreamPath, Uri uri, Func<Uri, string> encodeResource) =>
        $"{publicStreamPath}/resources/{encodeResource(uri)}";

    private string ProtectResource(string publicStreamPath, Uri uri)
    {
        var payload = Encoding.UTF8.GetBytes($"{publicStreamPath}\n{uri.AbsoluteUri}");
        var protectedPayload = _resourceProtector.Protect(
            payload,
            DateTimeOffset.UtcNow.AddMinutes(30));
        return WebEncoders.Base64UrlEncode(protectedPayload);
    }

    private static HttpRequestMessage CreateRequest(string sourceUrl, string? referrerUrl)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, sourceUrl);
        if (Uri.TryCreate(referrerUrl, UriKind.Absolute, out var referrer))
        {
            request.Headers.Referrer = referrer;
        }

        return request;
    }

    // 某些媒体节点会把 .ts 错标为 text/*，此时使用调用方根据扩展名给出的类型。
    private static string SelectMediaType(string? upstreamMediaType, string fallbackMediaType) =>
        string.IsNullOrWhiteSpace(upstreamMediaType) ||
        upstreamMediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
        upstreamMediaType.StartsWith("application/octet-stream", StringComparison.OrdinalIgnoreCase)
            ? fallbackMediaType
            : upstreamMediaType;

    private void LogResponse(string sourceUrl, int statusCode, string? mediaType) =>
        logger.LogInformation(
            "媒体代理响应：{Host}，状态码：{StatusCode}，类型：{MediaType}",
            new Uri(sourceUrl).Host,
            statusCode,
            mediaType);

    [GeneratedRegex("URI=\\\"(?<value>[^\\\"]+)\\\"", RegexOptions.IgnoreCase)]
    private static partial Regex PlaylistUriRegex();

    private sealed record CurlResponse(
        int StatusCode,
        string MediaType,
        string ContentRange,
        string AcceptRanges,
        byte[] Body);
}
