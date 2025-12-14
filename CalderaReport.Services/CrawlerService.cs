using CalderaReport.Domain.Data;
using CalderaReport.Domain.Enums;
using CalderaReport.Services.Abstract;
using Microsoft.EntityFrameworkCore;

namespace CalderaReport.Services;

public class CrawlerService : ICrawlerService
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;

    public CrawlerService(IDbContextFactory<AppDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
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
