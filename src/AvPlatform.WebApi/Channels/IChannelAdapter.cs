using AvPlatform.WebApi.Models;

namespace AvPlatform.WebApi.Channels;

/// <summary>所有渠道必须实现的统一内容协议。</summary>
public interface IChannelAdapter
{
    string Code { get; }
    string Name { get; }
    string Mode { get; }
    Task<ChannelHomeResponse> GetHomeAsync(CancellationToken cancellationToken);
    Task<ChannelSearchResponse> SearchAsync(string query, CancellationToken cancellationToken);
    Task<ChannelDetailResponse?> GetDetailAsync(string itemId, CancellationToken cancellationToken);
    Task<ChannelPlaySource?> GetPlayAsync(string itemId, string episodeId, CancellationToken cancellationToken);
}

/// <summary>渠道内部播放源，不会直接序列化给浏览器。</summary>
public sealed record ChannelPlaySource(
    string EpisodeId,
    string Title,
    bool IsPlayable,
    string? SourceUrl,
    string? MediaType,
    string MediaKind,
    string? ReferrerUrl = null,
    string Transport = "http");
