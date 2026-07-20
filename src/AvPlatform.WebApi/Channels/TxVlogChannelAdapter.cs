using System.Globalization;
using System.Text.Json;
using AvPlatform.WebApi.Models;

namespace AvPlatform.WebApi.Channels;

/// <summary>TXVLOG 视频渠道。</summary>
public sealed class TxVlogChannelAdapter(TxVlogApiClient apiClient) : IChannelAdapter
{
    public string Code => "txvlog";
    public string Name => "TXVLOG";
    public string Mode => "AES 加密 API";

    public async Task<ChannelHomeResponse> GetHomeAsync(CancellationToken cancellationToken)
    {
        using var response = await apiClient.PostAsync(
            "h5/movie/block",
            new Dictionary<string, object?>
            {
                ["position"] = "app_home_tj",
                ["page"] = 1
            },
            cancellationToken);

        return new ChannelHomeResponse(
            Code,
            Name,
            Mode,
            DateTimeOffset.UtcNow,
            false,
            null,
            MapHome(response.Document.RootElement.GetProperty("data")),
            "已过滤首页广告块，只保留真实视频。");
    }

    public async Task<ChannelSearchResponse> SearchAsync(
        string query,
        CancellationToken cancellationToken)
    {
        using var response = await apiClient.PostAsync(
            "h5/movie/search",
            new Dictionary<string, object?>
            {
                ["keywords"] = query,
                ["page"] = 1
            },
            cancellationToken);

        return new ChannelSearchResponse(
            Code,
            query,
            DateTimeOffset.UtcNow,
            false,
            MapVideos(response.Document.RootElement.GetProperty("data"), null),
            "TXVLOG 实时搜索结果。");
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

        var data = response.Document.RootElement.GetProperty("data");
        var title = Text(data, "name") ?? itemId;
        var source = PlayUrl(response.BaseUri, Text(data, "play_link"));
        var payType = Text(data, "pay_type");
        var isPaid = payType is not null && payType is not ("free" or "0");
        return new ChannelDetailResponse(
            Code,
            Name,
            itemId,
            title,
            Text(data, "img"),
            Text(data, "description"),
            Text(data, "cat_name") ?? Tags(data) ?? "视频",
            Text(data, "nickname"),
            1,
            CompactNumber(Text(data, "click")),
            true,
            isPaid,
            Number(data, "money"),
            DateTimeOffset.UtcNow,
            false,
            [new ChannelEpisodeResponse(
                "main",
                title,
                Text(data, "duration"),
                !isPaid,
                source is not null)]);
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

        using var response = await LoadDetailAsync(itemId, cancellationToken);
        if (response is null)
        {
            return null;
        }

        var data = response.Document.RootElement.GetProperty("data");
        var source = PlayUrl(response.BaseUri, Text(data, "play_link"));
        return new ChannelPlaySource(
            episodeId,
            Text(data, "name") ?? itemId,
            source is not null,
            source,
            source is null ? null : "application/vnd.apple.mpegurl",
            "video");
    }

    private async Task<TxVlogApiResponse?> LoadDetailAsync(
        string itemId,
        CancellationToken cancellationToken)
    {
        if (!long.TryParse(itemId, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            return null;
        }

        return await apiClient.PostAsync(
            "h5/movie/detail",
            new Dictionary<string, object?> { ["id"] = itemId },
            cancellationToken);
    }

    private static IReadOnlyList<ChannelItemResponse> MapHome(JsonElement blocks)
    {
        var items = new List<ChannelItemResponse>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var block in blocks.EnumerateArray())
        {
            if (!block.TryGetProperty("items", out var videos) || videos.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in MapVideos(videos, Text(block, "name")))
            {
                if (ids.Add(item.Id))
                {
                    items.Add(item);
                }
                if (items.Count == 30)
                {
                    return items;
                }
            }
        }
        return items;
    }

    private static IReadOnlyList<ChannelItemResponse> MapVideos(JsonElement videos, string? kind)
    {
        if (videos.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var items = new List<ChannelItemResponse>();
        foreach (var video in videos.EnumerateArray())
        {
            var id = Text(video, "id");
            var title = Text(video, "name");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var duration = Text(video, "duration");
            var time = Text(video, "time");
            items.Add(new ChannelItemResponse(
                id,
                title,
                null,
                Text(video, "img"),
                string.Join(" · ", new[] { duration, time }.Where(value => !string.IsNullOrWhiteSpace(value))),
                string.IsNullOrWhiteSpace(kind) ? "视频" : kind,
                Text(video, "nickname"),
                1,
                CompactNumber(Text(video, "click"))));
        }
        return items;
    }

    private static string? PlayUrl(Uri baseUri, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        return Uri.TryCreate(value, UriKind.Absolute, out var absolute)
            ? absolute.ToString()
            : new Uri(baseUri, value.TrimStart('/')).ToString();
    }

    private static string? Tags(JsonElement data)
    {
        if (!data.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        var values = tags.EnumerateArray()
            .Select(tag => tag.ValueKind == JsonValueKind.String ? tag.GetString() : Text(tag, "name"))
            .Where(value => !string.IsNullOrWhiteSpace(value));
        var result = string.Join("、", values);
        return result.Length == 0 ? null : result;
    }

    private static decimal? CompactNumber(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().ToLowerInvariant().Replace(",", string.Empty, StringComparison.Ordinal);
        var multiplier = normalized.EndsWith('w') || normalized.EndsWith('万')
            ? 10_000m
            : normalized.EndsWith('k') ? 1_000m : 1m;
        normalized = normalized.TrimEnd('w', '万', 'k');
        return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
            ? number * multiplier
            : null;
    }

    private static decimal? Number(JsonElement value, string propertyName) =>
        decimal.TryParse(Text(value, propertyName), NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;

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
}
