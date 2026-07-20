using Microsoft.EntityFrameworkCore;

namespace AvPlatform.WebApi.Persistence;

/// <summary>平台 SQLite 数据库上下文。</summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    /// <summary>渠道缓存快照。</summary>
    public DbSet<ChannelCacheEntry> ChannelCacheEntries => Set<ChannelCacheEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ChannelCacheEntry>(entity =>
        {
            entity.HasKey(x => x.Key);
            entity.Property(x => x.Key).HasMaxLength(200);
            entity.Property(x => x.ChannelCode).HasMaxLength(50).IsRequired();
            entity.Property(x => x.PayloadJson).IsRequired();
            entity.HasIndex(x => new { x.ChannelCode, x.ExpiresAt });
        });
    }
}

/// <summary>保存渠道结果的持久化缓存。</summary>
public sealed class ChannelCacheEntry
{
    public required string Key { get; set; }
    public required string ChannelCode { get; set; }
    public required string PayloadJson { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}
