using AvPlatform.WebApi.Models;

namespace AvPlatform.WebApi.Channels;

/// <summary>第一条真实内容渠道：YXFM。</summary>
public sealed class YxfmChannelAdapter(YxfmApiClient apiClient) : IChannelAdapter
{
    public string Code => "yxfm";
    public string Name => "有声（YXFM）";
    public string Mode => "加密 API";

    public async Task<ChannelHomeResponse> GetHomeAsync(CancellationToken cancellationToken)
    {
        using var response = await apiClient.PostAsync(new
        {
            v = "radio_recommend_list",
            page = 1,
            page_size = 12,
            device_type = "2",
            ids = string.Empty
        }, cancellationToken);

        var items = YxfmResponseMapper.MapHome(response.RootElement.GetProperty("data"));
        return new ChannelHomeResponse(
            Code,
            Name,
            Mode,
            DateTimeOffset.UtcNow,
            false,
            null,
            items,
            "已忽略上游广告和 Banner，只保留去重后的真实专辑。");
    }

    public async Task<ChannelSearchResponse> SearchAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var home = await GetHomeAsync(cancellationToken);
        var items = home.Items
            .Where(item => Contains(item.Title, query) ||
                           Contains(item.Summary, query) ||
                           Contains(item.Author, query) ||
                           Contains(item.Kind, query))
            .ToArray();

        return new ChannelSearchResponse(
            Code,
            query,
            DateTimeOffset.UtcNow,
            false,
            items,
            "YXFM 尚未发现独立搜索协议，当前搜索范围是首页推荐专辑。");
    }

    public async Task<ChannelDetailResponse?> GetDetailAsync(
        string itemId,
        CancellationToken cancellationToken)
    {
        using var response = await apiClient.PostAsync(new
        {
            v = "radio_album_info",
            album_id = itemId,
            device_type = "2"
        }, cancellationToken);

        return YxfmResponseMapper.MapDetail(response.RootElement.GetProperty("data"), Code, Name);
    }

    public async Task<ChannelPlaySource?> GetPlayAsync(
        string itemId,
        string episodeId,
        CancellationToken cancellationToken)
    {
        using var response = await apiClient.PostAsync(new
        {
            v = "radio_album_info",
            album_id = itemId,
            device_type = "2"
        }, cancellationToken);

        return YxfmResponseMapper.MapPlaySource(response.RootElement.GetProperty("data"), episodeId);
    }

    private static bool Contains(string? value, string query) =>
        value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
}
