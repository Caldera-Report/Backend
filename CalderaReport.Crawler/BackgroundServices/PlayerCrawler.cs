using CalderaReport.Domain.Data;
using CalderaReport.Domain.DestinyApi;
using CalderaReport.Domain.Enums;
using CalderaReport.Services.Abstract;
using Hangfire;
using Microsoft.EntityFrameworkCore;

namespace CalderaReport.Crawler.BackgroundServices;

public class PlayerCrawler : BackgroundService
{
    private IDbContextFactory<AppDbContext> _contextFactory;
    private readonly ILogger<PlayerCrawler> _logger;
    private readonly ICrawlerService _crawlerService;

    private const int MaxConcurrentPlayers = 25;

    public PlayerCrawler(
        ILogger<PlayerCrawler> logger,
        IDbContextFactory<AppDbContext> contextFactory,
        ICrawlerService crawlerService)
    {
        _logger = logger;
        _contextFactory = contextFactory;
        _crawlerService = crawlerService;
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

                var availableSlots = MaxConcurrentPlayers - activeTasks.Count;
                var batchSize = Math.Min(availableSlots, 10);
                var playerQueueIds = await GetNextPlayerQueueItem(ct, batchSize);
                if (!playerQueueIds.Any())
                {
                    if (activeTasks.Count == 0)
                    {
                        await Task.Delay(TimeSpan.FromMinutes(5), ct);
                    }
                    else
                    {
                        var completedTask = await Task.WhenAny(activeTasks);
                        activeTasks.Remove(completedTask);
                        await completedTask;
                    }
                    continue;
                }

                foreach (var playerId in playerQueueIds)
                {
                    var task = ProcessPlayer(playerId);
                    activeTasks.Add(task);
                }
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

    private async Task<IEnumerable<long>> GetNextPlayerQueueItem(CancellationToken ct, int batchSize)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync(ct);
            var processingStatus = (int)PlayerQueueStatus.Processing;
            var queuedStatus = (int)PlayerQueueStatus.Queued;
            var errorStatus = (int)PlayerQueueStatus.Error;
            var maxAttempts = 3;

            var playerValues = await context.Database.SqlQuery<long>($@"
                    UPDATE ""PlayerCrawlQueue""
                    SET ""Status"" = {processingStatus}, ""Attempts"" = ""Attempts"" + 1
                    WHERE ""Id"" IN (
                        SELECT ""Id""
                        FROM ""PlayerCrawlQueue""
                        WHERE ""Status"" IN ({queuedStatus}, {errorStatus})
                            AND ""Attempts"" < {maxAttempts}
                        ORDER BY ""Id""
                        FOR UPDATE SKIP LOCKED
                        LIMIT {batchSize}
                    )
                    RETURNING ""PlayerId""").ToListAsync(ct);

            return playerValues ?? new List<long>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching next player queue item.");
            return new List<long>();
        }
    }

    private async Task ProcessPlayer(long playerId)
    {
        try
        {
            var addedReports = await _crawlerService.CrawlPlayer(playerId);

            BackgroundJob.Enqueue<ILeaderboardService>("leaderboards-crawler", s => s.CheckAndComputeLeaderboards(playerId, addedReports));

            await using (var context = await _contextFactory.CreateDbContextAsync())
            {
                var queueItem = await context.PlayerCrawlQueue.FirstOrDefaultAsync(p => p.PlayerId == playerId);
                if (queueItem == null)
                {
                    _logger.LogWarning("Player queue item not found for PlayerId: {PlayerId}", playerId);
                    return;
                }
                queueItem.Status = PlayerQueueStatus.Completed;
                queueItem.ProcessedAt = DateTime.UtcNow;
                await context.SaveChangesAsync();
            }
        }
        catch (DestinyApiException ex) when (Enum.TryParse(ex.ErrorCode.ToString(), out BungieErrorCodes result) && result == BungieErrorCodes.PrivateAccount)
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            try
            {
                var queueItem = await context.PlayerCrawlQueue.FirstOrDefaultAsync(p => p.PlayerId == playerId);
                if (queueItem != null)
                {
                    queueItem.Status = PlayerQueueStatus.Completed;
                    await context.SaveChangesAsync();
                }
            }
            catch (Exception innerEx)
            {
                _logger.LogError(innerEx, "Error updating player queue status to Error for player {PlayerId}.", playerId);
            }
        }
        catch (DestinyApiException ex) when (Enum.TryParse(ex.ErrorCode.ToString(), out BungieErrorCodes result) && result == BungieErrorCodes.AccountNotFound)
        {
            //swallow, it's already handled at the service level
        }
        catch (Exception ex)
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            _logger.LogError(ex, "Error processing player {PlayerId}.", playerId);
            try
            {
                var queueItem = await context.PlayerCrawlQueue.FirstOrDefaultAsync(p => p.PlayerId == playerId);
                if (queueItem != null)
                {
                    queueItem.Status = PlayerQueueStatus.Error;
                    await context.SaveChangesAsync();
                }
            }
            catch (Exception innerEx)
            {
                _logger.LogError(innerEx, "Error updating player queue status to Error for player {PlayerId}.", playerId);
            }
        }
    }
}
