using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AvPlatform.WebApi.Models;
using Microsoft.AspNetCore.WebUtilities;

namespace AvPlatform.WebApi.Channels;

/// <summary>AIJAV 加密 API 渠道。</summary>
public sealed class AiJavChannelAdapter(
    GdApiClient apiClient,
    IConfiguration configuration) : IChannelAdapter
{
    private const string EpisodeId = "main";
    private const string PlaySecret = "FoEDb2QIeVvUOyTlBJ9NMDYgJFNZ30";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string Code => "aijav";
    public string Name => "AIJAV";
    public string Mode => "加密 API";

    public async Task<ChannelHomeResponse> GetHomeAsync(CancellationToken cancellationToken)
    {
        using var response = await apiClient.PostAsync(
            "video/lists",
            new Dictionary<string, object?>
            {
                ["page"] = 1,
                ["limit"] = 27,
                ["type"] = 4
            },
            cancellationToken);

        return new ChannelHomeResponse(
            Code,
            Name,
            Mode,
            DateTimeOffset.UtcNow,
            false,
            null,
            MapItems(response.RootElement),
            "详情由列表数据生成，播放地址在请求播放时实时签名。");
    }

    public async Task<ChannelSearchResponse> SearchAsync(
        string query,
        CancellationToken cancellationToken)
    {
        using var response = await apiClient.PostAsync(
            "video/lists",
            new Dictionary<string, object?>
            {
                ["page"] = 1,
                ["limit"] = 24,
                ["keyword"] = query
            },
            cancellationToken);

        return new ChannelSearchResponse(
            Code,
            query,
            DateTimeOffset.UtcNow,
            false,
            MapItems(response.RootElement),
            "AIJAV 实时搜索结果。");
    }

    public Task<ChannelDetailResponse?> GetDetailAsync(
        string itemId,
        CancellationToken cancellationToken)
    {
        var item = DecodeItem(itemId);
        if (item is null)
        {
            return Task.FromResult<ChannelDetailResponse?>(null);
        }

        var playable = CreatePlayUri(item.Thumb) is not null;
        var detail = new ChannelDetailResponse(
            Code,
            Name,
            itemId,
            item.Title,
            CoverUrl(item.Thumb),
            BuildDetailSummary(item),
            item.Publisher ?? "视频",
            item.Actor ?? item.Director,
            1,
            item.Play ?? item.CollectCount ?? item.Rating,
            true,
            false,
            0,
            DateTimeOffset.UtcNow,
            false,
            [new ChannelEpisodeResponse(EpisodeId, item.Title, item.Duration, true, playable)]);
        return Task.FromResult<ChannelDetailResponse?>(detail);
    }

    public Task<ChannelPlaySource?> GetPlayAsync(
        string itemId,
        string episodeId,
        CancellationToken cancellationToken)
    {
        if (episodeId != EpisodeId || DecodeItem(itemId) is not { } item)
        {
            return Task.FromResult<ChannelPlaySource?>(null);
        }

        var source = CreatePlayUri(item.Thumb)?.ToString();
        return Task.FromResult<ChannelPlaySource?>(new ChannelPlaySource(
            EpisodeId,
            item.Title,
            source is not null,
            source,
            source is null ? null : "application/vnd.apple.mpegurl",
            "video"));
    }

    private IReadOnlyList<ChannelItemResponse> MapItems(JsonElement root)
    {
        if (!TryGetVideoList(root, out var videos))
        {
            return [];
        }

        var items = new List<ChannelItemResponse>();
        foreach (var video in videos.EnumerateArray())
        {
            var upstreamId = Text(video, "id");
            var title = Text(video, "title");
            if (string.IsNullOrWhiteSpace(upstreamId) || string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var token = new AiJavItemToken(
                upstreamId,
                title,
                Text(video, "thumb"),
                Text(video, "duration"),
                Text(video, "mash"),
                Text(video, "actor"),
                Text(video, "director"),
                Text(video, "publisher"),
                Number(video, "rating_avg"),
                Number(video, "collect_count"),
                Number(video, "play"));
            items.Add(new ChannelItemResponse(
                EncodeItem(token),
                token.Title,
                null,
                CoverUrl(token.Thumb),
                BuildCardSummary(token),
                token.Publisher ?? "视频",
                token.Actor ?? token.Director,
                1,
                token.Play ?? token.CollectCount ?? token.Rating));
        }

        return items;
    }

    private static bool TryGetVideoList(JsonElement root, out JsonElement videos)
    {
        videos = default;
        if (!root.TryGetProperty("data", out var data))
        {
            return false;
        }
        if (data.ValueKind == JsonValueKind.Array)
        {
            videos = data;
            return true;
        }

        return data.ValueKind == JsonValueKind.Object &&
               data.TryGetProperty("data", out videos) &&
               videos.ValueKind == JsonValueKind.Array;
    }

    private Uri? CreatePlayUri(string? thumb)
    {
        var mediaUri = MediaUri(thumb);
        if (mediaUri is null)
        {
            return null;
        }

        var slash = mediaUri.AbsolutePath.LastIndexOf('/');
        if (slash < 0)
        {
            return null;
        }

        var unsigned = new Uri(mediaUri, mediaUri.AbsolutePath[..slash] + "/hls/1/index.m3u8");
        var wsTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 7200;
        var source = PlaySecret + unsigned.AbsolutePath + wsTime.ToString(CultureInfo.InvariantCulture);
        var wsSecret = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
        var builder = new UriBuilder(unsigned)
        {
            Query = $"wsSecret={wsSecret}&wsTime={wsTime}&ip={RandomIp()}"
        };
        return builder.Uri;
    }

    private string? CoverUrl(string? thumb)
    {
        if (Uri.TryCreate(thumb, UriKind.Absolute, out var absolute) &&
            absolute.Scheme is "http" or "https")
        {
            return absolute.ToString();
        }

        return MediaUri(thumb)?.ToString();
    }

    private Uri? MediaUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var originValue = configuration.GetValue("Channels:AiJav:MediaOrigin", "https://ut.lnh7.com");
        if (!Uri.TryCreate(originValue, UriKind.Absolute, out var origin) ||
            origin.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("AIJAV 媒体节点地址无效。");
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute) &&
            absolute.Scheme is "http" or "https")
        {
            return new Uri(origin, absolute.AbsolutePath);
        }

        return Uri.TryCreate(origin, value.TrimStart('/'), out var relative) ? relative : null;
    }

    private static string EncodeItem(AiJavItemToken item) =>
        WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(item, JsonOptions));

    private static AiJavItemToken? DecodeItem(string itemId)
    {
        if (itemId.Length is 0 or > 4096)
        {
            return null;
        }

        try
        {
            var item = JsonSerializer.Deserialize<AiJavItemToken>(
                WebEncoders.Base64UrlDecode(itemId),
                JsonOptions);
            return item is not null && item.Id.Length > 0 && item.Title.Length > 0 ? item : null;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return null;
        }
    }

    private static string? BuildCardSummary(AiJavItemToken item)
    {
        var parts = new[]
        {
            string.IsNullOrWhiteSpace(item.Mash) ? null : $"番号 {item.Mash}",
            string.IsNullOrWhiteSpace(item.Duration) ? null : $"时长 {item.Duration}"
        };
        var summary = string.Join(" · ", parts.Where(part => part is not null));
        return summary.Length == 0 ? null : summary;
    }

    private static string? BuildDetailSummary(AiJavItemToken item)
    {
        var parts = new[]
        {
            string.IsNullOrWhiteSpace(item.Mash) ? null : $"番号：{item.Mash}",
            string.IsNullOrWhiteSpace(item.Director) ? null : $"导演：{item.Director}",
            string.IsNullOrWhiteSpace(item.Publisher) ? null : $"发行：{item.Publisher}"
        };
        var summary = string.Join("；", parts.Where(part => part is not null));
        return summary.Length == 0 ? null : summary;
    }

    private static string? Text(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return ElementText(property);
    }

    private static string? ElementText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => Clean(value.GetString()),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.Array => Join(value.EnumerateArray().Select(ElementText)),
        JsonValueKind.Object when value.TryGetProperty("name", out var name) => ElementText(name),
        _ => null
    };

    private static decimal? Number(JsonElement value, string propertyName) =>
        decimal.TryParse(Text(value, propertyName), NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Join(IEnumerable<string?> values)
    {
        var joined = string.Join("、", values.Where(value => !string.IsNullOrWhiteSpace(value)));
        return joined.Length == 0 ? null : joined;
    }

    private static string RandomIp() => string.Join('.', Enumerable.Range(0, 4)
        .Select(_ => RandomNumberGenerator.GetInt32(1, 255)));

    private sealed record AiJavItemToken(
        [property: JsonPropertyName("i")] string Id,
        [property: JsonPropertyName("t")] string Title,
        [property: JsonPropertyName("u")] string? Thumb,
        [property: JsonPropertyName("d")] string? Duration,
        [property: JsonPropertyName("m")] string? Mash,
        [property: JsonPropertyName("a")] string? Actor,
        [property: JsonPropertyName("r")] string? Director,
        [property: JsonPropertyName("p")] string? Publisher,
        [property: JsonPropertyName("g")] decimal? Rating,
        [property: JsonPropertyName("c")] decimal? CollectCount,
        [property: JsonPropertyName("v")] decimal? Play);
}
