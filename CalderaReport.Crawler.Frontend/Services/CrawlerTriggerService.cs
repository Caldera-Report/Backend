namespace CalderaReport.Crawler.Frontend.Services;

public class CrawlerTriggerService : ICrawlerTriggerService
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;

    public CrawlerTriggerService(IDbContextFactory<AppDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task TriggerAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        if (await context.PlayerCrawlQueue.AnyAsync(pcq => pcq.Status == PlayerQueueStatus.Queued || pcq.Status == PlayerQueueStatus.Processing, cancellationToken))
        {
            return;
        }
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
