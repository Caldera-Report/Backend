using Microsoft.Extensions.Caching.Memory;
using StackExchange.Redis;

namespace Crawler.Helpers;

public static class CacheHelper
{
    private const string ActivityHashMapCacheKey = "activityHashMappings";

    public static async Task<Dictionary<long, long>> GetActivityHashMapAsync(this IMemoryCache cache, IConnectionMultiplexer redis)
    {
        return await cache.GetOrCreateAsync(ActivityHashMapCacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15);

            var db = redis.GetDatabase();
            var entries = await db.HashGetAllAsync(ActivityHashMapCacheKey);

            return entries.ToDictionary(
                x => long.TryParse(x.Name.ToString(), out var nameHash) ? nameHash : 0,
                x => long.TryParse(x.Value.ToString(), out var valueHash) ? valueHash : 0
            );
        }) ?? new Dictionary<long, long>();
    }
}
