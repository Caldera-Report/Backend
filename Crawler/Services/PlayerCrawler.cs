using API.Clients.Abstract;
using Crawler.Helpers;
using Domain.Data;
using Domain.DB;
using Domain.DestinyApi;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using StackExchange.Redis;
using System.Collections.Concurrent;

namespace Crawler.Services
{
    public class PlayerCrawler : BackgroundService
    {
        private IDbContextFactory<AppDbContext> _contextFactory;
        private readonly IDestiny2ApiClient _client;
        private readonly ILogger<PlayerCrawler> _logger;
        private readonly IMemoryCache _cache;
        private readonly IDatabase _redis;

        private const int MaxConcurrentPlayers = 20;
        private static readonly DateTime ActivityCutoffUtc = new DateTime(2025, 7, 15);

        public PlayerCrawler(
            IDestiny2ApiClient client,
            ILogger<PlayerCrawler> logger,
            IDbContextFactory<AppDbContext> contextFactory,
            IMemoryCache cache,
            IConnectionMultiplexer redis)
        {
            _client = client;
            _logger = logger;
            _contextFactory = contextFactory;
            _cache = cache;
            _redis = redis.GetDatabase();
        }

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            _logger.LogInformation("Player crawler started processing queue with {MaxConcurrency} concurrent workers.", MaxConcurrentPlayers);
            var activeTasks = new List<Task>();

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    while (activeTasks.Count >= MaxConcurrentPlayers)
                    {
                        var completedTask = await Task.WhenAny(activeTasks);
                        activeTasks.Remove(completedTask);
                        await completedTask;
                    }

                    var playerQueueId = await GetNextPlayerQueueItem(ct);
                    if (playerQueueId == null)
                    {
                        if (activeTasks.Count == 0)
                        {
                            await Task.Delay(1000, ct);
                        }
                        else
                        {
                            var completedTask = await Task.WhenAny(activeTasks);
                            activeTasks.Remove(completedTask);
                            await completedTask;
                        }
                        continue;
                    }

                    activeTasks.Add(ProcessPlayerAsync(playerQueueId.Value, ct));
                }

