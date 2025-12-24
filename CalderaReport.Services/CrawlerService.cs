using CalderaReport.Clients.Abstract;
using CalderaReport.Domain.Data;
using CalderaReport.Domain.DB;
using CalderaReport.Domain.DestinyApi;
using CalderaReport.Domain.Enums;
using CalderaReport.Domain.Manifest;
using CalderaReport.Services.Abstract;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;
using StackExchange.Redis;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace CalderaReport.Services;

public class CrawlerService : ICrawlerService
{
    private static readonly DateTime ActivityCutoffUtc = new DateTime(2025, 7, 15, 19, 0, 0);
    private readonly IBungieClient _bungieClient;
    private readonly StackExchange.Redis.IDatabase _redis;
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly ILogger<CrawlerService> _logger;

    private const string ConquestCacheKey = "conquests:mappings";
    private static readonly JsonSerializerOptions CacheSerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly IReadOnlyDictionary<string, string> ActivityNameAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        // ["Alias Name"] = "Canonical Name"
    };

    private readonly SemaphoreSlim _activityHashMapSemaphore = new SemaphoreSlim(1, 1);
    private readonly SemaphoreSlim _conquestCacheSemaphore = new SemaphoreSlim(1, 1);
    private readonly ParallelOptions _parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount * 2 };

    public CrawlerService(IDbContextFactory<AppDbContext> contextFactory, IBungieClient bungieClient, IConnectionMultiplexer redis, ILogger<CrawlerService> logger)
    {
        _contextFactory = contextFactory;
        _bungieClient = bungieClient;
        _redis = redis.GetDatabase();
        _logger = logger;
    }

    public async Task<bool> CrawlPlayer(long playerId)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            var player = await context.Players.FirstOrDefaultAsync(p => p.Id == playerId);
            if (player is null)
            {
                _logger.LogWarning("Player {PlayerId} not found in database; skipping work item.", playerId);
                return false;
            }

            player.LastCrawlStarted = DateTime.UtcNow;
            await context.SaveChangesAsync();

            var lastPlayedActivityDate = await GetLastPlayedActivityDateForPlayer(player);
            var charactersToProcess = await GetCharactersForCrawl(player, lastPlayedActivityDate);


            var allReports = new ConcurrentBag<ActivityReport>();
            var sessionCounters = new ConcurrentDictionary<(long ReportId, long PlayerId), int>();

            await Parallel.ForEachAsync(charactersToProcess, _parallelOptions, async (character, ct) =>
            {
                var reports = await CrawlCharacter(
                    player,
                    character.Key,
                    player.NeedsFullCheck ? ActivityCutoffUtc : lastPlayedActivityDate,
                    sessionCounters);
                if (reports is null)
                {
                    return;
                }
                foreach (var report in reports)
                {
                    allReports.Add(report);
                }
            });

            if (allReports.IsEmpty)
            {
                player.LastCrawlCompleted = DateTime.UtcNow;
                player.NeedsFullCheck = false;
                var playerQueueItem = await context.PlayerCrawlQueue.FirstOrDefaultAsync(pcq => pcq.PlayerId == playerId);
                if (playerQueueItem != null)
                {
                    playerQueueItem.Status = PlayerQueueStatus.Completed;
                    playerQueueItem.ProcessedAt = DateTime.UtcNow;
                }
                await context.SaveChangesAsync();
                return false;
            }
            else
            {
                await CreateActivityReports(allReports.ToList(), playerId);

                var finalPlayerQueueItem = await context.PlayerCrawlQueue.FirstOrDefaultAsync(pcq => pcq.PlayerId == playerId);
                var finalPlayer = await context.Players.FirstOrDefaultAsync(p => p.Id == playerId);

                if (finalPlayerQueueItem != null)
                {
                    finalPlayerQueueItem.Status = PlayerQueueStatus.Completed;
                    finalPlayerQueueItem.ProcessedAt = DateTime.UtcNow;
                }

                if (finalPlayer != null)
                {
                    finalPlayer.LastCrawlCompleted = DateTime.UtcNow;
                    finalPlayer.NeedsFullCheck = false;
                }

                await context.SaveChangesAsync();

                _logger.LogInformation("Created {ReportCount} activity reports for player {PlayerId}.", allReports.Count, playerId);

                return true;
            }
        }
        catch (DestinyApiException ex) when (Enum.TryParse(ex.ErrorCode.ToString(), out BungieErrorCodes result) && result == BungieErrorCodes.AccountNotFound)
        {
            _logger.LogError("Player {PlayerId} does not exist, deleting", playerId);
            try
            {
                await using var context = await _contextFactory.CreateDbContextAsync();
                var player = await context.Players.FirstOrDefaultAsync(p => p.Id == playerId);
                if (player != null)
                {
                    context.Players.Remove(player);
                }
                await context.SaveChangesAsync();
            }
            catch (Exception innerEx)
            {
                _logger.LogError(innerEx, "Error removing player {PlayerId}", playerId);
            }
            throw;
        }
        catch (DestinyApiException ex) when (Enum.TryParse(ex.ErrorCode.ToString(), out BungieErrorCodes result) && result == BungieErrorCodes.PrivateAccount)
        {
            _logger.LogWarning("Player {PlayerId} has a private account", playerId);
            try
            {
                await using var context = await _contextFactory.CreateDbContextAsync();
                var player = await context.Players.FirstOrDefaultAsync(p => p.Id == playerId);
                if (player != null)
                {
                    player.NeedsFullCheck = true;
                }
                var playerQueueItem = await context.PlayerCrawlQueue.FirstOrDefaultAsync(pcq => pcq.PlayerId == playerId);
                if (playerQueueItem != null)
                {
                    playerQueueItem.Status = PlayerQueueStatus.Completed;
                    playerQueueItem.ProcessedAt = DateTime.UtcNow;
                }
                await context.SaveChangesAsync();
            }
            catch (Exception innerEx)
            {
                _logger.LogError(innerEx, "Error updating player queue status to Completed for player {PlayerId}.", playerId);
            }
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing player {PlayerId}.", playerId);
            try
            {
                await using var context = await _contextFactory.CreateDbContextAsync();
                var player = await context.Players.FirstOrDefaultAsync(p => p.Id == playerId);
                player?.NeedsFullCheck = true;
                await context.SaveChangesAsync();
            }
            catch (Exception innerEx)
            {
                _logger.LogError(innerEx, "Error updating player queue status to Error for player {PlayerId}.", playerId);
            }
            throw;
        }
    }

    private async Task<Dictionary<string, DestinyCharacterComponent>> GetCharactersForCrawl(Player player, DateTime lastPlayedActivityDate)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var characters = await GetCharactersForPlayer(player);

        if (player.LastCrawlStarted == null || player.NeedsFullCheck)
        {
            return characters;
        }
        else
        {
            return characters.Where(c => c.Value.dateLastPlayed > lastPlayedActivityDate).ToDictionary();
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
        if (player == null)
        {
            throw new InvalidOperationException($"Player with ID {playerId} not found.");
        }

        if (player.DisplayName != profile.profile.data.userInfo.bungieGlobalDisplayName ||
            player.DisplayNameCode != profile.profile.data.userInfo.bungieGlobalDisplayNameCode)
        {
            player.DisplayName = profile.profile.data.userInfo.bungieGlobalDisplayName;
            player.DisplayNameCode = profile.profile.data.userInfo.bungieGlobalDisplayNameCode;
            player.FullDisplayName = $"{profile.profile.data.userInfo.bungieGlobalDisplayName}#{profile.profile.data.userInfo.bungieGlobalDisplayNameCode:0000}";
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

    private async Task<DateTime> GetLastPlayedActivityDateForPlayer(Player player)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var lastPlayedActivityDate = await context.ActivityReports
            .AsNoTracking()
            .Where(r => r.Players.Any(p => p.PlayerId == player.Id) && !r.NeedsFullCheck)
            .OrderByDescending(r => r.Date)
            .Select(r => (DateTime?)r.Date)
            .FirstOrDefaultAsync();
        return player.NeedsFullCheck ? ActivityCutoffUtc : lastPlayedActivityDate ?? ActivityCutoffUtc;
    }

    private async Task<IEnumerable<ActivityReport>> CrawlCharacter(
        Player player,
        string characterId,
        DateTime lastPlayedActivityDate,
        ConcurrentDictionary<(long ReportId, long PlayerId), int> sessionCounters)
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
            foreach (var activityReport in response.Response.activities)
            {
                hasReachedLastUpdate = activityReport.period <= lastPlayedActivityDate;
                if (activityReport.period < ActivityCutoffUtc || hasReachedLastUpdate)
                    break;
                var rawHash = activityReport.activityDetails.referenceId;
                if (!activityHashMap.TryGetValue(rawHash, out var canonicalId))
                    continue;

                if (!long.TryParse(activityReport.activityDetails.instanceId, out var instanceId))
                    continue;

                if (existingReportIds.Contains(instanceId))
                    continue;

                reportsBag.Add(new ActivityReport
                {
                    Id = instanceId,
                    ActivityId = canonicalId,
                    BungieActivityId = rawHash,
                    Date = activityReport.period,
                    NeedsFullCheck = activityReport.values["playerCount"].basic.value != 1,
                    Players = new List<ActivityReportPlayer>
                        {
                            new ActivityReportPlayer
                            {
                                PlayerId = player.Id,
                                ActivityReportId = instanceId,
                                SessionId = GetNextSessionId(sessionCounters, instanceId, player.Id),
                                Score = (int)activityReport.values["score"].basic.value,
                                ActivityId = canonicalId,
                                Duration = TimeSpan.FromSeconds(activityReport.values["activityDurationSeconds"].basic.value),
                                Completed = activityReport.values["completed"].basic.value == 1 && activityReport.values["completionReason"].basic.value != 2.0,
                            }
                        }
                });
            }

            if (response.Response.activities.Last().period < ActivityCutoffUtc)
                break;
        }
        return reportsBag.ToList();
    }

    private async Task<IReadOnlyDictionary<long, long>> GetActivityHashMap()
    {
        var cacheKey = "activityHashMappings";
        var entries = await _redis.HashGetAllAsync(cacheKey);
        if (entries.Length == 0)
        {
            await _activityHashMapSemaphore.WaitAsync();
            try
            {
                var groupedActivities = await PullActivitiesFromBungie();
                if (groupedActivities.Count == 0)
                {
                    throw new InvalidOperationException("No Activities pulled from Bungie");
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
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(n => n, n => n, StringComparer.OrdinalIgnoreCase);

        var grouped = new Dictionary<string, List<DestinyActivityDefinition>>(StringComparer.OrdinalIgnoreCase);

        foreach (var definition in allActivities.Values)
        {
            var displayName = definition.displayProperties?.name ?? string.Empty;
            var normalizedName = NormalizeActivityName(displayName);
            if (string.IsNullOrWhiteSpace(normalizedName))
            {
                continue;
            }

            var canonicalName = TryResolveCanonicalActivityName(normalizedName, canonicalNames);
            if (canonicalName is null)
            {
                continue;
            }

            if (!grouped.TryGetValue(canonicalName, out var variants))
            {
                variants = new List<DestinyActivityDefinition>();
                grouped[canonicalName] = variants;
            }

            variants.Add(definition);
        }

        var mappings = BuildCanonicalModelsAndMappings(grouped, canonicalActivities);

        return mappings;
    }

    private async Task<IReadOnlyDictionary<long, List<CachedConquest>>> GetConquestLookupAsync()
    {
        var cached = await _redis.StringGetAsync(ConquestCacheKey);
        if (cached.HasValue && !cached.IsNullOrEmpty)
        {
            var deserialized = JsonSerializer.Deserialize<Dictionary<long, List<CachedConquest>>>(cached!.ToString(), CacheSerializerOptions);
            if (deserialized != null)
            {
                return deserialized;
            }
        }

        await _conquestCacheSemaphore.WaitAsync();
        try
        {
            cached = await _redis.StringGetAsync(ConquestCacheKey);
            if (cached.HasValue && !cached.IsNullOrEmpty)
            {
                var deserialized = JsonSerializer.Deserialize<Dictionary<long, List<CachedConquest>>>(cached!.ToString(), CacheSerializerOptions);
                if (deserialized != null)
                {
                    return deserialized;
                }
            }

            await using var context = await _contextFactory.CreateDbContextAsync();
            var conquestData = await context.ConquestMappings
                .Include(cm => cm.Expansion)
                .Select(cm => new CachedConquest
                {
                    ActivityId = cm.ActivityId,
                    BungieActivityId = cm.BungieActivityId,
                    ReleaseDate = cm.Expansion.ReleaseDate,
                    EndDate = cm.Expansion.EndDate == default ? null : cm.Expansion.EndDate
                })
                .ToListAsync();

            var grouped = conquestData
                .GroupBy(c => c.BungieActivityId)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderBy(c => c.ReleaseDate).ToList());

            await _redis.StringSetAsync(
                ConquestCacheKey,
                JsonSerializer.Serialize(grouped, CacheSerializerOptions),
                TimeSpan.FromMinutes(15));

            return grouped;
        }
        finally
        {
            _conquestCacheSemaphore.Release();
        }
    }

    private static long? ResolveConquestActivityId(
        long bungieActivityId,
        DateTime activityDateUtc,
        IReadOnlyDictionary<long, List<CachedConquest>> conquestLookup)
    {
        if (bungieActivityId == 0 || !conquestLookup.TryGetValue(bungieActivityId, out var conquestOptions) || conquestOptions.Count == 0)
        {
            return null;
        }

        var applicable = conquestOptions.FirstOrDefault(c =>
            activityDateUtc >= c.ReleaseDate &&
            activityDateUtc <= (c.EndDate ?? DateTime.MaxValue));

        return (applicable ?? conquestOptions.First()).ActivityId;
    }

    private sealed class CachedConquest
    {
        public long ActivityId { get; init; }
        public long BungieActivityId { get; init; }
        public DateTime ReleaseDate { get; init; }
        public DateTime? EndDate { get; init; }
    }

    private static string NormalizeActivityName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return name;
        name = NormalizeWhitespace(name);
        var idx = name.IndexOf(": Customize", StringComparison.OrdinalIgnoreCase);
        if (idx == -1)
            idx = name.IndexOf(": Matchmade", StringComparison.OrdinalIgnoreCase);
        return (idx >= 0 ? name[..idx] : name).Trim();
    }

    private static string NormalizeWhitespace(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        var inWhitespace = false;

        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!inWhitespace)
                {
                    builder.Append(' ');
                    inWhitespace = true;
                }
                continue;
            }

            inWhitespace = false;
            builder.Append(ch);
        }

        return builder.ToString().Trim();
    }

    private static IEnumerable<string> GetActivityNameMatchCandidates(string normalizedName)
    {
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            yield break;
        }

        yield return normalizedName;

        var lastColon = normalizedName.LastIndexOf(": ", StringComparison.OrdinalIgnoreCase);
        if (lastColon < 0 || lastColon + 2 >= normalizedName.Length)
        {
            yield break;
        }

        var suffix = normalizedName[(lastColon + 2)..].Trim();
        if (!string.IsNullOrWhiteSpace(suffix) && !suffix.Equals(normalizedName, StringComparison.OrdinalIgnoreCase))
        {
            yield return suffix;
        }
    }

    private static string? TryResolveCanonicalActivityName(
        string normalizedActivityName,
        IReadOnlyDictionary<string, string> canonicalNames)
    {
        foreach (var candidate in GetActivityNameMatchCandidates(normalizedActivityName))
        {
            if (canonicalNames.ContainsKey(candidate))
            {
                return candidate;
            }

            if (ActivityNameAliases.TryGetValue(candidate, out var aliasTarget) && canonicalNames.ContainsKey(aliasTarget))
            {
                return aliasTarget;
            }
        }

        return null;
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

    private async Task CreateActivityReports(List<ActivityReport> activityReports, long playerId)
    {
        if (activityReports.Count == 0)
        {
            return;
        }

        await using var context = await _contextFactory.CreateDbContextAsync();

        var conquestLookup = await GetConquestLookupAsync();

        List<ActivityReport> reportsToInsert = new();
        List<ActivityReportPlayer> playerReportsToInsert = new();
        var reportsNeedingFullCheck = new HashSet<long>();

        var mergedReports = activityReports
            .GroupBy(r => r.Id)
            .Select(g =>
            {
                var baseReport = g.OrderByDescending(r => r.Date).First();
                baseReport.NeedsFullCheck = g.Any(r => r.NeedsFullCheck);
                baseReport.ActivityId = g.Select(r => r.ActivityId).FirstOrDefault(id => id != 0);
                baseReport.BungieActivityId = g.Select(r => r.BungieActivityId).FirstOrDefault(id => id != 0);

                baseReport.Players = g
                    .SelectMany(r => r.Players)
                    .GroupBy(p => new { p.PlayerId, p.SessionId })
                    .Select(pg => pg.First())
                    .ToList();

                return baseReport;
            })
            .ToList();

        var reportIds = mergedReports.Select(r => r.Id).Distinct().ToList();
        var existingReportIds = reportIds.Count == 0
            ? new HashSet<long>()
            : await context.ActivityReports
                .Where(ar => reportIds.Contains(ar.Id))
                .Select(ar => ar.Id)
                .ToHashSetAsync();

        var existingPlayerReports = reportIds.Count == 0
            ? new Dictionary<(long ReportId, int SessionId), ActivityReportPlayer>()
            : await context.ActivityReportPlayers
                .Where(arp => arp.PlayerId == playerId && reportIds.Contains(arp.ActivityReportId))
                .ToDictionaryAsync(
                    arp => (arp.ActivityReportId, arp.SessionId),
                    arp => arp);


        foreach (var report in mergedReports)
        {
            var conquestActivityId = ResolveConquestActivityId(report.BungieActivityId, report.Date, conquestLookup);
            if (conquestActivityId.HasValue)
            {
                report.ActivityId = conquestActivityId.Value;
                foreach (var playerReport in report.Players)
                {
                    playerReport.ActivityId = conquestActivityId.Value;
                }
            }

            if (existingReportIds.Contains(report.Id) == false)
            {
                reportsToInsert.Add(report);
                if (report.Players.Count > 0)
                {
                    playerReportsToInsert.AddRange(report.Players);
                }
                continue;
            }

            foreach (var incomingPlayerReport in report.Players)
            {
                if (existingPlayerReports.TryGetValue((incomingPlayerReport.ActivityReportId, incomingPlayerReport.SessionId), out var existingPlayerReport) == false)
                {
                    playerReportsToInsert.Add(incomingPlayerReport);
                    continue;
                }

                if (IsPlayerReportChanged(existingPlayerReport, incomingPlayerReport))
                {
                    existingPlayerReport.SessionId = incomingPlayerReport.SessionId;
                    existingPlayerReport.Score = incomingPlayerReport.Score;
                    existingPlayerReport.Completed = incomingPlayerReport.Completed;
                    existingPlayerReport.Duration = incomingPlayerReport.Duration;
                    existingPlayerReport.ActivityId = incomingPlayerReport.ActivityId;
                }
            }

            if (report.NeedsFullCheck)
            {
                reportsNeedingFullCheck.Add(report.Id);
            }
        }

        if (reportsToInsert.Count > 0)
        {
            await BulkInsertActivityReportsAsync(context, reportsToInsert);
        }
        if (playerReportsToInsert.Count > 0)
        {
            await BulkInsertActivityReportPlayersAsync(context, playerReportsToInsert);
        }
        if (reportsNeedingFullCheck.Count > 0)
        {
            var ids = reportsNeedingFullCheck.ToArray();
            await context.Database.ExecuteSqlRawAsync(
                "UPDATE \"ActivityReports\" SET \"NeedsFullCheck\" = TRUE WHERE \"Id\" = ANY ({0})",
                ids);
        }
        await context.SaveChangesAsync();
    }

    private static async Task BulkInsertActivityReportsAsync(AppDbContext context, List<ActivityReport> reports)
    {
        const int batchSize = 500;
        for (var i = 0; i < reports.Count; i += batchSize)
        {
            var batch = reports.Skip(i).Take(batchSize).ToList();
            if (batch.Count == 0)
            {
                continue;
            }

            var sql = new StringBuilder();
            sql.Append("INSERT INTO \"ActivityReports\" (\"Id\", \"Date\", \"ActivityId\", \"NeedsFullCheck\") VALUES ");

            await using var command = new NpgsqlCommand();
            for (var index = 0; index < batch.Count; index++)
            {
                var report = batch[index];
                if (index > 0)
                {
                    sql.Append(", ");
                }

                var idParam = $"@p{index}_id";
                var dateParam = $"@p{index}_date";
                var activityParam = $"@p{index}_activity";
                var needsFullCheckParam = $"@p{index}_needs";

                sql.Append($"({idParam}, {dateParam}, {activityParam}, {needsFullCheckParam})");

                command.Parameters.AddWithValue(idParam, report.Id);
                command.Parameters.AddWithValue(dateParam, report.Date);
                command.Parameters.AddWithValue(activityParam, report.ActivityId);
                command.Parameters.AddWithValue(needsFullCheckParam, report.NeedsFullCheck);
            }

            sql.Append(" ON CONFLICT (\"Id\") DO NOTHING;");

            command.CommandText = sql.ToString();
            var connection = (NpgsqlConnection)context.Database.GetDbConnection();
            command.Connection = connection;
            if (connection.State != System.Data.ConnectionState.Open)
            {
                await connection.OpenAsync();
            }

            var transaction = context.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction;
            if (transaction != null)
            {
                command.Transaction = transaction;
            }

            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task BulkInsertActivityReportPlayersAsync(AppDbContext context, List<ActivityReportPlayer> players)
    {
        const int batchSize = 500;
        for (var i = 0; i < players.Count; i += batchSize)
        {
            var batch = players.Skip(i).Take(batchSize).ToList();
            if (batch.Count == 0)
            {
                continue;
            }

            var sql = new StringBuilder();
            sql.Append("INSERT INTO \"ActivityReportPlayers\" (\"PlayerId\", \"SessionId\", \"ActivityReportId\", \"Score\", \"Completed\", \"Duration\", \"ActivityId\") VALUES ");

            await using var command = new NpgsqlCommand();
            for (var index = 0; index < batch.Count; index++)
            {
                var player = batch[index];
                if (index > 0)
                {
                    sql.Append(", ");
                }

                var playerIdParam = $"@p{index}_player";
                var sessionIdParam = $"@p{index}_session";
                var reportIdParam = $"@p{index}_report";
                var scoreParam = $"@p{index}_score";
                var completedParam = $"@p{index}_completed";
                var durationParam = $"@p{index}_duration";
                var activityParam = $"@p{index}_activity";

                sql.Append($"({playerIdParam}, {sessionIdParam}, {reportIdParam}, {scoreParam}, {completedParam}, {durationParam}, {activityParam})");

                command.Parameters.AddWithValue(playerIdParam, player.PlayerId);
                command.Parameters.AddWithValue(sessionIdParam, player.SessionId);
                command.Parameters.AddWithValue(reportIdParam, player.ActivityReportId);
                command.Parameters.AddWithValue(scoreParam, player.Score);
                command.Parameters.AddWithValue(completedParam, player.Completed);
                command.Parameters.AddWithValue(durationParam, player.Duration);
                command.Parameters.AddWithValue(activityParam, player.ActivityId);
            }

            sql.Append(" ON CONFLICT (\"ActivityReportId\", \"PlayerId\", \"SessionId\") DO NOTHING;");

            command.CommandText = sql.ToString();
            var connection = (NpgsqlConnection)context.Database.GetDbConnection();
            command.Connection = connection;
            if (connection.State != System.Data.ConnectionState.Open)
            {
                await connection.OpenAsync();
            }

            var transaction = context.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction;
            if (transaction != null)
            {
                command.Transaction = transaction;
            }

            await command.ExecuteNonQueryAsync();
        }
    }

    private static bool IsPlayerReportChanged(ActivityReportPlayer existing, ActivityReportPlayer incoming)
    {
        return existing.Score != incoming.Score
            || existing.SessionId != incoming.SessionId
            || existing.Completed != incoming.Completed
            || existing.Duration != incoming.Duration
            || existing.ActivityId != incoming.ActivityId;
    }

    private static int GetNextSessionId(
        ConcurrentDictionary<(long ReportId, long PlayerId), int> sessionCounters,
        long reportId,
        long playerId)
    {
        return sessionCounters.AddOrUpdate((reportId, playerId), 1, (_, current) => current + 1);
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

    public async Task CrawlActivityReport(long reportId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var activityReport = await context.ActivityReports.FirstOrDefaultAsync(ar => ar.Id == reportId);
        if (activityReport == null)
        {
            _logger.LogWarning("Activity report {ReportId} not found.", reportId);
            return;
        }
        var pgcr = (await _bungieClient.GetPostGameCarnageReport(reportId)).Response;

        var activityHashMap = await GetActivityHashMap();
        var activityId = activityHashMap.TryGetValue(pgcr.activityDetails.referenceId, out var mapped) ? mapped : 0;

        if (activityId == 0)
        {
            _logger.LogError("Unknown activity ID {activityId} in report {ReportId}", pgcr.activityDetails.referenceId, reportId);
            context.ActivityReports.Remove(activityReport);
            await context.SaveChangesAsync();
            return;
        }

        var conquestLookup = await GetConquestLookupAsync();
        var conquestActivityId = ResolveConquestActivityId(pgcr.activityDetails.referenceId, activityReport.Date, conquestLookup);
        if (conquestActivityId.HasValue)
        {
            activityId = conquestActivityId.Value;
        }

        activityReport.ActivityId = activityId;

        var publicEntries = pgcr.entries.Where(e => e.player.destinyUserInfo.isPublic).ToList();

        if (publicEntries.Count > 0)
        {
            var playerData = publicEntries
                .Select(e => new Player
                {
                    Id = long.Parse(e.player.destinyUserInfo.membershipId),
                    MembershipType = e.player.destinyUserInfo.membershipType,
                    DisplayName = e.player.destinyUserInfo.displayName,
                    DisplayNameCode = e.player.destinyUserInfo.bungieGlobalDisplayNameCode,
                    FullDisplayName = $"{e.player.destinyUserInfo.displayName}#{e.player.destinyUserInfo.bungieGlobalDisplayNameCode:0000}"
                })
                .DistinctBy(p => p.Id)
                .ToList();

            if (playerData.Count > 0)
            {
                var playerIds = playerData.Select(p => p.Id).ToList();

                var existingPlayerIds = await context.Players
                    .Where(p => playerIds.Contains(p.Id))
                    .Select(p => p.Id)
                    .ToListAsync();

                var newPlayerData = playerData.Where(p => !existingPlayerIds.Contains(p.Id)).ToList();

                if (newPlayerData.Count > 0)
                {
                    foreach (var player in newPlayerData)
                    {
                        while (!await _redis.StringSetAsync($"lock:player:{player.Id}", player.Id, when: When.NotExists, expiry: TimeSpan.FromSeconds(10)))
                        {
                            await Task.Delay(Random.Shared.Next(50, 200));
                        }

                        try
                        {
                            if (!await context.Players.AnyAsync(p => p.Id == player.Id))
                            {
                                context.Players.Add(player);
                                context.PlayerCrawlQueue.Add(new PlayerCrawlQueue(player.Id));
                                await context.SaveChangesAsync();
                            }
                        }
                        finally
                        {
                            await _redis.KeyDeleteAsync($"lock:player:{player.Id}");
                        }
                    }
                }
            }
        }

        if (context.ChangeTracker.HasChanges())
        {
            await context.SaveChangesAsync();
        }

        _logger.LogInformation("Processed activity report {ReportId} with {PlayerCount} players.", reportId, publicEntries.Count);
    }
}
