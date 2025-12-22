namespace CalderaReport.Services.Abstract;

public interface ICrawlerService
{
    public Task<bool> CrawlPlayer(long playerId);
    public Task CrawlActivityReport(long activityReportId);
    public Task LoadCrawler();
}