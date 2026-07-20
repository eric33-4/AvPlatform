using System.Globalization;
using System.Text.Json;
using AvPlatform.WebApi.Models;

namespace AvPlatform.WebApi.Channels;

/// <summary>ONE 视频渠道。</summary>
public sealed class OneChannelAdapter(
    OneApiClient apiClient,
    YueShuGeConfigProvider configProvider,
    IConfiguration configuration) : IChannelAdapter
{
    public string Code => "one";
    public string Name => "ONE";
    public string Mode => "签名加密 API";

    public async Task<ChannelHomeResponse> GetHomeAsync(CancellationToken cancellationToken)
    {
        var imageOrigin = await ImageOriginAsync(cancellationToken);
        using var response = await apiClient.PostAsync(
            "v2.5/article/discovery",
            new Dictionary<string, object?>
            {
                ["page"] = 1,
                ["size"] = 20,
                ["model_id"] = 6,
                ["demand_tag_id"] = 0,
                ["sort"] = "published_at",
                ["published_at"] = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            },
            cancellationToken);

        return new ChannelHomeResponse(
            Code,
            Name,
            Mode,
            DateTimeOffset.UtcNow,
            false,
            null,
            MapItems(response.RootElement.GetProperty("data"), imageOrigin),
            "当前展示 ONE 最新点播内容，详情和播放地址实时读取。");
    }

    public async Task<ChannelSearchResponse> SearchAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var imageOrigin = await ImageOriginAsync(cancellationToken);
        using var response = await apiClient.PostAsync(
            "v2.5/article/search",
            new Dictionary<string, object?>
            {
                ["size"] = 20,
                ["page"] = 1,
                ["model_id"] = 0,
                ["keyword"] = query
            },
            cancellationToken);

        return new ChannelSearchResponse(
            Code,
            query,
            DateTimeOffset.UtcNow,
            false,
            MapItems(response.RootElement.GetProperty("data"), imageOrigin),
            "ONE 实时搜索结果。");
    }

    public async Task<ChannelDetailResponse?> GetDetailAsync(
        string itemId,
        CancellationToken cancellationToken)
    {
        using var response = await LoadDetailAsync(itemId, cancellationToken);
        if (response is null)
        {
            return null;
        }

        var data = response.RootElement.GetProperty("data");
        var imageOrigin = await ImageOriginAsync(cancellationToken);
        var price = Number(data, "coin") ?? Number(data, "vip_coin");
        var isPaid = price > 0 && Text(data, "is_limit_free") != "1";
        var episodes = MapEpisodes(data, isPaid);
        return new ChannelDetailResponse(
            Code,
            Name,
            itemId,
            Text(data, "title") ?? itemId,
            AbsoluteUrl(imageOrigin, Text(data, "thumb") ?? Text(data, "thumbnail")),
            Text(data, "description") ?? Text(data, "subtitle"),
            Category(data),
            Text(data, "actor") ?? Text(data, "author"),
            episodes.Count,
            Number(data, "views"),
            true,
            isPaid,
            price,
            DateTimeOffset.UtcNow,
            false,
            episodes);
    }

    public async Task<ChannelPlaySource?> GetPlayAsync(
        string itemId,
        string episodeId,
        CancellationToken cancellationToken)
    {
        using var response = await LoadDetailAsync(itemId, cancellationToken);
        if (response is null)
        {
            return null;
        }

        var data = response.RootElement.GetProperty("data");
        var mediaOrigin = await MediaOriginAsync(cancellationToken);
        var title = Text(data, "title") ?? itemId;
        return episodeId switch
        {
            "video" => PlaySource(
                episodeId,
                title,
                mediaOrigin,
                Text(data, "video_hls") ??
                Text(data, "video_file") ??
                Text(data, "video_hls_h265"),
                "video"),
            "audio" => PlaySource(
                episodeId,
                title,
                mediaOrigin,
                Text(data, "audio_hls") ?? Text(data, "audio_file"),
                "audio"),
            _ => null
        };
    }

    private async Task<JsonDocument?> LoadDetailAsync(
        string itemId,
        CancellationToken cancellationToken)
    {
        if (!long.TryParse(itemId, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            return null;
        }

        return await apiClient.PostAsync(
            "v2.5/article/detail",
            new Dictionary<string, object?> { ["id"] = id },
            cancellationToken);
    }

    private static IReadOnlyList<ChannelItemResponse> MapItems(JsonElement data, Uri imageOrigin)
    {
        if (data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var items = new List<ChannelItemResponse>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in data.EnumerateArray())
        {
            var id = Text(item, "id");
            var title = Text(item, "title");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title) || !ids.Add(id))
            {
                continue;
            }

            items.Add(new ChannelItemResponse(
                id,
                title,
                null,
                AbsoluteUrl(imageOrigin, Text(item, "thumb") ?? Text(item, "thumbnail")),
                Text(item, "subtitle") ?? Text(item, "description") ?? Text(item, "number"),
                Category(item),
                Text(item, "actor") ?? Text(item, "author"),
                1,
                Number(item, "views")));
        }
        return items;
    }

    private static IReadOnlyList<ChannelEpisodeResponse> MapEpisodes(JsonElement data, bool isPaid)
    {
        var episodes = new List<ChannelEpisodeResponse>();
        var title = Text(data, "title") ?? "播放";
        var duration = Text(data, "video_length") ?? Text(data, "length");
        if (First(data, "video_hls", "video_file", "video_hls_h265") is not null)
        {
            episodes.Add(new ChannelEpisodeResponse("video", title, duration, !isPaid, true));
        }
        if (First(data, "audio_hls", "audio_file") is not null)
        {
            episodes.Add(new ChannelEpisodeResponse("audio", title, duration, !isPaid, true));
        }
        return episodes;
    }

    private static ChannelPlaySource PlaySource(
        string episodeId,
        string title,
        Uri mediaOrigin,
        string? source,
        string mediaKind)
    {
        var url = AbsoluteUrl(mediaOrigin, source);
        var isHls = url?.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) == true;
        return new ChannelPlaySource(
            episodeId,
            title,
            url is not null,
            url,
            url is null
                ? null
                : isHls ? "application/vnd.apple.mpegurl" : mediaKind == "audio" ? "audio/mpeg" : "video/mp4",
            mediaKind);
    }

    private async Task<Uri> ImageOriginAsync(CancellationToken cancellationToken) =>
        Origin(
            await configProvider.GetHostAsync("one_img", cancellationToken) ??
            configuration.GetValue("Channels:One:ImageOrigin", "https://enimg807.5pkwjhp.com/"),
            "ONE 图片节点");

    private async Task<Uri> MediaOriginAsync(CancellationToken cancellationToken) =>
        Origin(
            await configProvider.GetHostAsync("one_video", cancellationToken) ??
            configuration.GetValue("Channels:One:MediaOrigin", "https://dlmk0129.bx7qxb.com/"),
            "ONE 媒体节点");

    private static Uri Origin(string value, string name) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? uri
            : throw new InvalidOperationException($"{name}无效。");

    private static string? AbsoluteUrl(Uri origin, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        return Uri.TryCreate(value, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https"
            ? absolute.ToString()
            : new Uri(origin, value.TrimStart('/')).ToString();
    }

    private static string Category(JsonElement value) => Text(value, "model_id") switch
    {
        "1" => "图文",
        "6" => "视频",
        _ => "ONE 内容"
    };

    private static string? First(JsonElement value, params string[] names) =>
        names.Select(name => Text(value, name)).FirstOrDefault(item => !string.IsNullOrWhiteSpace(item));

    private static string? Text(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var property))
        {
            return null;
        }
        return property.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(property.GetString()) ? null : property.GetString()!.Trim(),
            JsonValueKind.Number => property.GetRawText(),
            _ => null
        };
    }

    private static decimal? Number(JsonElement value, string propertyName) =>
        decimal.TryParse(Text(value, propertyName), NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;
}
