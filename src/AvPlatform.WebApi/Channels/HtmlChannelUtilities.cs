using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.WebUtilities;

namespace AvPlatform.WebApi.Channels;

/// <summary>HTML 渠道共用的页面读取、ID 编码和脚本解包工具。</summary>
internal static partial class HtmlChannelUtilities
{
    public static async Task<HtmlChannelPage> LoadAsync(
        HttpClient httpClient,
        Uri uri,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(uri, cancellationToken);
        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();
        return await ParseAsync(
            html,
            response.RequestMessage?.RequestUri ?? uri,
            cancellationToken);
    }

    public static async Task<HtmlChannelPage> ParseAsync(
        string html,
        Uri uri,
        CancellationToken cancellationToken)
    {
        var parser = new HtmlParser();
        var document = await parser.ParseDocumentAsync(html, cancellationToken);
        return new HtmlChannelPage(document, html, uri);
    }

    public static string EncodePath(string path) =>
        WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(path));

    public static string DecodePath(string value)
    {
        try
        {
            var path = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(value));
            var pathOnly = path.Split('?', 2)[0];
            var decodedPathOnly = Uri.UnescapeDataString(pathOnly);
            var hasTraversal = decodedPathOnly
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => segment is "." or "..");
            if (!path.StartsWith('/') ||
                decodedPathOnly.StartsWith("//", StringComparison.Ordinal) ||
                decodedPathOnly.Contains('\\') ||
                hasTraversal)
            {
                throw new FormatException("渠道内容 ID 无效。");
            }

            return path;
        }
        catch (FormatException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new FormatException("渠道内容 ID 无效。", exception);
        }
    }

    public static string? NormalizePath(string? href, Uri pageUri)
    {
        if (string.IsNullOrWhiteSpace(href) || !Uri.TryCreate(pageUri, href, out var uri))
        {
            return null;
        }

        return uri.AbsolutePath + uri.Query;
    }

    public static string? AbsoluteUrl(string? value, Uri pageUri)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
            !Uri.TryCreate(pageUri, WebUtility.HtmlDecode(value), out var uri))
        {
            return null;
        }

        return uri.ToString();
    }

    public static string? Meta(IDocument document, string name) =>
        document.QuerySelector($"meta[property='{name}']")?.GetAttribute("content") ??
        document.QuerySelector($"meta[name='{name}']")?.GetAttribute("content");

    public static string? Text(IElement? element)
    {
        var value = WebUtility.HtmlDecode(element?.TextContent ?? string.Empty);
        return string.IsNullOrWhiteSpace(value) ? null : WhitespaceRegex().Replace(value, " ").Trim();
    }

    public static decimal? ParseCompactNumber(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().ToLowerInvariant().Replace(",", string.Empty, StringComparison.Ordinal);
        var multiplier = normalized.EndsWith('m') ? 1_000_000m : normalized.EndsWith('k') ? 1_000m : 1m;
        normalized = normalized.TrimEnd('m', 'k');
        return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
            ? number * multiplier
            : null;
    }

    public static string? UnpackFirstMediaUrl(string html)
    {
        foreach (Match match in PackedScriptRegex().Matches(html))
        {
            var payload = DecodeJavaScriptString(match.Groups["payload"].Value);
            var tokens = DecodeJavaScriptString(match.Groups["tokens"].Value).Split('|');
            if (!int.TryParse(match.Groups["radix"].Value, out var radix))
            {
                continue;
            }

            var unpacked = PackedWordRegex().Replace(payload, word =>
            {
                var index = ParseRadix(word.Value, radix);
                return index >= 0 && index < tokens.Length && tokens[index].Length > 0 ? tokens[index] : word.Value;
            });

            var source = SourceRegex().Match(unpacked);
            if (source.Success)
            {
                return WebUtility.HtmlDecode(source.Groups["url"].Value);
            }
        }

        return null;
    }

    private static string DecodeJavaScriptString(string value) => value
        .Replace("\\/", "/", StringComparison.Ordinal)
        .Replace("\\'", "'", StringComparison.Ordinal)
        .Replace("\\\"", "\"", StringComparison.Ordinal)
        .Replace("\\\\", "\\", StringComparison.Ordinal);

    private static int ParseRadix(string value, int radix)
    {
        var result = 0;
        foreach (var character in value)
        {
            var digit = character switch
            {
                >= '0' and <= '9' => character - '0',
                >= 'a' and <= 'z' => character - 'a' + 10,
                >= 'A' and <= 'Z' => character - 'A' + 10,
                _ => -1
            };
            if (digit < 0 || digit >= radix)
            {
                return -1;
            }
            result = checked(result * radix + digit);
        }

        return result;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"eval\(function\(p,a,c,k,e,d\).*?\}\('(?<payload>(?:\\.|[^'])*)',(?<radix>\d+),(?<count>\d+),'(?<tokens>(?:\\.|[^'])*)'\.split\('\|'\)", RegexOptions.Singleline)]
    private static partial Regex PackedScriptRegex();

    [GeneratedRegex(@"\b[0-9a-zA-Z]+\b")]
    private static partial Regex PackedWordRegex();

    [GeneratedRegex("""source(?:1280|842)?\s*=\s*['"](?<url>https?://[^'"]+)['"]""", RegexOptions.IgnoreCase)]
    private static partial Regex SourceRegex();
}

/// <summary>已解析的 HTML 页面。</summary>
internal sealed record HtmlChannelPage(IDocument Document, string Html, Uri Uri);
