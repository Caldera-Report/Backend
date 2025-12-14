using CalderaReport.Domain.Enums;

namespace CalderaReport.Domain.DB
{
    public class PlayerCrawlQueue
    {
        public Guid Id { get; set; }
        public long PlayerId { get; set; }
        public DateTime EnqueuedAt { get; set; } = DateTime.UtcNow;
        public DateTime? ProcessedAt { get; set; }
        public PlayerQueueStatus Status { get; set; } = PlayerQueueStatus.Queued;
        public int Attempts { get; set; } = 0;

        public PlayerCrawlQueue() { }
        public PlayerCrawlQueue(long id)
        {
            Id = Guid.NewGuid();
            PlayerId = id;
        }
    }
}
