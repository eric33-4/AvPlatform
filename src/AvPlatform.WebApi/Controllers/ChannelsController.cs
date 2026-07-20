using AvPlatform.WebApi.Models;
using AvPlatform.WebApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace AvPlatform.WebApi.Controllers;

/// <summary>渠道查询接口。</summary>
[ApiController]
[Route("api/channels")]
public sealed class ChannelsController(
    IChannelService channelService,
    IChannelMediaProxy mediaProxy) : ControllerBase
{
    /// <summary>返回已经注册的渠道。</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<ChannelSummaryResponse>>(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<ChannelSummaryResponse>> GetChannels() => Ok(channelService.GetChannels());

    /// <summary>返回指定渠道首页，并优先使用缓存。</summary>
    [HttpGet("{code}/home")]
    [ProducesResponseType<ChannelHomeResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ChannelHomeResponse>> GetHome(
        string code,
        [FromQuery] bool refresh,
        CancellationToken cancellationToken)
    {
        var result = await channelService.GetHomeAsync(code, refresh, cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }

    /// <summary>搜索指定渠道的内容。</summary>
    [HttpGet("{code}/search")]
    [ProducesResponseType<ChannelSearchResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ChannelSearchResponse>> Search(
        string code,
        [FromQuery] string q,
        [FromQuery] bool refresh,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            ModelState.AddModelError(nameof(q), "搜索词不能为空。");
            return ValidationProblem(ModelState);
        }

        var result = await channelService.SearchAsync(code, q, refresh, cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }

    /// <summary>返回指定内容的详情和剧集。</summary>
    [HttpGet("{code}/items/{itemId}")]
    [ProducesResponseType<ChannelDetailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ChannelDetailResponse>> GetDetail(
        string code,
        string itemId,
        [FromQuery] bool refresh,
        CancellationToken cancellationToken)
    {
        var result = await channelService.GetDetailAsync(code, itemId, refresh, cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }

    /// <summary>返回免费剧集的播放信息。</summary>
    [HttpGet("{code}/items/{itemId}/episodes/{episodeId}/play")]
    [ProducesResponseType<ChannelPlayResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ChannelPlayResponse>> GetPlay(
        string code,
        string itemId,
        string episodeId,
        CancellationToken cancellationToken)
    {
        var result = await channelService.GetPlayAsync(code, itemId, episodeId, cancellationToken);
        return result.Status switch
        {
            ChannelPlayLookupStatus.Success => Ok(result.Response),
            ChannelPlayLookupStatus.NotPlayable => StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "该剧集当前不可播放",
                Detail = "平台只暴露上游明确标记为免费的剧集。"
            }),
            _ => NotFound()
        };
    }

    /// <summary>代理免费剧集媒体流，支持浏览器 Range 请求。</summary>
    [HttpGet("{code}/items/{itemId}/episodes/{episodeId}/stream")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status206PartialContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task Stream(
        string code,
        string itemId,
        string episodeId,
        CancellationToken cancellationToken)
    {
        var result = await channelService.GetPlayAsync(code, itemId, episodeId, cancellationToken);
        if (result.Status == ChannelPlayLookupStatus.NotPlayable)
        {
            Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        if (result.Status != ChannelPlayLookupStatus.Success ||
            string.IsNullOrWhiteSpace(result.SourceUrl) ||
            string.IsNullOrWhiteSpace(result.SourceMediaType))
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (result.SourceMediaType == "application/vnd.apple.mpegurl")
        {
            await mediaProxy.ProxyPlaylistAsync(
                HttpContext,
                result.SourceUrl,
                Request.Path,
                result.SourceReferrerUrl,
                result.SourceTransport,
                cancellationToken);
            return;
        }

        await mediaProxy.ProxyBinaryAsync(
            HttpContext,
            result.SourceUrl,
            result.SourceMediaType,
            result.SourceReferrerUrl,
            result.SourceTransport,
            cancellationToken);
    }

    /// <summary>代理 HLS 嵌套播放列表、密钥和媒体分段。</summary>
    [HttpGet("{code}/items/{itemId}/episodes/{episodeId}/stream/resources/{resourceToken}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status206PartialContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task StreamResource(
        string code,
        string itemId,
        string episodeId,
        string resourceToken,
        CancellationToken cancellationToken)
    {
        var streamPath = $"/api/channels/{Uri.EscapeDataString(code)}/items/{Uri.EscapeDataString(itemId)}" +
                         $"/episodes/{Uri.EscapeDataString(episodeId)}/stream";
        var result = await channelService.GetPlayAsync(code, itemId, episodeId, cancellationToken);
        if (result.Status != ChannelPlayLookupStatus.Success ||
            string.IsNullOrWhiteSpace(result.SourceUrl) ||
            !mediaProxy.TryDecodeResource(resourceToken, streamPath, out var resourceUri))
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (resourceUri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            await mediaProxy.ProxyPlaylistAsync(
                HttpContext,
                resourceUri.ToString(),
                streamPath,
                result.SourceReferrerUrl,
                result.SourceTransport,
                cancellationToken);
            return;
        }

        await mediaProxy.ProxyBinaryAsync(
            HttpContext,
            resourceUri.ToString(),
            HlsResourceMediaType(resourceUri),
            result.SourceReferrerUrl,
            result.SourceTransport,
            cancellationToken);
    }

    /// <summary>代理 HLS 音频分段。</summary>
    [HttpGet("{code}/items/{itemId}/episodes/{episodeId}/stream/segments/{segmentName}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status206PartialContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task StreamSegment(
        string code,
        string itemId,
        string episodeId,
        string segmentName,
        CancellationToken cancellationToken)
    {
        if (Path.GetFileName(segmentName) != segmentName)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var result = await channelService.GetPlayAsync(code, itemId, episodeId, cancellationToken);
        if (result.Status != ChannelPlayLookupStatus.Success ||
            string.IsNullOrWhiteSpace(result.SourceUrl))
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var segmentUrl = new Uri(new Uri(result.SourceUrl), segmentName).ToString();
        await mediaProxy.ProxyBinaryAsync(
            HttpContext,
            segmentUrl,
            "video/mp2t",
            result.SourceReferrerUrl,
            result.SourceTransport,
            cancellationToken);
    }

    private static string HlsResourceMediaType(Uri resourceUri) =>
        Path.GetExtension(resourceUri.AbsolutePath).ToLowerInvariant() switch
        {
            ".ts" => "video/mp2t",
            ".m4s" => "video/iso.segment",
            ".mp4" => "video/mp4",
            ".aac" => "audio/aac",
            _ => "application/octet-stream"
        };

}
