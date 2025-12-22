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

        var conquests = await context.Activities.Where(a => a.OpTypeId == (int)OpTypeEnum.Conquest)
            .ToDictionaryAsync(a => a.Id, a => a);

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

    public async Task ComputeLeaderboardsForPlayer(long playerId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var activityReports = await context.ActivityReportPlayers
            .AsNoTracking()
            .Where(arp => arp.PlayerId == playerId)
            .GroupBy(arp => arp.ActivityId)
            .ToDictionaryAsync(
                arpd => arpd.Key,
                arpd => arpd.ToList());
        foreach (var activityReport in activityReports)
        {
            foreach (var leaderboardType in Enum.GetValues<LeaderboardTypes>())
            {
                var leaderboardEntry = await context.PlayerLeaderboards.FirstOrDefaultAsync(pl => pl.PlayerId == playerId && pl.ActivityId == activityReport.Key && pl.LeaderboardType == leaderboardType);
                if (leaderboardEntry != null)
                {
                    leaderboardEntry.Data = CalculateData(activityReport.Value, leaderboardType);
                }
                else
                {
                    var newLeaderboardEntry = new PlayerLeaderboard()
                    {
                        ActivityId = activityReport.Key,
                        PlayerId = playerId,
                        LeaderboardType = leaderboardType,
                        Data = CalculateData(activityReport.Value, leaderboardType)
                    };
                    if (newLeaderboardEntry.Data == 0)
                    {
                        continue;
                    }
                    context.PlayerLeaderboards.Add(newLeaderboardEntry);
                }
            }
            await context.SaveChangesAsync();
        }
    }

    private static long CalculateData(List<ActivityReportPlayer> reports, LeaderboardTypes leaderboardType)
    {
        if (reports.Count(arp => arp.Completed) == 0)
        {
            return 0;
        }
        switch (leaderboardType)
        {
            case LeaderboardTypes.TotalCompletions:
                return reports.Count(arp => arp.Completed);
            case LeaderboardTypes.FastestCompletion:
                return (long)reports.Where(arp => arp.Completed).Min(arp => arp.Duration).TotalSeconds;
            case LeaderboardTypes.HighestScore:
                return reports.Where((arp) => arp.Completed).Max(arp => arp.Score);
            default: return 0;
        }
    }
}