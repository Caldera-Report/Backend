using API.Models.Responses;
using CalderaReport.Domain.Data;
using CalderaReport.Services.Abstract;
using Facet.Extensions;
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

    public async Task<IEnumerable<OpTypeDto>> GetAllActivities()
    {
        var activities = await _cache.StringGetAsync("activities:all");
        if (activities.HasValue)
        {
            return JsonSerializer.Deserialize<List<OpTypeDto>>(activities.ToString())
                ?? new List<OpTypeDto>();
        }
        else
        {
            return await CacheAllActivities();
        }
    }

    private async Task<IEnumerable<OpTypeDto>> CacheAllActivities()
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var activities = await context.OpTypes
            .Include(o => o.Activities.Where(a => a.Enabled))
            .Where(o => o.Activities.Any(a => a.Enabled))
            .ToListAsync();
        var activityDtos = activities.Select(a => a.ToFacet<OpTypeDto>()).ToArray();
        await _cache.StringSetAsync("activities:all", JsonSerializer.Serialize(activityDtos), new TimeSpan(0, 1, 0, 0));
        return activityDtos;
    }
}