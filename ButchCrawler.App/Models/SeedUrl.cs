using System;

namespace ButchCrawler.App
{
    // Added Id property (database primary key)
    public record SeedUrl(
        int Id,
        string Url,
        string? Domain,
        string? Category,
        int Priority,
        int MaxDepth,
        bool FollowExternal,
        bool IsActive,
        string? Notes,
        DateTime CreatedAt,
        DateTime? LastCrawledAt,
        string? SourceType,
        string? DiscoverySource,
        string? Tags
        );
}