                await Task.WhenAll(activeTasks);
                _logger.LogInformation("Player crawler completed.");
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Player crawler cancellation requested.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in player crawler loop.");
            }
        }

        private async Task<long?> GetNextPlayerQueueItem(CancellationToken ct)
        {
            try
            {
                await using var context = await _contextFactory.CreateDbContextAsync(ct);
                var processingStatus = (int)PlayerQueueStatus.Processing;
                var queuedStatus = (int)PlayerQueueStatus.Queued;
                var errorStatus = (int)PlayerQueueStatus.Error;
                var maxAttempts = 3;

                var playerValues = await context.Database.SqlQuery<PlayerCrawlQueue>($@"
                    UPDATE ""PlayerCrawlQueue""
                    SET ""Status"" = {processingStatus}, ""Attempts"" = ""Attempts"" + 1
                    WHERE ""Id"" = (
                        SELECT ""Id""
                        FROM ""PlayerCrawlQueue""
                        WHERE ""Status"" IN ({queuedStatus}, {errorStatus})
                            AND ""Attempts"" < {maxAttempts}
                        ORDER BY ""Id""
                        FOR UPDATE SKIP LOCKED
                        LIMIT 1
                    )
                    RETURNING *").ToListAsync(ct);
                var playerValue = playerValues.FirstOrDefault();

                return playerValue?.PlayerId ?? null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching next player queue item.");
                return null;
            }
        }

        private async Task ProcessPlayerAsync(long playerId, CancellationToken ct)
        {
            try
            {
                await using var context = await _contextFactory.CreateDbContextAsync(ct);
                var playerValue = await context.PlayerCrawlQueue.FirstOrDefaultAsync(p => p.PlayerId == playerId, ct);
                if (playerValue is null)
                {
                    _logger.LogWarning("Player queue item for PlayerId {PlayerId} not found; skipping.", playerId);
                    return;
                }
                var player = await context.Players.FirstOrDefaultAsync(p => p.Id == playerValue.PlayerId, ct);
                if (player is null)
                {
                    _logger.LogWarning("Player {PlayerId} not found in database; skipping work item.", playerValue.Id);
                    return;
                }

                var characters = await GetCharactersForPlayer(playerValue.PlayerId, player.MembershipType, context, ct);
                var lastPlayedActivityDate = await context.ActivityReports
                    .AsNoTracking()
                    .Where(r => r.Players.Any(p => p.PlayerId == playerValue.PlayerId) && !r.NeedsFullCheck)
                    .OrderByDescending(r => r.Date)
                    .Select(r => (DateTime?)r.Date)
                    .FirstOrDefaultAsync(ct);

                IEnumerable<KeyValuePair<string, DestinyCharacterComponent>> charactersToProcess;
                if (player.LastCrawlStarted == null || player.NeedsFullCheck)
                {
                    charactersToProcess = characters;
                }
                else
                {
                    charactersToProcess = characters.Where(c => c.Value.dateLastPlayed > (player.LastCrawlStarted ?? ActivityCutoffUtc));
                }

                var allReports = new ConcurrentBag<ActivityReport>();
                var characterTasks = charactersToProcess.Select(character =>
                    GetCharacterActivityReports(player, lastPlayedActivityDate ?? ActivityCutoffUtc, character.Key, allReports, ct)
                ).ToList();

                await Task.WhenAll(characterTasks);

                if (allReports.IsEmpty)
                {
                    player.NeedsFullCheck = false;
                    playerValue.Status = PlayerQueueStatus.Completed;
                    playerValue.ProcessedAt = DateTime.UtcNow;
                    await context.SaveChangesAsync(ct);
                    _logger.LogInformation("No new activities for player {PlayerId}; marked as completed.", playerValue.PlayerId);
                }
                else
                {
                    player.LastCrawlStarted = DateTime.UtcNow;
                    await context.SaveChangesAsync(ct);
                    
                    await CreateActivityReports(allReports, playerId, ct);
                    
                    await using var finalContext = await _contextFactory.CreateDbContextAsync(ct);
                    var finalPlayerQueueItem = await finalContext.PlayerCrawlQueue.FirstOrDefaultAsync(pcq => pcq.PlayerId == playerId, ct);
                    var finalPlayer = await finalContext.Players.FirstOrDefaultAsync(p => p.Id == playerId, ct);
                    
                    if (finalPlayerQueueItem != null)
                    {
                        finalPlayerQueueItem.Status = PlayerQueueStatus.Completed;
                        finalPlayerQueueItem.ProcessedAt = DateTime.UtcNow;
                    }
                    
                    await ComputeLeaderboardsForPlayer(playerId, ct);
                    
                    if (finalPlayer != null)
                    {
                        finalPlayer.LastCrawlCompleted = DateTime.UtcNow;
                        finalPlayer.NeedsFullCheck = false;
                    }
                    
                    await finalContext.SaveChangesAsync(ct);
                    
                    _logger.LogInformation("Created {ReportCount} activity reports for player {PlayerId}.", allReports.Count, playerValue.PlayerId);
                }
            }
            catch (DestinyApiException ex) when (ex.ErrorCode == 1601)
            {
                _logger.LogError("Player {PlayerId} does not exist", playerId);
                try
                {
                    await using var context = await _contextFactory.CreateDbContextAsync(ct);
                    var playerValue = await context.PlayerCrawlQueue.FirstOrDefaultAsync(p => p.PlayerId == playerId, ct);
                    if (playerValue == null)
                    {
                        _logger.LogWarning("Player crawl queue item for PlayerId {PlayerId} not found; cannot remove.", playerId);
                        return;
                    }
                    context.PlayerCrawlQueue.Remove(playerValue);
                    var player = await context.Players.FirstOrDefaultAsync(p => p.Id == playerValue.PlayerId, ct);
                    if (player != null)
                    {
                        context.Players.Remove(player);
                    }
                    await context.SaveChangesAsync(ct);
                }
                catch (Exception innerEx)
                {
                    _logger.LogError(innerEx, "Error removing player {PlayerId}", playerId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing player {PlayerId}.", playerId);
                try
                {
                    await using var context = await _contextFactory.CreateDbContextAsync(ct);
                    var queueItem = await context.PlayerCrawlQueue.FirstOrDefaultAsync(p => p.PlayerId == playerId, ct);
                    if (queueItem != null)
                    {
                        queueItem.Status = PlayerQueueStatus.Error;
                        await context.SaveChangesAsync(ct);
                    }
                }
                catch (Exception innerEx)
                {
                    _logger.LogError(innerEx, "Error updating player queue status to Error for player {PlayerId}.", playerId);
                }
            }
        }

        public async Task<Dictionary<string, DestinyCharacterComponent>> GetCharactersForPlayer(long membershipId, int membershipType, AppDbContext context, CancellationToken ct)
        {
            try
            {
                var characters = await _client.GetCharactersForPlayer(membershipId, membershipType, ct);

                if (characters.ErrorCode == 1665)
                {
                    _logger.LogWarning("Player {PlayerId} has a private profile; skipping.", membershipId);
                    return new Dictionary<string, DestinyCharacterComponent>();
                }

                await CheckPlayerNameAndEmblem(characters.Response, membershipId, context, ct);
                return characters.Response.characters.data;
            }
            catch
            {
                throw;
            }
        }

        public async Task CheckPlayerNameAndEmblem(DestinyProfileResponse profile, long id, AppDbContext context, CancellationToken ct)
        {
            var player = await context.Players.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (player == null)
            {
                _logger.LogWarning("Skipping display name sync; player {PlayerId} not found.", id);
                return;
            }

            if (player.DisplayName != profile.profile.data.userInfo.bungieGlobalDisplayName ||
                player.DisplayNameCode != profile.profile.data.userInfo.bungieGlobalDisplayNameCode)
            {
                player.DisplayName = profile.profile.data.userInfo.bungieGlobalDisplayName;
                player.DisplayNameCode = profile.profile.data.userInfo.bungieGlobalDisplayNameCode;
                player.FullDisplayName = player.DisplayName + "#" + player.DisplayNameCode;
                context.Players.Update(player);
                await context.SaveChangesAsync(ct);
                _logger.LogInformation("Updated display information for player {PlayerId}.", id);
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
                    await context.SaveChangesAsync(ct);
                    _logger.LogInformation("Updated last played character emblem for player {PlayerId}.", id);
                }
            }
        }

        public async Task GetCharacterActivityReports(Player player, DateTime lastPlayedActivityDate, string characterId, ConcurrentBag<ActivityReport> reportsBag, CancellationToken ct)
        {
            lastPlayedActivityDate = player.NeedsFullCheck ? ActivityCutoffUtc : lastPlayedActivityDate;

            await using var context = await _contextFactory.CreateDbContextAsync(ct);

            var page = 0;
            var hasReachedLastUpdate = false;
            var activityCount = 250;
            var existingReportIds = player.NeedsFullCheck ? new HashSet<long>() : await context.ActivityReportPlayers
                .Where(arp => arp.PlayerId == player.Id)
                .Select(arp => arp.ActivityReportId)
                .ToHashSetAsync(ct);

            try
            {
                while (!hasReachedLastUpdate)
                {
                    var response = await _client.GetHistoricalStatsForCharacter(player.Id, player.MembershipType, characterId, page, activityCount, ct);
                    if (response.Response?.activities == null || !response.Response.activities.Any())
                        break;
                    page++;
                    var activityHashMap = await _cache.GetActivityHashMapAsync(_redis);
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

                    }
                    if (response.Response.activities.Last().period < ActivityCutoffUtc)
                        break;
                }
            }
            catch (DestinyApiException ex) when (ex.ErrorCode == 1665)
            {
                _logger.LogWarning(ex, "Historical stats throttled for player {PlayerId} character {CharacterId}.", player.Id, characterId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching activity reports for player: {PlayerId}, character: {CharacterId}", player.Id, characterId);
                throw;
            }
        }

        private async Task CreateActivityReports(IEnumerable<ActivityReport> activityReports, long playerId, CancellationToken ct)
        {
            var reportList = activityReports.ToList();
            if (reportList.Count == 0)
            {
                return;
            }

            await using var context = await _contextFactory.CreateDbContextAsync(ct);

            var playerIds = reportList.SelectMany(r => r.Players).Select(p => p.PlayerId).Distinct().ToList();

            foreach (var report in reportList)
            {
                while (!await _redis.StringSetAsync($"locks:activities:{report.Id}", report.Id, when: When.NotExists, expiry: TimeSpan.FromSeconds(10)))
                {
                    await Task.Delay(Random.Shared.Next(50, 200));
                }
                try
                {
                    var existing = await context.ActivityReports.Include(ar => ar.Players).FirstOrDefaultAsync(ar => ar.Id == report.Id, ct);

                    if (existing is null)
                    {
                        context.ActivityReports.Add(report);
                    }
                    else
                    {
                        foreach (var incomingPlayerReport in report.Players)
                        {
                            var existingPlayerReport = existing.Players.FirstOrDefault(arp => arp.PlayerId == playerId && arp.SessionId == incomingPlayerReport.SessionId);

                            if (existingPlayerReport is null)
                            {
                                context.ActivityReportPlayers.Add(incomingPlayerReport);
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

                        existing.NeedsFullCheck |= report.NeedsFullCheck;
                    }
                    await context.SaveChangesAsync(ct);
                }
                finally
                {
                    await _redis.KeyDeleteAsync($"locks:activities:{report.Id}");
                }
            }
        }

        private async Task ComputeLeaderboardsForPlayer(long playerId, CancellationToken ct)
        {
            await using var context = await _contextFactory.CreateDbContextAsync(ct);
            var activityReports = await context.ActivityReportPlayers
                .AsNoTracking()
                .Where(arp => arp.PlayerId == playerId)
                .GroupBy(arp => arp.ActivityId)
                .ToDictionaryAsync(
                    arpd => arpd.Key,
                    arpd => arpd.ToList(),
                    ct);
            foreach (var activityReport in activityReports)
            {
                foreach (var leaderboardType in Enum.GetValues<LeaderboardTypes>())
                {
                    var leaderboardEntry = await context.PlayerLeaderboards.FirstOrDefaultAsync(pl => pl.PlayerId == playerId && pl.ActivityId == activityReport.Key && pl.LeaderboardType == leaderboardType);
                    if (leaderboardEntry != null)
                    {
                        leaderboardEntry.Data = CalculateData(activityReport.Value, leaderboardType);
                    }
                    else
                    {
                        var newLeaderboardEntry = new PlayerLeaderboard()
                        {
                            ActivityId = activityReport.Key,
                            PlayerId = playerId,
                            LeaderboardType = leaderboardType,
                            Data = CalculateData(activityReport.Value, leaderboardType)
                        };
                        if (newLeaderboardEntry.Data == 0)
                        {
                            continue;
                        }
                        context.PlayerLeaderboards.Add(newLeaderboardEntry);
                    }
                }
                await context.SaveChangesAsync();
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

        private static long CalculateData(List<ActivityReportPlayer> reports, LeaderboardTypes leaderboardType)
        {
            if (reports.Count(arp => arp.Completed) == 0)
            {
                return 0;
            }
            switch (leaderboardType)
            {
                case LeaderboardTypes.TotalCompletions:
                    return reports.Count(arp => arp.Completed);
                case LeaderboardTypes.FastestCompletion:
                    return (long)reports.Where(arp => arp.Completed).Min(arp => arp.Duration).TotalSeconds;
                case LeaderboardTypes.HighestScore:
                    return reports.Where((arp) => arp.Completed).Max(arp => arp.Score);
                default: return 0;
            }
        }
    }
}
