using CalderaReport.Domain.Data;
using CalderaReport.Services.Abstract;
using Microsoft.EntityFrameworkCore;

namespace CalderaReport.Crawler.Services
{
    public class ActivityReportCrawler : BackgroundService
    {
        private IDbContextFactory<AppDbContext> _contextFactory;
        private readonly ILogger<ActivityReportCrawler> _logger;
        private readonly ICrawlerService _crawlerService;

        private const int MaxConcurrentTasks = 150;

        public ActivityReportCrawler(
            ILogger<ActivityReportCrawler> logger,
            IDbContextFactory<AppDbContext> contextFactory,
            ICrawlerService crawlerService)
        {
            _logger = logger;
            _contextFactory = contextFactory;
            _crawlerService = crawlerService;
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

                    await using var context = await _contextFactory.CreateDbContextAsync();
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
                        RETURNING ""Id""").ToListAsync();
                    var activityReportId = activityReportIds.FirstOrDefault();
                    if (activityReportId == 0)
                    {
                        if (activeTasks.Count > 0)
                        {
                            await Task.WhenAll(activeTasks);
                            activeTasks.Clear();
                        }
                        await Task.Delay(1000);
                        continue;
                    }

                    var activityReport = await context.ActivityReports.FirstOrDefaultAsync(ar => ar.Id == activityReportId);
                    if (activityReport == null)
                    {
                        _logger.LogWarning("Activity report {ReportId} not found after claiming.", activityReportId);
                        continue;
                    }

                    activeTasks.Add(ProcessActivityReportAsync(activityReport.Id));
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

        private async Task ProcessActivityReportAsync(long reportId)
        {
            await using var context = await _contextFactory.CreateDbContextAsync();

            try
            {
                await _crawlerService.CrawlActivityReport(reportId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing activity report {ReportId}", reportId);
                var activityReport = await context.ActivityReports.FirstOrDefaultAsync(ar => ar.Id == reportId);
                if (activityReport == null)
                {
                    _logger.LogWarning("Activity report {ReportId} not found in error handler.", reportId);
                    return;
                }
                activityReport.NeedsFullCheck = true;
                await context.SaveChangesAsync();
            }
        }
    }
}
