using CalderaReport.Domain.DTO.Responses;
using CalderaReport.Domain.Enums;

namespace CalderaReport.Services.Abstract;

public interface ILeaderboardService
{
    public Task<IEnumerable<LeaderboardResponse>> GetLeaderboard(long activityId, LeaderboardTypes type, int count, int offset);
    public Task<IEnumerable<LeaderboardResponse>> GetLeaderboardsForPlayer(List<long> playerIds, long activityId, LeaderboardTypes type);
}
