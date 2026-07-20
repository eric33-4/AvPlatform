using System.Text.Json;
using System.Text.RegularExpressions;
using AvPlatform.WebApi.Models;
using Microsoft.AspNetCore.WebUtilities;

namespace AvPlatform.WebApi.Channels;

/// <summary>SFTV HTML 渠道。</summary>
public sealed partial class SftvChannelAdapter(
    YueShuGeHtmlClient htmlClient,
    YueShuGeConfigProvider configProvider) : IChannelAdapter
{
    private const string EpisodeId = "main";
    private static readonly YueShuGeHtmlChannelProfile Profile = new(
        "SFTV",
        "sftv",
        "Sftv",
        "token_sftv",
        "Mozilla/5.0 (iPhone; CPU iPhone OS 18_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.5 Mobile/15E148 Safari/604.1",
        "zh-CN,zh;q=0.9");

    public string Code => "sftv";
    public string Name => "SFTV";
    public string Mode => "HTML Parse";

    public async Task<ChannelHomeResponse> GetHomeAsync(CancellationToken cancellationToken)
    {
        var page = await htmlClient.LoadAsync(
            Profile,
            "/index.php?ch=listA&class=New&now_page=1",
            cancellationToken);
        return new ChannelHomeResponse(
            Code,
            Name,
            Mode,
            DateTimeOffset.UtcNow,
            false,
            null,
            ParseItems(page),
            "播放地址由中央 Cookie、jindex.php 和 iframe 实时解析。");
    }

    public async Task<ChannelSearchResponse> SearchAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var page = await htmlClient.LoadAsync(
            Profile,
            $"/?ch=smov&sop=Searched&serached={Uri.EscapeDataString(query)}&now_page=1",
            cancellationToken);
        return new ChannelSearchResponse(
            Code,
            query,
            DateTimeOffset.UtcNow,
            false,
            ParseItems(page),
            "SFTV 实时搜索结果。");
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

        var title = HtmlChannelUtilities.Text(page.Document.QuerySelector(".Vview > .name.WF")) ?? itemId;
        var cover = HtmlChannelUtilities.AbsoluteUrl(
            page.Document.QuerySelector(".pics img")?.GetAttribute("src") ??
            BackgroundImageRegex().Match(page.Html).Groups["url"].Value,
            page.Uri);
        var duration = HtmlChannelUtilities.Text(page.Document.QuerySelector(".detailslist .icon-time"));
        var category = HtmlChannelUtilities.Text(page.Document.QuerySelector(".playpoint .type"));
        var popularity = HtmlChannelUtilities.ParseCompactNumber(
            HtmlChannelUtilities.Text(page.Document.QuerySelector(".details > .count")));
        var playable = SpCodeFromItem(itemId) is not null &&
                       !string.IsNullOrWhiteSpace(await configProvider.GetTokenAsync("token_sftv", cancellationToken));
        return new ChannelDetailResponse(
            Code,
            Name,
            itemId,
            title,
            cover,
            HtmlChannelUtilities.Text(page.Document.QuerySelector(".intro")),
            category ?? "视频",
            null,
            1,
            popularity,
            true,
            false,
            0,
            DateTimeOffset.UtcNow,
            false,
            [new ChannelEpisodeResponse(EpisodeId, title, duration, true, playable)]);
    }

    public async Task<ChannelPlaySource?> GetPlayAsync(
        string itemId,
        string episodeId,
        CancellationToken cancellationToken)
    {
        if (episodeId != EpisodeId || await LoadDetailAsync(itemId, cancellationToken) is not { } page ||
            SpCodeFromItem(itemId) is not { } spCode ||
            await configProvider.GetTokenAsync("token_sftv", cancellationToken) is not { } cookie ||
            Group(SessionIdRegex().Match(cookie), "value") is not { } postId)
        {
            return null;
        }

        using var result = await htmlClient.PostFormAsync(
            Profile,
            "/jindex.php",
            new Dictionary<string, string>
            {
                ["SPCode"] = spCode,
                ["func"] = "new_play",
                ["op"] = "do_playts",
                ["post_id"] = postId
            },
            page.Uri.ToString(),
            cancellationToken);
        var root = result.RootElement;
        if (!IsSuccess(root) || !root.TryGetProperty("video_url", out var videoHtml) ||
            videoHtml.ValueKind != JsonValueKind.String)
        {
            return new ChannelPlaySource(EpisodeId, spCode, false, null, null, "video");
        }

        var fragment = videoHtml.GetString()!;
        var source = ExtractMediaUrl(fragment, page.Uri);
        var referrer = page.Uri.ToString();
        if (source is null && Group(IframeRegex().Match(fragment), "url") is { } iframeValue &&
            Uri.TryCreate(page.Uri, iframeValue, out var iframeUri))
        {
            var iframe = await htmlClient.LoadAbsoluteAsync(
                Profile,
                iframeUri,
                page.Uri.ToString(),
                cancellationToken);
            source = ExtractMediaUrl(iframe.Html, iframe.Uri);
            referrer = iframe.Uri.ToString();
        }

        var title = HtmlChannelUtilities.Text(page.Document.QuerySelector(".Vview > .name.WF")) ?? spCode;
        return new ChannelPlaySource(
            EpisodeId,
            title,
            source is not null,
            source,
            MediaType(source),
            "video",
            referrer);
    }

    private static IReadOnlyList<ChannelItemResponse> ParseItems(HtmlChannelPage page)
    {
        var items = new List<ChannelItemResponse>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var card in page.Document.QuerySelectorAll(".listA > a, .listB > a"))
        {
            var path = HtmlChannelUtilities.NormalizePath(card.GetAttribute("href"), page.Uri);
            var title = HtmlChannelUtilities.Text(card.QuerySelector(".name"));
            if (path is null || title is null || !path.Contains("spcode=", StringComparison.OrdinalIgnoreCase) ||
                !seen.Add(path))
            {
                continue;
            }

            var cover = HtmlChannelUtilities.AbsoluteUrl(
                card.QuerySelector("img")?.GetAttribute("data-original") ??
                card.QuerySelector("img")?.GetAttribute("src"),
                page.Uri);
            var duration = HtmlChannelUtilities.Text(card.QuerySelector(".icon-time"));
            var code = HtmlChannelUtilities.Text(card.QuerySelector(".icon-barcode"));
            items.Add(new ChannelItemResponse(
                HtmlChannelUtilities.EncodePath(path),
                title,
                null,
                cover,
                string.Join(" · ", new[] { code, duration }.Where(value => !string.IsNullOrWhiteSpace(value))),
                "视频",
                null,
                1,
                HtmlChannelUtilities.ParseCompactNumber(HtmlChannelUtilities.Text(card.QuerySelector(".vs")))));
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

    private static string? ExtractMediaUrl(string html, Uri pageUri)
    {
        var value = Group(VideoSourceRegex().Match(html), "url") ??
                    Group(LooseMediaRegex().Match(html), "url");
        return HtmlChannelUtilities.AbsoluteUrl(value, pageUri);
    }

    private static bool IsSuccess(JsonElement root)
    {
        if (!root.TryGetProperty("err", out var value))
        {
            return false;
        }
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number == 111 ||
               value.ValueKind == JsonValueKind.String && value.GetString() == "111";
    }

    private static string? Group(Match match, string name) =>
        match.Success && !string.IsNullOrWhiteSpace(match.Groups[name].Value)
            ? match.Groups[name].Value
            : null;

    private static string? MediaType(string? source) =>
        source?.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) == true
            ? "application/vnd.apple.mpegurl"
            : source is null ? null : "video/mp4";

    private static string? SpCodeFromItem(string itemId)
    {
        var path = HtmlChannelUtilities.DecodePath(itemId);
        var queryIndex = path.IndexOf('?', StringComparison.Ordinal);
        if (queryIndex < 0)
        {
            return null;
        }
        var query = QueryHelpers.ParseQuery(path[queryIndex..]);
        var value = query.TryGetValue("spcode", out var values) ? values.ToString() : null;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    [GeneratedRegex("(?:^|;\\s*)PHPSESSID=(?<value>[^;]+)", RegexOptions.IgnoreCase)]
    private static partial Regex SessionIdRegex();

    [GeneratedRegex("<iframe[^>]+src=[\"'](?<url>[^\"']+)", RegexOptions.IgnoreCase)]
    private static partial Regex IframeRegex();

    [GeneratedRegex("<video[^>]*src=[\"'](?<url>[^\"']+)", RegexOptions.IgnoreCase)]
    private static partial Regex VideoSourceRegex();

    [GeneratedRegex("(?:src|source)\\s*[=:]\\s*[\"'](?<url>https?://[^\"']+\\.(?:m3u8|mp4)[^\"']*)", RegexOptions.IgnoreCase)]
    private static partial Regex LooseMediaRegex();

    [GeneratedRegex("background-image:\\s*url\\([\"']?(?<url>[^\"')]+)", RegexOptions.IgnoreCase)]
    private static partial Regex BackgroundImageRegex();
}
