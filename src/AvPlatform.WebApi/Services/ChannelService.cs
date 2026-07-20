using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AvPlatform.WebApi.Channels;
using AvPlatform.WebApi.Models;
using AvPlatform.WebApi.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace AvPlatform.WebApi.Services;

/// <summary>统一管理渠道注册、两级缓存和播放入口。</summary>
public sealed class ChannelService(
    IEnumerable<IChannelAdapter> adapters,
    AppDbContext db,
    IMemoryCache memoryCache,
    IConfiguration configuration,
    ILogger<ChannelService> logger) : IChannelService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IChannelAdapter[] _adapters = adapters.ToArray();

    public IReadOnlyList<ChannelSummaryResponse> GetChannels() =>
        _adapters.Select(x => new ChannelSummaryResponse(x.Code, x.Name, x.Mode, true)).ToArray();

    public Task<ChannelHomeResponse?> GetHomeAsync(
        string code,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var adapter = FindAdapter(code);
        return adapter is null
            ? Task.FromResult<ChannelHomeResponse?>(null)
            : GetCachedAsync(
                $"channel:{adapter.Code}:home",
                adapter.Code,
                forceRefresh,
                async token => (ChannelHomeResponse?)await adapter.GetHomeAsync(token),
                value => value with { FromCache = true },
                cancellationToken);
    }

    public Task<ChannelSearchResponse?> SearchAsync(
        string code,
        string query,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var adapter = FindAdapter(code);
        if (adapter is null)
        {
            return Task.FromResult<ChannelSearchResponse?>(null);
        }

        var normalizedQuery = query.Trim();
        var queryHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedQuery)))[..16];
        return GetCachedAsync(
            $"channel:{adapter.Code}:search:{queryHash}",
            adapter.Code,
            forceRefresh,
            async token => (ChannelSearchResponse?)await adapter.SearchAsync(normalizedQuery, token),
            value => value with { FromCache = true },
            cancellationToken);
    }

    public Task<ChannelDetailResponse?> GetDetailAsync(
        string code,
        string itemId,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var adapter = FindAdapter(code);
        return adapter is null
            ? Task.FromResult<ChannelDetailResponse?>(null)
            : GetCachedAsync(
                $"channel:{adapter.Code}:detail:{itemId}",
                adapter.Code,
                forceRefresh,
                token => adapter.GetDetailAsync(itemId, token),
                value => value with { FromCache = true },
                cancellationToken);
    }

    public async Task<ChannelPlayLookupResult> GetPlayAsync(
        string code,
        string itemId,
        string episodeId,
        CancellationToken cancellationToken)
    {
        var adapter = FindAdapter(code);
        if (adapter is null)
        {
            return new ChannelPlayLookupResult(ChannelPlayLookupStatus.ChannelNotFound);
        }

        var source = await GetCachedAsync(
            $"channel:{adapter.Code}:play:v3:{itemId}:{episodeId}",
            adapter.Code,
            false,
            token => adapter.GetPlayAsync(itemId, episodeId, token),
            value => value,
            cancellationToken);
        if (source is null)
        {
            return new ChannelPlayLookupResult(ChannelPlayLookupStatus.EpisodeNotFound);
        }

        if (!source.IsPlayable || string.IsNullOrWhiteSpace(source.SourceUrl) ||
            string.IsNullOrWhiteSpace(source.MediaType))
        {
            return new ChannelPlayLookupResult(ChannelPlayLookupStatus.NotPlayable);
        }

        var streamPath = $"/api/channels/{Uri.EscapeDataString(code)}/items/{Uri.EscapeDataString(itemId)}" +
                         $"/episodes/{Uri.EscapeDataString(episodeId)}/stream";
        return new ChannelPlayLookupResult(
            ChannelPlayLookupStatus.Success,
            new ChannelPlayResponse(
                code,
                itemId,
                source.EpisodeId,
                source.Title,
                streamPath,
                source.MediaType,
                source.MediaKind),
            source.SourceUrl,
            source.MediaType,
            source.ReferrerUrl,
            source.Transport);
    }

    private IChannelAdapter? FindAdapter(string code) =>
        _adapters.FirstOrDefault(x => x.Code.Equals(code, StringComparison.OrdinalIgnoreCase));

    private async Task<T?> GetCachedAsync<T>(
        string cacheKey,
        string channelCode,
        bool forceRefresh,
        Func<CancellationToken, Task<T?>> factory,
        Func<T, T> markAsCached,
        CancellationToken cancellationToken)
        where T : class
    {
        if (!forceRefresh && memoryCache.TryGetValue(cacheKey, out T? memoryValue) && memoryValue is not null)
        {
            return markAsCached(memoryValue);
        }

        if (!forceRefresh)
        {
            var persisted = await db.ChannelCacheEntries.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Key == cacheKey, cancellationToken);
            if (persisted is not null && persisted.ExpiresAt > DateTimeOffset.UtcNow)
            {
                try
                {
                    var cached = JsonSerializer.Deserialize<T>(persisted.PayloadJson, JsonOptions);
                    if (cached is not null)
                    {
                        memoryCache.Set(cacheKey, cached, persisted.ExpiresAt);
                        return markAsCached(cached);
                    }
                }
                catch (JsonException exception)
                {
                    logger.LogWarning(exception, "忽略损坏的渠道缓存：{CacheKey}", cacheKey);
                }
            }
        }

        var result = await factory(cancellationToken);
        if (result is null)
        {
            return null;
        }

        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(
            configuration.GetValue("Caching:ChannelMinutes", 10));
        await SaveCacheAsync(cacheKey, channelCode, result, expiresAt, cancellationToken);
        memoryCache.Set(cacheKey, result, expiresAt);
        logger.LogInformation("渠道缓存刷新：{ChannelCode}，键：{CacheKey}，有效期至：{ExpiresAt}",
            channelCode, cacheKey, expiresAt);
        return result;
    }

    private async Task SaveCacheAsync<T>(
        string cacheKey,
        string channelCode,
        T value,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(value, JsonOptions);
        var entry = await db.ChannelCacheEntries.SingleOrDefaultAsync(x => x.Key == cacheKey, cancellationToken);
        if (entry is null)
        {
            db.ChannelCacheEntries.Add(new ChannelCacheEntry
            {
                Key = cacheKey,
                ChannelCode = channelCode,
                PayloadJson = payload,
                UpdatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = expiresAt
            });
        }
        else
        {
            entry.PayloadJson = payload;
            entry.UpdatedAt = DateTimeOffset.UtcNow;
            entry.ExpiresAt = expiresAt;
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
