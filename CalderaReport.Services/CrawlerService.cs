using CalderaReport.Clients.Abstract;
using CalderaReport.Domain.Data;
using CalderaReport.Domain.DB;
using CalderaReport.Domain.DestinyApi;
using CalderaReport.Domain.Enums;
using CalderaReport.Domain.Manifest;
using CalderaReport.Services.Abstract;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using System.Collections.Concurrent;

namespace CalderaReport.Services;

public class CrawlerService : ICrawlerService
{
    private static readonly DateTime ActivityCutoffUtc = new DateTime(2025, 7, 15, 19, 0, 0);
    private readonly IBungieClient _bungieClient;
    private readonly IDatabase _redis;
    private readonly IDbContextFactory<AppDbContext> _contextFactory;

    private readonly SemaphoreSlim _activityHashMapSemaphore = new SemaphoreSlim(1, 1);
    private readonly ParallelOptions _parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };

    public CrawlerService(IDbContextFactory<AppDbContext> contextFactory, IBungieClient bungieClient, IConnectionMultiplexer redis)
    {
        _contextFactory = contextFactory;
        _bungieClient = bungieClient;
        _redis = redis.GetDatabase();
    }

    public async Task<Dictionary<string, DestinyCharacterComponent>> GetCharactersForCrawl(Player player)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var characters = await GetCharactersForPlayer(player);

        if (player.LastCrawlStarted == null || player.NeedsFullCheck)
        {
            return characters;
        }
        else
        {
            return characters.Where(c => c.Value.dateLastPlayed > (player.LastCrawlStarted ?? ActivityCutoffUtc)).ToDictionary();
        }
    }

    private async Task<Dictionary<string, DestinyCharacterComponent>> GetCharactersForPlayer(Player player)
    {
        var characters = await _bungieClient.GetCharactersForPlayer(player.Id, player.MembershipType);

        await CheckPlayerNameAndEmblem(characters.Response, player.Id);
        return characters.Response.characters.data;
    }

    private async Task CheckPlayerNameAndEmblem(DestinyProfileResponse profile, long playerId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var player = await context.Players.FirstOrDefaultAsync(p => p.Id == playerId);
        if (player == null) {
            throw new InvalidOperationException($"Player with ID {playerId} not found.");
        }

        if (player.DisplayName != profile.profile.data.userInfo.bungieGlobalDisplayName ||
            player.DisplayNameCode != profile.profile.data.userInfo.bungieGlobalDisplayNameCode)
        {
            player.DisplayName = profile.profile.data.userInfo.bungieGlobalDisplayName;
            player.DisplayNameCode = profile.profile.data.userInfo.bungieGlobalDisplayNameCode;
            player.FullDisplayName = player.DisplayName + "#" + player.DisplayNameCode;
            context.Players.Update(player);
            await context.SaveChangesAsync();
        }

        var lastPlayedCharacter = profile.characters.data.Values
            .OrderByDescending(cid => profile.characters.data[cid.characterId].dateLastPlayed)
            .FirstOrDefault();

        if (lastPlayedCharacter != null)
        {
            if (player.LastPlayedCharacterEmblemPath != lastPlayedCharacter.emblemPath || player.LastPlayedCharacterBackgroundPath != lastPlayedCharacter.emblemBackgroundPath)
            {
                player.LastPlayedCharacterEmblemPath = lastPlayedCharacter.emblemPath;
                player.LastPlayedCharacterBackgroundPath = lastPlayedCharacter.emblemBackgroundPath;
                context.Players.Update(player);
                await context.SaveChangesAsync();
            }
        }
    }

    public async Task<DateTime> GetLastPlayedActivityDateForPlayer(Player player)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var lastPlayedActivityDate = await context.ActivityReports
            .AsNoTracking()
            .Where(r => r.Players.Any(p => p.PlayerId == player.Id) && !r.NeedsFullCheck)
            .OrderByDescending(r => r.Date)
            .Select(r => (DateTime?)r.Date)
            .FirstOrDefaultAsync();
        return lastPlayedActivityDate ?? ActivityCutoffUtc;
    }

    public async Task<IEnumerable<ActivityReport>> CrawlCharacter(Player player, string characterId, DateTime lastPlayedActivityDate)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var page = 0;
        var hasReachedLastUpdate = false;
        var activityCount = 250;
        var existingReportIds = player.NeedsFullCheck ? new HashSet<long>() : await context.ActivityReportPlayers
            .Where(arp => arp.PlayerId == player.Id)
            .Select(arp => arp.ActivityReportId)
            .ToHashSetAsync();

        var reportsBag = new ConcurrentBag<ActivityReport>();

        while (!hasReachedLastUpdate)
        {
            var response = await _bungieClient.GetHistoricalStatsForCharacter(player.Id, player.MembershipType, characterId, page, activityCount);
            if (response.Response?.activities == null || !response.Response.activities.Any())
                break;
            page++;
            var activityHashMap = await GetActivityHashMap();
            Parallel.ForEach(response.Response.activities, _parallelOptions, activityReport =>
            {
                hasReachedLastUpdate = activityReport.period <= lastPlayedActivityDate;
                if (activityReport.period < ActivityCutoffUtc || hasReachedLastUpdate)
                    return;
                var rawHash = activityReport.activityDetails.referenceId;
                if (!activityHashMap.TryGetValue(rawHash, out var canonicalId))
                    return;

                if (!long.TryParse(activityReport.activityDetails.instanceId, out var instanceId))
                    return;

                if (existingReportIds.Contains(instanceId))
                    return;

                reportsBag.Add(new ActivityReport
                {
                    Id = instanceId,
                    ActivityId = canonicalId,
                    Date = activityReport.period,
                    NeedsFullCheck = activityReport.values["playerCount"].basic.value != 1,
                    Players = new List<ActivityReportPlayer>
                        {
                            new ActivityReportPlayer
                            {
                                PlayerId = player.Id,
                                ActivityReportId = instanceId,
                                SessionId = reportsBag.Count(ar => ar.Id == instanceId && ar.Players.Any(arp => arp.PlayerId == player.Id)) + 1,
                                Score = (int)activityReport.values["score"].basic.value,
                                ActivityId = canonicalId,
                                Duration = TimeSpan.FromSeconds(activityReport.values["activityDurationSeconds"].basic.value),
                                Completed = activityReport.values["completed"].basic.value == 1 && activityReport.values["completionReason"].basic.value != 2.0,
                            }
                        }
                });
            });
            if (response.Response.activities.Last().period < ActivityCutoffUtc)
                break;
        }
        return reportsBag.ToList();
    }

    private async Task<IReadOnlyDictionary<long, long>> GetActivityHashMap()
    {
        var cacheKey = "activityHashMappings";
        var entries = await _redis.HashGetAllAsync(cacheKey);
        if (entries.Count() == 0)
        {
            await _activityHashMapSemaphore.WaitAsync();
            try
            {
                var groupedActivities = await PullActivitiesFromBungie();
                if (entries.Count() == 0)
                {
                    throw new InvalidOperationException("Activity hash mappings are not populated in Redis.");
                }

                await _redis.HashSetAsync(cacheKey, groupedActivities.Select(kv => new HashEntry(kv.Key, kv.Value)).ToArray());
                await _redis.KeyExpireAsync(cacheKey, TimeSpan.FromMinutes(15));

                return groupedActivities;
            }
            finally
            {
                _activityHashMapSemaphore.Release();
            }
        }
        return entries.ToDictionary(
            x => long.TryParse(x.Name.ToString(), out var nameHash) ? nameHash : 0,
            x => long.TryParse(x.Value.ToString(), out var valueHash) ? valueHash : 0
        );
    }

    private async Task<IReadOnlyDictionary<long, long>> PullActivitiesFromBungie()
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var canonicalActivities = await context.Activities.ToListAsync();
        var manifest = await _bungieClient.GetManifest();
        var allActivities = await _bungieClient.GetActivityDefinitions(manifest.Response.jsonWorldComponentContentPaths.en.DestinyActivityDefinition);

        var canonicalNames = canonicalActivities
            .Select(a => NormalizeActivityName(a.Name))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var grouped = allActivities.Values
            .Where(d => canonicalNames.Any(n => n.Contains(NormalizeActivityName(d.displayProperties.name))))
            .GroupBy(d => NormalizeActivityName(d.displayProperties.name))
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var mappings = BuildCanonicalModelsAndMappings(grouped, canonicalActivities);

        return mappings;
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

    public async Task LoadCrawler()
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var isCrawling = await context.PlayerCrawlQueue.AnyAsync(pcq => pcq.Status == PlayerQueueStatus.Queued || pcq.Status == PlayerQueueStatus.Processing);
        if (!isCrawling)
        {
            await context.Database.ExecuteSqlRawAsync(
                """
                    TRUNCATE TABLE "PlayerCrawlQueue";
                    INSERT INTO "PlayerCrawlQueue"
                        ("Id","PlayerId","EnqueuedAt","ProcessedAt","Status","Attempts")
                    SELECT
                        gen_random_uuid(),  
                        p."Id",
                        NOW(),
                        NULL,
                        {0},                          
                        0
                    FROM "Players" p;
                    """,
                (int)PlayerQueueStatus.Queued);
        }
    }
}
