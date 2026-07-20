using System.Net;
using System.Text.RegularExpressions;
using AvPlatform.WebApi.Models;

namespace AvPlatform.WebApi.Channels;

/// <summary>91PRON HTML 渠道。</summary>
public sealed partial class Pron91ChannelAdapter(YueShuGeHtmlClient htmlClient) : IChannelAdapter
{
    private const string EpisodeId = "main";
    private static readonly YueShuGeHtmlChannelProfile Profile = new(
        "91PRON",
        "91pron",
        "Pron91",
        null,
        "91appnew",
        "zh-CN,zh;q=0.9,en-US;q=0.8,en;q=0.7");

    public string Code => "91pron";
    public string Name => "91PRON";
    public string Mode => "HTML Parse";

    public async Task<ChannelHomeResponse> GetHomeAsync(CancellationToken cancellationToken)
    {
        var page = await htmlClient.LoadAsync(Profile, "/index.php", cancellationToken);
        return new ChannelHomeResponse(
            Code,
            Name,
            Mode,
            DateTimeOffset.UtcNow,
            false,
            null,
            ParseItems(page),
            "91PRON 播放地址从详情页编码脚本中实时恢复。");
    }

    public async Task<ChannelSearchResponse> SearchAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var page = await htmlClient.LoadAsync(
            Profile,
            $"/search_result.php?search_id={Uri.EscapeDataString(query)}&search_type=search_videos&min_duration=",
            cancellationToken);
        return new ChannelSearchResponse(
            Code,
            query,
            DateTimeOffset.UtcNow,
            false,
            ParseItems(page),
            "91PRON 实时搜索结果。");
    }

    public async Task<ChannelDetailResponse?> GetDetailAsync(
        string itemId,
        CancellationToken cancellationToken)
    {
        var page = await LoadDetailAsync(itemId, cancellationToken);
        if (page is null)
        {
            return null;
        }

        var title = CleanTitle(page.Document.Title);
        var player = page.Document.QuerySelector("video#player_one");
        var source = Source(page);
        return new ChannelDetailResponse(
            Code,
            Name,
            itemId,
            title,
            HtmlChannelUtilities.AbsoluteUrl(player?.GetAttribute("poster"), page.Uri),
            HtmlChannelUtilities.Meta(page.Document, "description"),
            "视频",
            Author(page.Html),
            1,
            Popularity(page.Html),
            true,
            false,
            0,
            DateTimeOffset.UtcNow,
            false,
            [new ChannelEpisodeResponse(
                EpisodeId,
                title,
                HtmlChannelUtilities.Text(page.Document.QuerySelector(".video-info-span")),
                true,
                source is not null)]);
    }

    public async Task<ChannelPlaySource?> GetPlayAsync(
        string itemId,
        string episodeId,
        CancellationToken cancellationToken)
    {
        if (episodeId != EpisodeId || await LoadDetailAsync(itemId, cancellationToken) is not { } page)
        {
            return null;
        }

        var source = Source(page);
        return new ChannelPlaySource(
            EpisodeId,
            CleanTitle(page.Document.Title),
            source is not null,
            source,
            MediaType(source),
            "video",
            page.Uri.ToString());
    }

    private static IReadOnlyList<ChannelItemResponse> ParseItems(HtmlChannelPage page)
    {
        var items = new List<ChannelItemResponse>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var card in page.Document.QuerySelectorAll(".well.well-sm"))
        {
            var link = card.QuerySelector("a[href*='view_video.php']");
            var path = HtmlChannelUtilities.NormalizePath(link?.GetAttribute("href"), page.Uri);
            var title = HtmlChannelUtilities.Text(card.QuerySelector(".video-title"));
            if (path is null || title is null || !seen.Add(path))
            {
                continue;
            }

            var cover = HtmlChannelUtilities.AbsoluteUrl(
                card.QuerySelector("img.img-responsive")?.GetAttribute("src"),
                page.Uri);
            items.Add(new ChannelItemResponse(
                HtmlChannelUtilities.EncodePath(path),
                title,
                null,
                cover,
                HtmlChannelUtilities.Text(card.QuerySelector(".duration")),
                "视频",
                null,
                1,
                Popularity(card.TextContent)));
        }
        return items;
    }

    private async Task<HtmlChannelPage?> LoadDetailAsync(
        string itemId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await htmlClient.LoadAsync(
                Profile,
                HtmlChannelUtilities.DecodePath(itemId),
                cancellationToken);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private static string? Source(HtmlChannelPage page)
    {
        var direct = page.Document.QuerySelector("video#player_one source")?.GetAttribute("src");
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return HtmlChannelUtilities.AbsoluteUrl(direct, page.Uri);
        }

        var encoded = EncodedSourceRegex().Match(page.Html).Groups["value"].Value;
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return null;
        }
        var decoded = WebUtility.HtmlDecode(Uri.UnescapeDataString(encoded));
        var value = SourceTagRegex().Match(decoded).Groups["url"].Value;
        return HtmlChannelUtilities.AbsoluteUrl(value, page.Uri);
    }

    private static decimal? Popularity(string html)
    {
        var value = PopularityRegex().Match(WebUtility.HtmlDecode(html)).Groups["value"].Value;
        return HtmlChannelUtilities.ParseCompactNumber(value);
    }

    private static string? Author(string html)
    {
        var value = AuthorRegex().Match(WebUtility.HtmlDecode(html)).Groups["value"].Value;
        return string.IsNullOrWhiteSpace(value) ? null : WhitespaceRegex().Replace(value, " ").Trim();
    }

    private static string CleanTitle(string? title)
    {
        const string suffix = " - 91porn";
        return string.IsNullOrWhiteSpace(title)
            ? "91PRON 视频"
            : title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? title[..^suffix.Length] : title;
    }

    private static string? MediaType(string? source) =>
        source?.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) == true
            ? "application/vnd.apple.mpegurl"
            : source is null ? null : "video/mp4";

    [GeneratedRegex("strencode2\\(\"(?<value>%[0-9a-fA-F%]+)\"\\)", RegexOptions.IgnoreCase)]
    private static partial Regex EncodedSourceRegex();

    [GeneratedRegex("<source\\s+src=['\"](?<url>[^'\"]+)", RegexOptions.IgnoreCase)]
    private static partial Regex SourceTagRegex();

    [GeneratedRegex("热度:\\s*(?<value>[0-9,.kKmM]+)", RegexOptions.IgnoreCase)]
    private static partial Regex PopularityRegex();

    [GeneratedRegex("作者:\\s*(?<value>[^<\\r\\n]+)", RegexOptions.IgnoreCase)]
    private static partial Regex AuthorRegex();

    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespaceRegex();
}
