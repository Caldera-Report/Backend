using CalderaReport.Domain.Data;
using CalderaReport.Domain.DB;
using CalderaReport.Services.Abstract;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using System.Text.Json;

namespace CalderaReport.Services;

public class ActivityService : IActivityService
{
    private readonly IDatabase _cache;
    private readonly IDbContextFactory<AppDbContext> _contextFactory;

    public ActivityService(
        IConnectionMultiplexer redis,
        IDbContextFactory<AppDbContext> contextFactory)
    {
        _cache = redis.GetDatabase();
        _contextFactory = contextFactory;
    }

    public async Task<IEnumerable<OpType>> GetAllActivities()
    {
        var activities = await _cache.StringGetAsync("activities:all");
        if (activities.HasValue)
        {
            return JsonSerializer.Deserialize<List<OpType>>(activities.ToString())
                ?? new List<OpType>();
        }
        else
        {
            return await CacheAllActivitiesAsync();
        }
    }

    private async Task<IEnumerable<OpType>> CacheAllActivitiesAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var activities = await context.OpTypes
            .Include(o => o.Activities.Where(a => a.Enabled))
            .Where(o => o.Activities.Any(a => a.Enabled))
            .ToListAsync();
        await _cache.StringSetAsync("activities:all", JsonSerializer.SerializeToUtf8Bytes(activities.ToList()), new TimeSpan(0, 1, 0, 0));
        return activities;
    }
}