using API.Clients.Abstract;
using Crawler.Helpers;
using Domain.Data;
using Domain.DB;
using Domain.DestinyApi;
using Domain.DTO;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;
using StackExchange.Redis;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;

namespace Crawler.Services
{
    public class CharacterCrawler : BackgroundService
    {
        private readonly IDbContextFactory<AppDbContext> _contextFactory;
        private readonly IDestiny2ApiClient _client;
        private readonly ChannelReader<CharacterWorkItem> _input;
        private readonly ILogger<CharacterCrawler> _logger;
        private readonly ConcurrentDictionary<long, int> _playerCharacterWorkCount;
        private readonly IMemoryCache _cache;
        private readonly IConnectionMultiplexer _redis;

        private const int MaxConcurrentTasks = 20;
        private static readonly DateTime ActivityCutoffUtc = new DateTime(2025, 7, 15);

        public CharacterCrawler(
            IDestiny2ApiClient client,
            ChannelReader<CharacterWorkItem> input,
            ILogger<CharacterCrawler> logger,
            IDbContextFactory<AppDbContext> contextFactory,
            ConcurrentDictionary<long, int> playerCharacterWorkCount,
            IMemoryCache cache,
            IConnectionMultiplexer redis)
        {
            _client = client;
            _input = input;
            _logger = logger;
            _contextFactory = contextFactory;
            _playerCharacterWorkCount = playerCharacterWorkCount;
            _cache = cache;
            _redis = redis;
        }

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            var activeTasks = new List<Task>();
            _logger.LogInformation("Character crawler started.");
            try
            {
                await foreach (var item in _input.ReadAllAsync(ct))
                {
                    while (activeTasks.Count >= MaxConcurrentTasks)
                    {
                        var completedTask = await Task.WhenAny(activeTasks);
                        activeTasks.Remove(completedTask);
                    }

                    activeTasks.Add(ProcessItemAsync(item, ct));
                }

                await Task.WhenAll(activeTasks);
                _logger.LogInformation("Character crawler completed.");
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Character crawler cancellation requested.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in character crawler loop.");
            }
        }

