using Domain.Enums;

namespace Domain.DB
{
    public class PlayerLeaderboard
    {
        public long PlayerId { get; set; }
        public long ActivityId { get; set; }
        public LeaderboardTypes LeaderboardType { get; set; }
        public long Data { get; set; }
        public Player Player { get; set; }
    }
}
