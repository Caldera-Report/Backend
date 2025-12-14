using API.Models.Responses;
using CalderaReport.Domain.Data;
using CalderaReport.Domain.DB;
using CalderaReport.Domain.DTO.Responses;
using CalderaReport.Domain.Enums;
using CalderaReport.Services.Abstract;
using Microsoft.EntityFrameworkCore;
using System.Globalization;

namespace CalderaReport.Services;

public class LeaderboardService : ILeaderboardService
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;

    public LeaderboardService(IDbContextFactory<AppDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<IEnumerable<LeaderboardResponse>> GetLeaderboard(long activityId, LeaderboardTypes type, int count, int offset)
    {
        await using var context = _contextFactory.CreateDbContext();

        if (!await context.Activities.AnyAsync(a => a.Id == activityId))
        {
            throw new ArgumentException("Activity does not exist");
        }

        var query = context.PlayerLeaderboards
            .AsNoTracking()
            .Include(pl => pl.Player)
            .Where(pl => pl.ActivityId == activityId && pl.LeaderboardType == type);
        var leaderboardQuery = type == LeaderboardTypes.FastestCompletion ? query.OrderBy(pl => pl.Data)
            : query.OrderByDescending(pl => pl.Data);
        var leaderboard = await leaderboardQuery
            .Skip(offset)
            .Take(count)
            .ToListAsync();

        return leaderboard
            .Select((pl, i) => new LeaderboardResponse()
            {
                Player = new PlayerDto(pl.Player),
                Rank = offset + i + 1,
                Data = pl.LeaderboardType == LeaderboardTypes.FastestCompletion ? TimeSpan.FromSeconds(pl.Data).ToString() : pl.Data.ToString("0,0", CultureInfo.InvariantCulture)
            })
            .ToList();
    }

    public async Task<IEnumerable<LeaderboardResponse>> GetLeaderboardsForPlayer(List<long> playerIds, long activityId, LeaderboardTypes type)
    {
        await using var context = _contextFactory.CreateDbContext();

        if (playerIds.Count == 0)
        {
            return new List<LeaderboardResponse>();
        }

        if (!await context.Activities.AnyAsync(a => a.Id == activityId))
        {
            throw new ArgumentException("Activity does not exist");
        }

        var query = context.PlayerLeaderboards
            .AsNoTracking()
            .Include(pl => pl.Player)
            .Where(pl => playerIds.Contains(pl.PlayerId)
                && pl.LeaderboardType == type
                && pl.ActivityId == activityId);

        var leaderboardQuery = type == LeaderboardTypes.FastestCompletion
            ? query.OrderBy(pl => pl.Data)
            : query.OrderByDescending(pl => pl.Data);

        var leaderboards = await leaderboardQuery
            .Take(250)
            .ToListAsync();

        var leaderboardTasks = leaderboards.Select(async pl =>
        {
            var rank = await ComputePlayerScore(pl);
            return new LeaderboardResponse
            {
                Player = new PlayerDto(pl.Player),
                Rank = rank,
                Data = pl.LeaderboardType == LeaderboardTypes.FastestCompletion
                    ? TimeSpan.FromSeconds(pl.Data).ToString()
                    : pl.Data.ToString("0,0", CultureInfo.InvariantCulture)
            };
        }).ToList();

        var leaderboardResponses = await Task.WhenAll(leaderboardTasks);
        return leaderboardResponses.ToList();
    }

    private async Task<int> ComputePlayerScore(PlayerLeaderboard playerLeaderboard)
    {
        await using var context = _contextFactory.CreateDbContext();
        var query = context.PlayerLeaderboards
            .Where(pl => pl.ActivityId == playerLeaderboard.ActivityId
                    && pl.LeaderboardType == playerLeaderboard.LeaderboardType);
        var higherCount = playerLeaderboard.LeaderboardType == LeaderboardTypes.FastestCompletion ? await query.Where(pl => pl.Data < playerLeaderboard.Data).CountAsync()
            : await query.Where(pl => pl.Data > playerLeaderboard.Data).CountAsync();
        return higherCount + 1;
    }
}