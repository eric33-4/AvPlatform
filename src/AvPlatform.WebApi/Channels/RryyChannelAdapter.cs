using AvPlatform.WebApi.Models;

namespace AvPlatform.WebApi.Channels;

/// <summary>RRYY HTML 渠道；播放地址通过 Box 公共解析接口获取。</summary>
public sealed class RryyChannelAdapter(
    YueShuGeHtmlClient htmlClient,
    YueShuGeBoxClient boxClient) : IChannelAdapter
{
    private const string EpisodeId = "main";
    private static readonly YueShuGeHtmlChannelProfile Profile = new(
        "RRYY",
        "rryy",
        "Rryy",
        "token_rryy",
        "Mozilla/5.0 (iPhone; CPU iPhone OS 18_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.5 Mobile/15E148 Safari/604.1",
        "zh-CN,zh;q=0.9");

    public string Code => "rryy";
    public string Name => "RRYY";
    public string Mode => "HTML Parse + Box";

    public async Task<ChannelHomeResponse> GetHomeAsync(CancellationToken cancellationToken)
    {
        var page = await htmlClient.LoadAsync(Profile, "/home/index/1/1.html", cancellationToken);
        return new ChannelHomeResponse(
            Code,
            Name,
            Mode,
            DateTimeOffset.UtcNow,
            false,
            null,
            ParseItems(page),
            "RRYY 详情页的会员遮罩不会下发给浏览器，播放源由 Box 后端解析。");
    }

    public async Task<ChannelSearchResponse> SearchAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var page = await htmlClient.LoadAsync(
            Profile,
            $"/search/video/{Uri.EscapeDataString(query)}/1.html",
            cancellationToken);
        return new ChannelSearchResponse(
            Code,
            query,
            DateTimeOffset.UtcNow,
            false,
            ParseItems(page),
            "RRYY 实时搜索结果。");
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

        var title = HtmlChannelUtilities.Meta(page.Document, "og:title") ??
                    HtmlChannelUtilities.Text(page.Document.QuerySelector("#playerbox + section h2, article h2, main h2")) ??
                    CleanTitle(page.Document.Title);
        var cover = HtmlChannelUtilities.AbsoluteUrl(
            HtmlChannelUtilities.Meta(page.Document, "og:image") ??
            page.Document.QuerySelector("#playerbox img")?.GetAttribute("src"),
            page.Uri);
        var duration = HtmlChannelUtilities.Text(page.Document.QuerySelector("#playerbox + section .space-x-1"));
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
            [new ChannelEpisodeResponse(EpisodeId, title, duration, true, true)]);
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

        var source = await boxClient.ResolveVideoUrlAsync(page.Uri.ToString(), cancellationToken);
        var title = HtmlChannelUtilities.Meta(page.Document, "og:title") ?? CleanTitle(page.Document.Title);
        return new ChannelPlaySource(
            EpisodeId,
            title,
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
        foreach (var card in page.Document.QuerySelectorAll(".xpc-card"))
        {
            var link = card.QuerySelector("a[href*='/play/']");
            var path = HtmlChannelUtilities.NormalizePath(link?.GetAttribute("href"), page.Uri);
            var title = HtmlChannelUtilities.Text(card.QuerySelector(".xpc-title a")) ??
                        card.QuerySelector("img")?.GetAttribute("alt");
            if (path is null || string.IsNullOrWhiteSpace(title) || !seen.Add(path))
            {
                continue;
            }

            var cover = HtmlChannelUtilities.AbsoluteUrl(
                card.QuerySelector("img")?.GetAttribute("data-original") ??
                card.QuerySelector("img")?.GetAttribute("src"),
                page.Uri);
            var duration = HtmlChannelUtilities.Text(card.QuerySelector(".duration"));
            var category = HtmlChannelUtilities.Text(card.QuerySelector(".meta-chip"));
            items.Add(new ChannelItemResponse(
                HtmlChannelUtilities.EncodePath(path),
                title,
                null,
                cover,
                string.Join(" · ", new[] { category, duration }.Where(value => !string.IsNullOrWhiteSpace(value))),
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

    private static string CleanTitle(string? title)
    {
        const string suffix = " - 我为人人影院";
        return string.IsNullOrWhiteSpace(title)
            ? "RRYY 视频"
            : title.EndsWith(suffix, StringComparison.Ordinal) ? title[..^suffix.Length] : title;
    }
}
