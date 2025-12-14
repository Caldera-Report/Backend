using API.Models.Responses;
using CalderaReport.Clients.Abstract;
using CalderaReport.Domain.Data;
using CalderaReport.Domain.DB;
using CalderaReport.Domain.Manifest;
using CalderaReport.Services.Abstract;
using Facet.Extensions;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using System.Text.Json;

namespace CalderaReport.Services;

public class ActivityService : IActivityService
{
    private readonly IBungieClient _client;
    private readonly IDatabase _cache;
    private readonly IDbContextFactory<AppDbContext> _contextFactory;

    public ActivityService(
        IBungieClient client,
        IConnectionMultiplexer redis,
        IDbContextFactory<AppDbContext> contextFactory)
    {
        _client = client;
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

    public async Task GroupActivityDuplicates()
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var canonicalActivities = await context.Activities.ToListAsync();
        var manifest = await _client.GetManifest();
        var allActivities = await _client.GetActivityDefinitions(manifest.Response.jsonWorldComponentContentPaths.en.DestinyActivityDefinition);

        var canonicalNames = canonicalActivities
            .Select(a => NormalizeActivityName(a.Name))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var grouped = allActivities.Values
            .Where(d => canonicalNames.Any(n => n.Contains(NormalizeActivityName(d.displayProperties.name))))
            .GroupBy(d => NormalizeActivityName(d.displayProperties.name))
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var mappings = BuildCanonicalModelsAndMappings(grouped, canonicalActivities);

        await _cache.HashSetAsync("activityHashMappings",
            mappings.Select(kv => new HashEntry(kv.Key, kv.Value)).ToArray());
    }

    private static string NormalizeActivityName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return name;
        var idx = name.IndexOf(": Customize", StringComparison.OrdinalIgnoreCase);
        if (idx == -1)
            idx = name.IndexOf(": Matchmade", StringComparison.OrdinalIgnoreCase);
        return (idx >= 0 ? name[..idx] : name).Trim();
    }

    private static Dictionary<long, long> BuildCanonicalModelsAndMappings(
        Dictionary<string, List<DestinyActivityDefinition>> grouped,
        IReadOnlyCollection<Activity> canonicalActivities)
    {
        var mappings = new Dictionary<long, long>();

        foreach (var canonical in canonicalActivities)
        {
            var normalizedName = NormalizeActivityName(canonical.Name);

            if (!string.IsNullOrWhiteSpace(normalizedName) && grouped.TryGetValue(normalizedName, out var variants) && variants.Any())
            {
                var canonicalDef = variants.FirstOrDefault(v => v.hash == canonical.Id)
                    ?? variants.OrderBy(v => v.hash).First();

                mappings[canonical.Id] = canonical.Id;

                foreach (var variant in variants)
                {
                    mappings[variant.hash] = canonical.Id;
                }
            }
            else
            {
                mappings[canonical.Id] = canonical.Id;
            }
        }

        return mappings;
    }
}