        private async Task ProcessItemAsync(CharacterWorkItem item, CancellationToken ct)
        {
            try
            {
                await using var context = await _contextFactory.CreateDbContextAsync(ct);
                var player = await context.Players.FindAsync(item.PlayerId, ct) ?? throw new InvalidDataException($"Player with Id {item.PlayerId} cannot be found");

                var reports = await GetCharacterActivityReports(player, item.LastPlayed, item.CharacterId, ct);
                var insertedReportIds = await CreateActivityReports(reports, ct);
                var playerReports = reports
                    .Where(r => insertedReportIds.Contains(r.Id))
                    .SelectMany(r => r.Players)
                    .ToList();
                context.ActivityReportPlayers.AddRange(playerReports);
                await context.SaveChangesAsync(ct);

                player.NeedsFullCheck = false;
                await context.SaveChangesAsync(ct);
                _logger.LogInformation(
                    "Created {ReportCount} activity reports for player {PlayerId} character {CharacterId}.",
                    insertedReportIds.Count,
                    item.PlayerId,
                    item.CharacterId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing CharacterWorkItem for player: {PlayerId}, character: {CharacterId}", item.PlayerId, item.CharacterId);
                await using var context = await _contextFactory.CreateDbContextAsync(ct);
                var playerQueueItem = await context.PlayerCrawlQueue.FirstOrDefaultAsync(p => p.PlayerId == item.PlayerId, ct)
                    ?? throw new InvalidOperationException($"Player with Id {item.PlayerId} cannot be found");
                var player = await context.Players.FirstOrDefaultAsync(p => p.Id == item.PlayerId, ct)
                    ?? throw new InvalidOperationException($"Player with Id {item.PlayerId} cannot be found");

                playerQueueItem.Status = PlayerQueueStatus.Error;
                player.NeedsFullCheck = true;


                await context.SaveChangesAsync(ct);
            }
            finally
            {
                await FinalizeCharacterWorkAsync(item.PlayerId, ct);
            }
        }

        public async Task<List<ActivityReport>> GetCharacterActivityReports(Player player, DateTime lastPlayedActivityDate, string characterId, CancellationToken ct)
        {
            lastPlayedActivityDate = player.NeedsFullCheck ? ActivityCutoffUtc : lastPlayedActivityDate;

            var page = 0;
            var reports = new List<ActivityReport>();
            var hasReachedLastUpdate = false;
            var activityCount = 250;

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

                        reports.Add(new ActivityReport
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
                return reports;
            }
            catch (DestinyApiException ex) when (ex.ErrorCode == 1665)
            {
                _logger.LogWarning(ex, "Historical stats throttled for player {PlayerId} character {CharacterId}.", player.Id, characterId);
                return new List<ActivityReport>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching activity reports for player: {PlayerId}, character: {CharacterId}", player.Id, characterId);
                throw;
            }
        }

        private async Task<List<long>> CreateActivityReports(IEnumerable<ActivityReport> activityReports, CancellationToken ct)
        {
            var reportList = activityReports.ToList();
            if (reportList.Count == 0)
            {
                return new List<long>();
            }

            var parameters = new List<NpgsqlParameter>();
            var valueStrings = new List<string>();
            int parameterIndex = 0;

            foreach (var activityReport in reportList)
            {
                var pId = new NpgsqlParameter($"pId{parameterIndex}", activityReport.Id);
                var pDate = new NpgsqlParameter($"pDate{parameterIndex}", activityReport.Date);
                var pActivityId = new NpgsqlParameter($"pActivityId{parameterIndex}", activityReport.ActivityId);
                var pNeedsFullCheck = new NpgsqlParameter($"pNeedsFullCheck{parameterIndex}", activityReport.NeedsFullCheck);
                parameters.Add(pId);
                parameters.Add(pDate);
                parameters.Add(pActivityId);
                parameters.Add(pNeedsFullCheck);
                valueStrings.Add($"(@pId{parameterIndex}, @pDate{parameterIndex}, @pActivityId{parameterIndex}, @pNeedsFullCheck{parameterIndex})");
                parameterIndex++;
            }

            var sql = $@"
                INSERT INTO ""ActivityReports"" (""Id"", ""Date"", ""ActivityId"", ""NeedsFullCheck"")
                VALUES {string.Join(", ", valueStrings)}
                ON CONFLICT (""Id"") DO NOTHING
                RETURNING ""Id""";

            await using var context = await _contextFactory.CreateDbContextAsync(ct);
            return await context.Database
                .SqlQueryRaw<long>(sql, parameters.ToArray())
                .ToListAsync(ct);
        }

        private async Task FinalizeCharacterWorkAsync(long playerId, CancellationToken ct)
        {
            var remaining = DecrementCharacterWorkCount(playerId);
            if (remaining > 0)
            {
                return;
            }

            _playerCharacterWorkCount.TryRemove(playerId, out _);

            await using var context = await _contextFactory.CreateDbContextAsync(ct);
            var playerQueueItem = await context.PlayerCrawlQueue.FirstOrDefaultAsync(pcq => pcq.PlayerId == playerId, ct);
            if (playerQueueItem == null || playerQueueItem.Status != PlayerQueueStatus.Processing)
            {
                return;
            }

            playerQueueItem.Status = PlayerQueueStatus.Completed;
            playerQueueItem.ProcessedAt = DateTime.UtcNow;
            await context.SaveChangesAsync(ct);
            _logger.LogInformation("Completed crawling for player {PlayerId}; no pending PGCR work detected.", playerId);
        }

        private int DecrementCharacterWorkCount(long playerId)
        {
            while (true)
            {
                if (!_playerCharacterWorkCount.TryGetValue(playerId, out var current))
                {
                    return 0;
                }

                var updated = current <= 0 ? 0 : current - 1;
                if (_playerCharacterWorkCount.TryUpdate(playerId, updated, current))
                {
                    return updated;
                }
            }
        }
    }
}
