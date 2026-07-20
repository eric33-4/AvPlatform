using AvPlatform.WebApi.Models;

namespace AvPlatform.WebApi.Channels;

/// <summary>MISSAV HTML 渠道，后端负责页面解析和播放源提取。</summary>
public sealed class MissAvChannelAdapter(
    HttpClient httpClient,
    IConfiguration configuration,
    ILogger<MissAvChannelAdapter> logger) : IChannelAdapter
{
    public string Code => "missav";
    public string Name => "MissAV";
    public string Mode => "HTML Parse";

    public async Task<ChannelHomeResponse> GetHomeAsync(CancellationToken cancellationToken)
    {
        var homePath = configuration.GetValue("Channels:MissAv:HomePath", "/");
        var page = await LoadRequiredAsync(homePath, cancellationToken);
        return new ChannelHomeResponse(
            Code,
            Name,
            Mode,
            DateTimeOffset.UtcNow,
            false,
            null,
            ParseItems(page),
            "HTML 在后端解析，浏览器不直接访问 MISSAV。");
    }

    public async Task<ChannelSearchResponse> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var page = await LoadOptionalAsync($"/en/search/{Uri.EscapeDataString(query)}", cancellationToken);
        if (page is not null)
        {
            return new ChannelSearchResponse(
                Code,
                query,
                DateTimeOffset.UtcNow,
                false,
                ParseItems(page),
                "MISSAV 实时搜索结果。");
        }

        var home = await GetHomeAsync(cancellationToken);
        var items = home.Items.Where(item =>
            item.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            item.Id.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        return new ChannelSearchResponse(
            Code,
            query,
            DateTimeOffset.UtcNow,
            false,
            items,
            "上游搜索触发反爬，本次退化为首页结果过滤。");
    }

    public async Task<ChannelDetailResponse?> GetDetailAsync(string itemId, CancellationToken cancellationToken)
    {
        var page = await LoadOptionalAsync(HtmlChannelUtilities.DecodePath(itemId), cancellationToken);
        if (page is null)
        {
            return null;
        }

        var title = HtmlChannelUtilities.Meta(page.Document, "og:title") ??
                    HtmlChannelUtilities.Text(page.Document.QuerySelector("h1")) ?? itemId;
        var description = HtmlChannelUtilities.Meta(page.Document, "og:description") ??
                          HtmlChannelUtilities.Meta(page.Document, "description");
        var cover = HtmlChannelUtilities.AbsoluteUrl(
            HtmlChannelUtilities.Meta(page.Document, "og:image"), page.Uri);
        var duration = Duration(page);
        var isPlayable = HtmlChannelUtilities.UnpackFirstMediaUrl(page.Html) is not null;

        return new ChannelDetailResponse(
            Code,
            Name,
            itemId,
            title,
            cover,
            description,
            "JAV",
            ExtractCode(page.Uri.AbsolutePath),
            1,
            null,
            true,
            false,
            0,
            DateTimeOffset.UtcNow,
            false,
            [new ChannelEpisodeResponse("main", title, duration, true, isPlayable)]);
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

        var page = await LoadOptionalAsync(HtmlChannelUtilities.DecodePath(itemId), cancellationToken);
        if (page is null)
        {
            return null;
        }

        var title = HtmlChannelUtilities.Meta(page.Document, "og:title") ?? itemId;
        var source = HtmlChannelUtilities.UnpackFirstMediaUrl(page.Html);
        return new ChannelPlaySource(
            episodeId,
            title,
            source is not null,
            source,
            source is null ? null : "application/vnd.apple.mpegurl",
            "video",
            page.Uri.ToString(),
            "curl");
    }

    private IReadOnlyList<ChannelItemResponse> ParseItems(HtmlChannelPage page)
    {
        var items = new List<ChannelItemResponse>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var card in page.Document.QuerySelectorAll("div.thumbnail"))
        {
            var anchor = card.QuerySelector("div.my-2 a[href]") ?? card.QuerySelector("a[href]");
            var path = HtmlChannelUtilities.NormalizePath(anchor?.GetAttribute("href"), page.Uri);
            if (path is null || !paths.Add(path))
            {
                continue;
            }

            var image = card.QuerySelector("img");
            var title = HtmlChannelUtilities.Text(anchor) ?? image?.GetAttribute("alt");
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var duration = HtmlChannelUtilities.Text(card.QuerySelector("span.absolute"));
            items.Add(new ChannelItemResponse(
                HtmlChannelUtilities.EncodePath(path),
                title,
                null,
                HtmlChannelUtilities.AbsoluteUrl(
                    image?.GetAttribute("data-src") ?? image?.GetAttribute("src"), page.Uri),
                duration is null ? null : $"时长 {duration}",
                "JAV",
                ExtractCode(path),
                1,
                null));
            if (items.Count == 24)
            {
                break;
            }
        }

        return items;
    }

    private async Task<HtmlChannelPage> LoadRequiredAsync(string path, CancellationToken cancellationToken) =>
        await LoadOptionalAsync(path, cancellationToken) ??
        throw new HttpRequestException("所有 MISSAV 节点均调用失败。");

    private async Task<HtmlChannelPage?> LoadOptionalAsync(string path, CancellationToken cancellationToken)
    {
        var endpoints = configuration.GetSection("Channels:MissAv:Endpoints").Get<string[]>() ?? [];
        foreach (var endpoint in endpoints)
        {
            var uri = new Uri(new Uri(endpoint), path);
            try
            {
                var page = await HtmlChannelUtilities.LoadAsync(httpClient, uri, cancellationToken);
                logger.LogInformation("MISSAV 页面解析成功：{Host} {Path}", page.Uri.Host, page.Uri.AbsolutePath);
                return page;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "MISSAV 节点失败：{Host}", uri.Host);
            }
        }

        return null;
    }

    private static string ExtractCode(string path) =>
        path.Trim('/').Split('/').LastOrDefault()?.ToUpperInvariant() ?? "MISSAV";

    private static string? Duration(HtmlChannelPage page)
    {
        var raw = HtmlChannelUtilities.Meta(page.Document, "og:video:duration");
        return int.TryParse(raw, out var seconds) ? TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss") : null;
    }
}
