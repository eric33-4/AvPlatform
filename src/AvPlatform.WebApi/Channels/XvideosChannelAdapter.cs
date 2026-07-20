using System.Net;
using System.Text.RegularExpressions;
using AvPlatform.WebApi.Models;

namespace AvPlatform.WebApi.Channels;

/// <summary>XVIDEOS HTML 渠道。</summary>
public sealed partial class XvideosChannelAdapter(HttpClient httpClient) : IChannelAdapter
{
    public string Code => "xvideos";
    public string Name => "XVIDEOS";
    public string Mode => "HTML Parse";

    public async Task<ChannelHomeResponse> GetHomeAsync(CancellationToken cancellationToken)
    {
        var page = await HtmlChannelUtilities.LoadAsync(httpClient, new Uri("/", UriKind.Relative), cancellationToken);
        return new ChannelHomeResponse(
            Code,
            Name,
            Mode,
            DateTimeOffset.UtcNow,
            false,
            null,
            ParseItems(page),
            "播放地址由详情页实时提取，过期后会重新刷新缓存。");
    }

    public async Task<ChannelSearchResponse> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var page = await HtmlChannelUtilities.LoadAsync(
            httpClient,
            new Uri($"/?k={Uri.EscapeDataString(query)}", UriKind.Relative),
            cancellationToken);
        return new ChannelSearchResponse(
            Code,
            query,
            DateTimeOffset.UtcNow,
            false,
            ParseItems(page),
            "XVIDEOS 实时搜索结果。");
    }

    public async Task<ChannelDetailResponse?> GetDetailAsync(string itemId, CancellationToken cancellationToken)
    {
        var page = await LoadDetailAsync(itemId, cancellationToken);
        if (page is null)
        {
            return null;
        }

        var title = HtmlChannelUtilities.Meta(page.Document, "og:title") ??
                    HtmlChannelUtilities.Text(page.Document.QuerySelector("h2.page-title")) ?? itemId;
        var cover = HtmlChannelUtilities.AbsoluteUrl(
            HtmlChannelUtilities.Meta(page.Document, "og:image"), page.Uri);
        var description = HtmlChannelUtilities.Meta(page.Document, "description");
        var source = ExtractSource(page.Html);
        var duration = Duration(page);
        var author = DecodeScriptValue(UploaderRegex().Match(page.Html));

        return new ChannelDetailResponse(
            Code,
            Name,
            itemId,
            title,
            cover,
            description,
            "视频",
            author,
            1,
            InteractionCount(page),
            true,
            false,
            0,
            DateTimeOffset.UtcNow,
            false,
            [new ChannelEpisodeResponse("main", title, duration, true, source is not null)]);
    }

    public async Task<ChannelPlaySource?> GetPlayAsync(
        string itemId,
        string episodeId,
        CancellationToken cancellationToken)
    {
        if (episodeId != "main")
        {
            return null;
        }

        var page = await LoadDetailAsync(itemId, cancellationToken);
        if (page is null)
        {
            return null;
        }

        var source = ExtractSource(page.Html);
        var title = HtmlChannelUtilities.Meta(page.Document, "og:title") ?? itemId;
        return new ChannelPlaySource(
            episodeId,
            title,
            source is not null,
            source,
            source?.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) == true
                ? "application/vnd.apple.mpegurl"
                : source is null ? null : "video/mp4",
            "video",
            page.Uri.ToString());
    }

    private IReadOnlyList<ChannelItemResponse> ParseItems(HtmlChannelPage page)
    {
        var items = new List<ChannelItemResponse>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var card in page.Document.QuerySelectorAll("div.frame-block.thumb-block"))
        {
            var anchor = card.QuerySelector("p.title a[href]");
            var path = HtmlChannelUtilities.NormalizePath(anchor?.GetAttribute("href"), page.Uri);
            var title = WebUtility.HtmlDecode(anchor?.GetAttribute("title") ?? string.Empty).Trim();
            if (path is null || title.Length == 0 || !paths.Add(path))
            {
                continue;
            }

            var image = card.QuerySelector("div.thumb img");
            var duration = HtmlChannelUtilities.Text(card.QuerySelector("p.title span.duration"));
            var author = HtmlChannelUtilities.Text(card.QuerySelector("p.metadata span.name"));
            var metadata = HtmlChannelUtilities.Text(card.QuerySelector("p.metadata"));
            var views = metadata?.Split('-', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(HtmlChannelUtilities.ParseCompactNumber)
                .FirstOrDefault(value => value is not null);

            items.Add(new ChannelItemResponse(
                HtmlChannelUtilities.EncodePath(path),
                title,
                null,
                HtmlChannelUtilities.AbsoluteUrl(
                    image?.GetAttribute("data-src") ?? image?.GetAttribute("src"), page.Uri),
                duration is null ? null : $"时长 {duration}",
                HtmlChannelUtilities.Text(card.QuerySelector("span.video-hd-mark")) ?? "视频",
                author,
                1,
                views));
            if (items.Count == 24)
            {
                break;
            }
        }

        return items;
    }

    private async Task<HtmlChannelPage?> LoadDetailAsync(string itemId, CancellationToken cancellationToken)
    {
        try
        {
            return await HtmlChannelUtilities.LoadAsync(
                httpClient,
                new Uri(HtmlChannelUtilities.DecodePath(itemId), UriKind.Relative),
                cancellationToken);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private static string? ExtractSource(string html) =>
        DecodeScriptValue(HlsRegex().Match(html)) ??
        DecodeScriptValue(HighVideoRegex().Match(html)) ??
        DecodeScriptValue(LowVideoRegex().Match(html));

    private static string? DecodeScriptValue(Match match) =>
        match.Success
            ? WebUtility.HtmlDecode(match.Groups["value"].Value).Replace("\\/", "/", StringComparison.Ordinal)
            : null;

    private static string? Duration(HtmlChannelPage page)
    {
        var raw = HtmlChannelUtilities.Meta(page.Document, "og:duration");
        return int.TryParse(raw, out var seconds) ? TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss") : null;
    }

    private static decimal? InteractionCount(HtmlChannelPage page) =>
        decimal.TryParse(HtmlChannelUtilities.Meta(page.Document, "interactionCount"), out var value) ? value : null;

    [GeneratedRegex(@"setVideoHLS\('(?<value>[^']+)'\)")]
    private static partial Regex HlsRegex();

    [GeneratedRegex(@"setVideoUrlHigh\('(?<value>[^']+)'\)")]
    private static partial Regex HighVideoRegex();

    [GeneratedRegex(@"setVideoUrlLow\('(?<value>[^']+)'\)")]
    private static partial Regex LowVideoRegex();

    [GeneratedRegex(@"setUploaderName\('(?<value>[^']+)'\)")]
    private static partial Regex UploaderRegex();
}
