namespace Crawler.Frontend.Models;

public sealed record CrawlerStatusSnapshot(
    string Status,
    long TotalPlayers,
    long QueuedPlayers,
    long ProcessingPlayers,
    long CompletedPlayers,
    long ErrorPlayers,
    DateTime? OldestQueuedAt,
    DateTime? LastProcessedAt,
    DateTime? LastRunStartedAt,
    DateTime? LastRunFinishedAt,
    long TotalActivityReports,
    long PendingActivityReports)
{
    public static CrawlerStatusSnapshot Empty { get; } = new(
        Status: "Idle",
        TotalPlayers: 0,
        QueuedPlayers: 0,
        ProcessingPlayers: 0,
        CompletedPlayers: 0,
        ErrorPlayers: 0,
        OldestQueuedAt: null,
        LastProcessedAt: null,
        LastRunStartedAt: null,
        LastRunFinishedAt: null,
        TotalActivityReports: 0,
        PendingActivityReports: 0);

    public double CompletionPercent => TotalPlayers <= 0
        ? 0
        : Math.Clamp(Math.Round((double)CompletedPlayers / TotalPlayers * 100, 1), 0, 100);

    public long PlayersRemaining => Math.Max(0, TotalPlayers - CompletedPlayers);

    public double ActivityReportCompletionPercent => TotalActivityReports <= 0
        ? 0
        : Math.Clamp(Math.Round((double)(TotalActivityReports - PendingActivityReports) / TotalActivityReports * 100, 1), 0, 100);

    public long CompletedActivityReports => Math.Max(0, TotalActivityReports - PendingActivityReports);
}
