using System.Text.Json;
using AvPlatform.WebApi.Models;
using Microsoft.AspNetCore.WebUtilities;

namespace AvPlatform.WebApi.Channels;

/// <summary>INSAV GDAPI 渠道；当前只暴露无需登录的免费内容。</summary>
public sealed class InsAvChannelAdapter(
    GdApiClient apiClient,
    YueShuGeConfigProvider configProvider,
    IConfiguration configuration,
    ILogger<InsAvChannelAdapter> logger) : IChannelAdapter
{
    private const string EpisodeId = "main";
    private const int FreeSite = 3;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly GdApiChannelProfile Profile = new(
        "INSAV",
        "insav",
        "InsAv",
        AddDmSubdomain);

    public string Code => "insav";
    public string Name => "INSAV";
    public string Mode => "加密 API";

    public async Task<ChannelHomeResponse> GetHomeAsync(CancellationToken cancellationToken)
    {
        using var response = await LoadItemsAsync(null, cancellationToken);
        return new ChannelHomeResponse(
            Code,
            Name,
            Mode,
            DateTimeOffset.UtcNow,
            false,
            null,
            await MapItemsAsync(response.RootElement, cancellationToken),
            "当前展示 INSAV 动画站中无需登录的免费内容；付费内容不会伪装成可播放。");
    }

    public async Task<ChannelSearchResponse> SearchAsync(
        string query,
        CancellationToken cancellationToken)
    {
        using var response = await LoadItemsAsync(query, cancellationToken);
        return new ChannelSearchResponse(
            Code,
            query,
            DateTimeOffset.UtcNow,
            false,
            await MapItemsAsync(response.RootElement, cancellationToken),
            "INSAV 免费内容实时搜索结果。");
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

        var free = item.Private == 0;
        var detail = new ChannelDetailResponse(
            Code,
            Name,
            itemId,
            item.Title,
            item.CoverUrl,
            BuildSummary(item),
            "动画",
            item.Actor ?? item.Publisher,
            1,
            item.Popularity,
            true,
            !free,
            null,
            DateTimeOffset.UtcNow,
            false,
            [new ChannelEpisodeResponse(EpisodeId, item.Title, null, free, free)]);
        return Task.FromResult<ChannelDetailResponse?>(detail);
    }

    public async Task<ChannelPlaySource?> GetPlayAsync(
        string itemId,
        string episodeId,
        CancellationToken cancellationToken)
    {
        if (episodeId != EpisodeId || DecodeItem(itemId) is not { Private: 0 } item)
        {
            return null;
        }

        using var response = await apiClient.PostAsync(
            Profile,
            "api/video/getVideoUrl",
            new Dictionary<string, object?>
            {
                ["site"] = item.Site,
                ["device"] = 2,
                ["vid"] = item.Id
            },
            cancellationToken);
        var source = response.RootElement.TryGetProperty("data", out var data) &&
                     data.ValueKind == JsonValueKind.String
            ? data.GetString()
            : null;
        return new ChannelPlaySource(
            EpisodeId,
            item.Title,
            !string.IsNullOrWhiteSpace(source),
            source,
            string.IsNullOrWhiteSpace(source) ? null : "application/vnd.apple.mpegurl",
            "video");
    }

    private Task<JsonDocument> LoadItemsAsync(string? query, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["page"] = 1,
            ["device"] = 2,
            ["limit"] = 30,
            ["site"] = FreeSite
        };
        if (!string.IsNullOrWhiteSpace(query))
        {
            parameters["keyword"] = query;
        }
        return apiClient.PostAsync(Profile, "api/video/lists", parameters, cancellationToken);
    }

    private async Task<IReadOnlyList<ChannelItemResponse>> MapItemsAsync(
        JsonElement root,
        CancellationToken cancellationToken)
    {
        if (!TryGetVideos(root, out var videos))
        {
            return [];
        }

        var imageOrigin = await ImageOriginAsync(cancellationToken);
        var items = new List<ChannelItemResponse>();
        var total = 0;
        var free = 0;
        foreach (var video in videos.EnumerateArray())
        {
            total++;
            if (Integer(video, "private") == 0)
            {
                free++;
            }
            if (Integer(video, "private") != 0 || Integer(video, "id") is not { } id ||
                Text(video, "title") is not { } title)
            {
                continue;
            }

            var actor = NestedText(video, "actor", "name");
            var publisher = NestedText(video, "publisher", "name");
            var coverUrl = AbsoluteUrl(imageOrigin, Text(video, "thumb") ?? Text(video, "preview"));
            var token = new InsAvItemToken(
                FreeSite,
                id,
                title,
                coverUrl,
                actor,
                publisher,
                Number(video, "play"),
                0);
            items.Add(new ChannelItemResponse(
                EncodeItem(token),
                title,
                null,
                coverUrl,
                BuildSummary(token),
                "动画",
                actor ?? publisher,
                1,
                token.Popularity));
        }
        logger.LogInformation("INSAV 列表映射完成：总数 {Total}，免费 {Free}，输出 {Output}。", total, free, items.Count);
        return items;
    }

    private async Task<Uri> ImageOriginAsync(CancellationToken cancellationToken)
    {
        var value = await configProvider.GetHostAsync("img_insav", cancellationToken) ??
                    configuration.GetValue("Channels:InsAv:ImageOrigin", "https://yik.yanyunhome.com/");
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? uri
            : throw new InvalidOperationException("INSAV 图片节点无效。");
    }

    private static string? AbsoluteUrl(Uri origin, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        return Uri.TryCreate(value, UriKind.Absolute, out var absolute)
            ? absolute.ToString()
            : new Uri(origin, value.TrimStart('/')).ToString();
    }

    private static string EncodeItem(InsAvItemToken item) =>
        WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(item, JsonOptions));

    private static InsAvItemToken? DecodeItem(string value)
    {
        try
        {
            return JsonSerializer.Deserialize<InsAvItemToken>(WebEncoders.Base64UrlDecode(value), JsonOptions);
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return null;
        }
    }

    private static string BuildSummary(InsAvItemToken item) =>
        string.Join(" · ", new[] { item.Actor, item.Publisher }.Where(value => !string.IsNullOrWhiteSpace(value)));

    private static string? Text(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static string? NestedText(JsonElement element, string parent, string name) =>
        element.TryGetProperty(parent, out var nested) && nested.ValueKind == JsonValueKind.Object
            ? Text(nested, name)
            : null;

    private static int? Integer(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;

    private static decimal? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetDecimal(out var number) ? number : null;

    private static bool TryGetVideos(JsonElement root, out JsonElement videos)
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
        if (data.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        foreach (var name in new[] { "data", "list", "items" })
        {
            if (data.TryGetProperty(name, out var candidate) && candidate.ValueKind == JsonValueKind.Array)
            {
                videos = candidate;
                return true;
            }
        }
        return false;
    }

    private static string AddDmSubdomain(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            return endpoint;
        }
        if (uri.Host.StartsWith("dm.", StringComparison.OrdinalIgnoreCase))
        {
            return endpoint;
        }
        return new UriBuilder(uri) { Host = "dm." + uri.Host }.Uri.ToString();
    }

    private sealed record InsAvItemToken(
        int Site,
        int Id,
        string Title,
        string? CoverUrl,
        string? Actor,
        string? Publisher,
        decimal? Popularity,
        int Private);
}
