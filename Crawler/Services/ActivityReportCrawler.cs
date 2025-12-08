using API.Clients.Abstract;
using Crawler.Helpers;
using Domain.Data;
using Domain.DB;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;
using StackExchange.Redis;

namespace Crawler.Services
{
    public class ActivityReportCrawler : BackgroundService
    {
        private IDbContextFactory<AppDbContext> _contextFactory;
        private readonly IDestiny2ApiClient _client;
        private readonly ILogger<ActivityReportCrawler> _logger;
        private readonly IMemoryCache _cache;
        private readonly IDatabase _redis;

        private const int MaxConcurrentTasks = 150;

        public ActivityReportCrawler(
            ILogger<ActivityReportCrawler> logger,
            IDbContextFactory<AppDbContext> contextFactory,
            IDestiny2ApiClient client,
            IMemoryCache cache,
            IConnectionMultiplexer redis)
        {
            _logger = logger;
            _contextFactory = contextFactory;
            _client = client;
            _cache = cache;
            _redis = redis.GetDatabase();
        }

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            _logger.LogInformation("Activity report crawler started processing queue.");
            var activeTasks = new List<Task>();

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    while (activeTasks.Count >= MaxConcurrentTasks)
                    {
                        var completedTask = await Task.WhenAny(activeTasks);
                        activeTasks.Remove(completedTask);
                        await completedTask;
                    }

                    await using var context = await _contextFactory.CreateDbContextAsync(ct);
                    var activityReportIds = await context.Database.SqlQueryRaw<long>(@$"
                        UPDATE ""ActivityReports""
                        SET ""NeedsFullCheck"" = false
                        WHERE ""Id"" = (
                            SELECT ""Id""
                            FROM ""ActivityReports""
                            WHERE ""NeedsFullCheck"" = true
                            ORDER BY ""Id""
                            FOR UPDATE SKIP LOCKED
                            LIMIT 1
                        )
                        RETURNING ""Id""").ToListAsync(ct);
                    var activityReportId = activityReportIds.FirstOrDefault();
                    if (activityReportId == 0)
                    {
                        if (activeTasks.Count > 0)
                        {
                            await Task.WhenAll(activeTasks);
                            activeTasks.Clear();
                        }
                        await Task.Delay(1000, ct);
                        continue;
                    }

                    var activityReport = await context.ActivityReports.FirstOrDefaultAsync(ar => ar.Id == activityReportId, ct);
                    if (activityReport == null)
                    {
                        _logger.LogWarning("Activity report {ReportId} not found after claiming.", activityReportId);
                        continue;
                    }

                    activeTasks.Add(ProcessActivityReportAsync(activityReport.Id, ct));
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Activity report crawler cancellation requested.");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing activity report crawl queue.");
                }
            }

            if (activeTasks.Count > 0)
            {
                try
                {
                    await Task.WhenAll(activeTasks);
                }
                catch (Exception ex) when (ex is OperationCanceledException or AggregateException)
                {
                    _logger.LogDebug(ex, "Activity report crawler tasks cancelled during shutdown.");
                }
            }
        }

        private async Task ProcessActivityReportAsync(long reportId, CancellationToken ct)
        {
            await using var context = await _contextFactory.CreateDbContextAsync(ct);

            try
            {
                var activityReport = await context.ActivityReports.FirstOrDefaultAsync(ar => ar.Id == reportId, ct);
                if (activityReport == null)
                {
                    _logger.LogWarning("Activity report {ReportId} not found.", reportId);
                    return;
                }
                var pgcr = (await _client.GetPostGameCarnageReport(reportId, ct)).Response;

                var activityHashMap = await _cache.GetActivityHashMapAsync(_redis);
                var activityId = activityHashMap.TryGetValue(pgcr.activityDetails.referenceId, out var mapped) ? mapped : 0;

                if (activityId == 0)
                {
                    _logger.LogError("Unknown activity ID {activityId} in report {ReportId}", pgcr.activityDetails.referenceId, reportId);
                    context.ActivityReports.Remove(activityReport);
                    await context.SaveChangesAsync(ct);
                    return;
                }

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
                        })
                        .DistinctBy(p => p.Id)
                        .ToList();

                    if (playerData.Count > 0)
                    {
                        var playerIds = playerData.Select(p => p.Id).ToList();

                        var existingPlayerIds = await context.Players
                            .Where(p => playerIds.Contains(p.Id))
                            .Select(p => p.Id)
                            .ToListAsync(ct);

                        var newPlayerData = playerData.Where(p => !existingPlayerIds.Contains(p.Id)).ToList();

                        if (newPlayerData.Count > 0)
                        {
                            foreach (var player in newPlayerData)
                            {
                                while (!await _redis.StringSetAsync($"lock:player:{player.Id}", player.Id, when: When.NotExists, expiry: TimeSpan.FromSeconds(10)))
                                {
                                    await Task.Delay(Random.Shared.Next(50, 200), ct);
                                }

                                try
                                {
                                    if (!await context.Players.AnyAsync(p => p.Id == player.Id, ct))
                                    {
                                        context.Players.Add(player);
                                        context.PlayerCrawlQueue.Add(new PlayerCrawlQueue(player.Id));
                                        await context.SaveChangesAsync(ct);
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

                _logger.LogInformation("Processed activity report {ReportId} with {PlayerCount} players.", reportId, publicEntries.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing activity report {ReportId}", reportId);
                var activityReport = await context.ActivityReports.FirstOrDefaultAsync(ar => ar.Id == reportId, ct);
                if (activityReport == null)
                {
                    _logger.LogWarning("Activity report {ReportId} not found in error handler.", reportId);
                    return;
                }
                activityReport.NeedsFullCheck = true;
                await context.SaveChangesAsync(ct);
            }
        }
    }
}
