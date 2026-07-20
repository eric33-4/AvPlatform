using System.Globalization;
using System.Text;
using System.Text.Json;
using AvPlatform.WebApi.Models;

namespace AvPlatform.WebApi.Channels;

/// <summary>把 YXFM 私有字段压平为平台统一模型。</summary>
internal static class YxfmResponseMapper
{
    private static readonly string[] MojibakeMarkers = ["鏉", "鐨", "鍏", "绔", "鎴", "濂", "璇", "涓", "锟"];

    static YxfmResponseMapper() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static IReadOnlyList<ChannelItemResponse> MapHome(JsonElement data)
    {
        var items = new List<ChannelItemResponse>();
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var property in data.EnumerateObject())
        {
            if (!property.Name.EndsWith("AlbumList", StringComparison.Ordinal) ||
                property.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var album in property.Value.EnumerateArray())
            {
                var id = Text(album, "radio_album_id");
                if (string.IsNullOrWhiteSpace(id) || !ids.Add(id))
                {
                    continue;
                }

                items.Add(new ChannelItemResponse(
                    id,
                    Text(album, "name") ?? $"专辑 {id}",
                    null,
                    Text(album, "cover_img") ?? Text(album, "album_img"),
                    Text(album, "desc"),
                    Category(album) ?? ListName(property.Name),
                    Text(album, "host_name"),
                    Integer(album, "radio_count") ?? Integer(album, "num"),
                    Decimal(album, "hot_number") ?? Decimal(album, "views")));
            }
        }

        return items;
    }

    public static ChannelDetailResponse MapDetail(
        JsonElement data,
        string channelCode,
        string channelName)
    {
        var id = Text(data, "radio_album_id") ?? throw new InvalidOperationException("YXFM 详情缺少专辑 ID。");
        var episodes = MapEpisodes(data);
        var declaredCount = Integer(data, "radio_count") ?? episodes.Count;
        var price = Decimal(data, "price");

        return new ChannelDetailResponse(
            channelCode,
            channelName,
            id,
            Text(data, "name") ?? $"专辑 {id}",
            Text(data, "cover_img"),
            Text(data, "desc"),
            Category(data),
            ObjectText(data, "host", "name"),
            declaredCount,
            Decimal(data, "hot_number") ?? Decimal(data, "views"),
            Text(data, "is_finished") == "1",
            episodes.Any(x => !x.IsFree),
            price,
            DateTimeOffset.UtcNow,
            false,
            episodes);
    }

    internal static string? RepairText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !MojibakeMarkers.Any(value.Contains))
        {
            return value;
        }

        try
        {
            var bytes = Encoding.GetEncoding("GB18030").GetBytes(value);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return value;
        }
        catch (EncoderFallbackException)
        {
            return value;
        }
    }

    private static IReadOnlyList<ChannelEpisodeResponse> MapEpisodes(JsonElement data)
    {
        if (!data.TryGetProperty("radio_list", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var episodes = new List<ChannelEpisodeResponse>();
        foreach (var episode in list.EnumerateArray())
        {
            var id = Text(episode, "radio_id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var isFree = Text(episode, "is_free") == "1";
            var hls = Text(episode, "url");
            var mp3 = Text(episode, "down_url");

            episodes.Add(new ChannelEpisodeResponse(
                id,
                Text(episode, "name") ?? $"剧集 {id}",
                Text(episode, "duration"),
                isFree,
                isFree && (hls is not null || mp3 is not null)));
        }

        return episodes;
    }

    public static ChannelPlaySource? MapPlaySource(JsonElement data, string episodeId)
    {
        if (!data.TryGetProperty("radio_list", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var episode in list.EnumerateArray())
        {
            if (Text(episode, "radio_id") != episodeId)
            {
                continue;
            }

            var title = Text(episode, "name") ?? $"剧集 {episodeId}";
            if (Text(episode, "is_free") != "1")
            {
                return new ChannelPlaySource(episodeId, title, false, null, null, "audio");
            }

            var hls = Text(episode, "url");
            if (!string.IsNullOrWhiteSpace(hls))
            {
                return new ChannelPlaySource(
                    episodeId,
                    title,
                    true,
                    hls,
                    "application/vnd.apple.mpegurl",
                    "audio");
            }

            var mp3 = Text(episode, "down_url");
            return new ChannelPlaySource(
                episodeId,
                title,
                !string.IsNullOrWhiteSpace(mp3),
                mp3,
                string.IsNullOrWhiteSpace(mp3) ? null : "audio/mpeg",
                "audio");
        }

        return null;
    }

    private static string? Category(JsonElement value) =>
        ObjectText(value, "categorys", "child_category", "name") ??
        ObjectText(value, "categorys", "category", "name");

    private static string ListName(string propertyName) => propertyName switch
    {
        "longbookAlbumList" => "长篇",
        "shortbookAlbumList" => "短篇",
        "newAlbumList" => "最新",
        "bestAlbumList" => "精选",
        "likeAlbumList" => "猜你喜欢",
        _ => "专辑"
    };

    private static string? Text(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => RepairText(property.GetString()),
            JsonValueKind.Number => property.GetRawText(),
            _ => null
        };
    }

    private static string? ObjectText(JsonElement value, params string[] path)
    {
        var current = value;
        foreach (var part in path)
        {
            if (!current.TryGetProperty(part, out current))
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.String ? RepairText(current.GetString()) : null;
    }

    private static int? Integer(JsonElement value, string propertyName) =>
        int.TryParse(Text(value, propertyName), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;

    private static decimal? Decimal(JsonElement value, string propertyName) =>
        decimal.TryParse(Text(value, propertyName), NumberStyles.Number, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;
}
