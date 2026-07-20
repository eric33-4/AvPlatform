namespace AvPlatform.WebApi.Models;

/// <summary>渠道概要。</summary>
public sealed record ChannelSummaryResponse(string Code, string Name, string Mode, bool Enabled);

/// <summary>渠道首页结果。</summary>
public sealed record ChannelHomeResponse(
    string ChannelCode,
    string ChannelName,
    string Mode,
    DateTimeOffset FetchedAt,
    bool FromCache,
    string? SourceUrl,
    IReadOnlyList<ChannelItemResponse> Items,
    string? Notice);

/// <summary>统一内容卡片。</summary>
public sealed record ChannelItemResponse(
    string Id,
    string Title,
    string? Url,
    string? CoverUrl,
    string? Summary,
    string Kind,
    string? Author,
    int? EpisodeCount,
    decimal? Popularity);

/// <summary>渠道搜索结果。</summary>
public sealed record ChannelSearchResponse(
    string ChannelCode,
    string Query,
    DateTimeOffset FetchedAt,
    bool FromCache,
    IReadOnlyList<ChannelItemResponse> Items,
    string? Notice);

/// <summary>统一内容详情。</summary>
public sealed record ChannelDetailResponse(
    string ChannelCode,
    string ChannelName,
    string Id,
    string Title,
    string? CoverUrl,
    string? Summary,
    string? Category,
    string? Author,
    int EpisodeCount,
    decimal? Popularity,
    bool IsFinished,
    bool IsPaid,
    decimal? Price,
    DateTimeOffset FetchedAt,
    bool FromCache,
    IReadOnlyList<ChannelEpisodeResponse> Episodes);

/// <summary>统一剧集信息。只有免费剧集才会返回媒体地址。</summary>
public sealed record ChannelEpisodeResponse(
    string Id,
    string Title,
    string? Duration,
    bool IsFree,
    bool IsPlayable);

/// <summary>统一播放信息。</summary>
public sealed record ChannelPlayResponse(
    string ChannelCode,
    string ContentId,
    string EpisodeId,
    string Title,
    string MediaUrl,
    string MediaType,
    string MediaKind);
