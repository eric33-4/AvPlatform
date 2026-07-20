using AvPlatform.WebApi.Models;

namespace AvPlatform.WebApi.Channels;

/// <summary>HSCK HTML 渠道。</summary>
public sealed class HsckChannelAdapter(YueShuGeHtmlClient htmlClient) : IChannelAdapter
{
    private const string EpisodeId = "main";
    private static readonly YueShuGeHtmlChannelProfile Profile = new(
        "HSCK",
        "hsck",
        "Hsck",
        null,
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/143.0.0.0 Safari/537.36",
        "zh-CN,zh;q=0.9,en;q=0.8");

    public string Code => "hsck";
    public string Name => "HSCK";
    public string Mode => "HTML Parse";

    public async Task<ChannelHomeResponse> GetHomeAsync(CancellationToken cancellationToken)
    {
        var page = await htmlClient.LoadAsync(Profile, "/?type=ycgc&p=1", cancellationToken);
        return new ChannelHomeResponse(
            Code,
            Name,
            Mode,
            DateTimeOffset.UtcNow,
            false,
            null,
            ParseItems(page),
            "HSCK HLS 地址由详情页实时提取。");
    }

    public async Task<ChannelSearchResponse> SearchAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var page = await htmlClient.LoadAsync(
            Profile,
            $"/?search2=ndafeoafa&search={Uri.EscapeDataString(query)}",
            cancellationToken);
        return new ChannelSearchResponse(
            Code,
            query,
            DateTimeOffset.UtcNow,
            false,
            ParseItems(page),
            "HSCK 实时搜索结果。");
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
        var source = Source(page);
        var cover = HtmlChannelUtilities.AbsoluteUrl(
            HtmlChannelUtilities.Meta(page.Document, "og:image"),
            page.Uri);
        return new ChannelDetailResponse(
            Code,
            Name,
            itemId,
            title,
            cover,
            HtmlChannelUtilities.Meta(page.Document, "description"),
            "视频",
            null,
            1,
            null,
            true,
            false,
            0,
            DateTimeOffset.UtcNow,
            false,
            [new ChannelEpisodeResponse(EpisodeId, title, null, true, source is not null)]);
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
            source is null ? null : "application/vnd.apple.mpegurl",
            "video",
            page.Uri.ToString());
    }

    private static IReadOnlyList<ChannelItemResponse> ParseItems(HtmlChannelPage page)
    {
        var items = new List<ChannelItemResponse>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var card in page.Document.QuerySelectorAll(".stui-vodlist__box"))
        {
            var link = card.QuerySelector(".stui-vodlist__thumb") ?? card.QuerySelector("a[href]");
            var path = HtmlChannelUtilities.NormalizePath(link?.GetAttribute("href"), page.Uri);
            var title = link?.GetAttribute("title") ??
                        HtmlChannelUtilities.Text(card.QuerySelector("h4.title a"));
            if (path is null || string.IsNullOrWhiteSpace(title) || !seen.Add(path))
            {
                continue;
            }

            var cover = HtmlChannelUtilities.AbsoluteUrl(
                link?.GetAttribute("data-original") ?? card.QuerySelector("img")?.GetAttribute("src"),
                page.Uri);
            items.Add(new ChannelItemResponse(
                HtmlChannelUtilities.EncodePath(path),
                title,
                null,
                cover,
                null,
                "视频",
                null,
                1,
                null));
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

    private static string? Source(HtmlChannelPage page) =>
        HtmlChannelUtilities.AbsoluteUrl(
            page.Document.QuerySelector("#video_img")?.GetAttribute("src") ??
            page.Document.QuerySelector("video source")?.GetAttribute("src"),
            page.Uri);

    private static string CleanTitle(string? title)
    {
        const string marker = " - 黄色仓库";
        if (string.IsNullOrWhiteSpace(title))
        {
            return "HSCK 视频";
        }
        var index = title.IndexOf(marker, StringComparison.Ordinal);
        return index > 0 ? title[..index] : title;
    }
}
