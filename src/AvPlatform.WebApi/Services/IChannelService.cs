using AvPlatform.WebApi.Models;

namespace AvPlatform.WebApi.Services;

/// <summary>渠道业务服务。</summary>
public interface IChannelService
{
    IReadOnlyList<ChannelSummaryResponse> GetChannels();
    Task<ChannelHomeResponse?> GetHomeAsync(string code, bool forceRefresh, CancellationToken cancellationToken);
    Task<ChannelSearchResponse?> SearchAsync(string code, string query, bool forceRefresh, CancellationToken cancellationToken);
    Task<ChannelDetailResponse?> GetDetailAsync(string code, string itemId, bool forceRefresh, CancellationToken cancellationToken);
    Task<ChannelPlayLookupResult> GetPlayAsync(string code, string itemId, string episodeId, CancellationToken cancellationToken);
}

/// <summary>播放查询状态。</summary>
public enum ChannelPlayLookupStatus
{
    Success,
    ChannelNotFound,
    ContentNotFound,
    EpisodeNotFound,
    NotPlayable
}

/// <summary>播放查询结果，避免用 null 混淆不同失败原因。</summary>
public sealed record ChannelPlayLookupResult(
    ChannelPlayLookupStatus Status,
    ChannelPlayResponse? Response = null,
    string? SourceUrl = null,
    string? SourceMediaType = null,
    string? SourceReferrerUrl = null,
    string SourceTransport = "http");
