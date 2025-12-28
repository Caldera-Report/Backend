using API.Models.Responses;
using CalderaReport.Domain.Data;
using CalderaReport.Domain.DB;
using CalderaReport.Domain.DTO.Responses;
using CalderaReport.Domain.Enums;
using CalderaReport.Services.Abstract;
using Facet.Extensions.EFCore;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using System.Globalization;
using System.Text.Json;

namespace CalderaReport.Services;

public class LeaderboardService : ILeaderboardService
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly IDatabase _redis;
    private readonly SemaphoreSlim _refreshCallToArmsSemaphore = new SemaphoreSlim(1);

    public LeaderboardService(IDbContextFactory<AppDbContext> contextFactory, IConnectionMultiplexer redis)
    {
        _contextFactory = contextFactory;
        _redis = redis.GetDatabase();
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

    public async Task ComputeLeaderboardsForPlayer(Player player)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var activityReports = await context.ActivityReportPlayers
            .AsNoTracking()
            .Where(arp => arp.PlayerId == player.Id)
            .GroupBy(arp => arp.ActivityId)
            .ToDictionaryAsync(
                arpd => arpd.Key,
                arpd => arpd.ToList());
        foreach (var activityReport in activityReports)
        {
            foreach (var leaderboardType in Enum.GetValues<LeaderboardTypes>())
            {
                if (leaderboardType == LeaderboardTypes.CallToArms)
                {
                    continue;
                }
                var leaderboardEntry = await context.PlayerLeaderboards.FirstOrDefaultAsync(pl => pl.PlayerId == player.Id && pl.ActivityId == activityReport.Key && pl.LeaderboardType == leaderboardType);
                if (leaderboardEntry != null)
                {
                    leaderboardEntry.Data = CalculateData(activityReport.Value, leaderboardType);
                }
                else
                {
                    var newLeaderboardEntry = new PlayerLeaderboard()
                    {
                        ActivityId = activityReport.Key,
                        PlayerId = player.Id,
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

    public async Task<bool> ShouldComputeCallToArmsLeaderboards(Player player)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        if (player.NeedsFullCheck)
        {
            return true;
        }

        var events = await GetCallToArmsActivities();
        var now = DateTime.UtcNow;
        if (events.Any(e => e.StartDate < now && (e.EndDate == null || now < e.EndDate)))
        {
            return true;
        }


        var hasLeaderboardEntries = await context.PlayerLeaderboards
            .AnyAsync(pl => pl.PlayerId == player.Id && pl.LeaderboardType == LeaderboardTypes.CallToArms);
        var hasActivityReports = await context.ActivityReportPlayers
                    .Where(arp => arp.PlayerId == player.Id && arp.Completed)
                    .Join(
                        context.ActivityReports,
                        arp => arp.ActivityReportId,
                        ar => ar.Id,
                        (arp, ar) => new { arp, ar })
                    .Join(
                        context.CallToArmsActivities,
                        x => x.arp.ActivityId,
                        ctaa => ctaa.ActivityId,
                        (x, ctaa) => new { x.arp, x.ar, ctaa })
                    .Join(
                        context.CallToArmsEvents,
                        x => x.ctaa.EventId,
                        e => e.Id,
                        (x, e) => new { x.arp, x.ar, e })
                    .AnyAsync(x =>
                        x.e.StartDate < x.ar.Date &&
                        (x.e.EndDate == null || x.ar.Date < x.e.EndDate));


        return !hasLeaderboardEntries && hasActivityReports;
    }

    public async Task ComputeCallToArmsLeaderboards(Player player)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var events = await GetCallToArmsActivities();

        foreach (var callToArmsEvent in events)
        {
            var activityIds = callToArmsEvent.CallToArmsActivities!
                .Select(a => a.ActivityId)
                .ToList();

            var maxes = await context.ActivityReportPlayers
                .Include(arp => arp.ActivityReport)
                .Where(arp =>
                    arp.PlayerId == player.Id
                    && callToArmsEvent.StartDate < arp.ActivityReport.Date
                    && arp.ActivityReport.Date < (callToArmsEvent.EndDate ?? DateTime.MaxValue)
                    && arp.Completed
                    && activityIds.Contains(arp.ActivityId))
                .GroupBy(arp => arp.ActivityId)
                .Select(g => new
                {
                    ActivityId = g.Key,
                    MaxScore = g.Max(arp => arp.Score)
                })
                .ToListAsync();

            var existingLeaderboardEntries = await context.PlayerLeaderboards
                .Where(pl =>
                    pl.PlayerId == player.Id
                    && pl.LeaderboardType == LeaderboardTypes.CallToArms
                    && maxes.Select(m => m.ActivityId).Contains(pl.ActivityId))
                .ToListAsync();
            foreach (var max in maxes)
            {
                var leaderboardEntry = existingLeaderboardEntries
                    .FirstOrDefault(pl => pl.ActivityId == max.ActivityId);
                if (leaderboardEntry != null)
                {
                    leaderboardEntry.Data = max.MaxScore;
                }
                else
                {
                    var newLeaderboardEntry = new PlayerLeaderboard()
                    {
                        ActivityId = max.ActivityId,
                        PlayerId = player.Id,
                        LeaderboardType = LeaderboardTypes.CallToArms,
                        Data = max.MaxScore
                    };
                    context.PlayerLeaderboards.Add(newLeaderboardEntry);
                }
            }
            await context.SaveChangesAsync();
        }
    }

    private async Task<IEnumerable<CallToArmsEventDto>> GetCallToArmsActivities()
    {
        var result = await _redis.SetScanAsync("calltoarms:events").ToListAsync();

        if (result.Count == 0)
        {
            try
            {
                await _refreshCallToArmsSemaphore.WaitAsync();

                result = await _redis.SetScanAsync("calltoarms:events").ToListAsync();
                if (result.Count == 0)
                {
                    await using var context = await _contextFactory.CreateDbContextAsync();
                    var events = await context.CallToArmsEvents.Include(ctae => ctae.CallToArmsActivities).ToFacetsAsync<CallToArmsEventDto>();
                    await _redis.SetAddAsync("calltoarms:events", events.Select(e => (RedisValue)JsonSerializer.Serialize(e)).ToArray());
                    await _redis.KeyExpireAsync("calltoarms:events", TimeSpan.FromMinutes(15));
                    return events;
                }
                else
                {
                    return result.Select(r => JsonSerializer.Deserialize<CallToArmsEventDto>(r.ToString())
                        ?? throw new InvalidDataException("Unable to deserialize call to arms data")).ToList();
                }
            }
            finally
            {
                _refreshCallToArmsSemaphore.Release();
            }
        }
        else
        {
            return result.Select(r => JsonSerializer.Deserialize<CallToArmsEventDto>(r.ToString())
            ?? throw new InvalidDataException("Unable to deserialize call to arms data")).ToList();
        }
    }

    public async Task CheckAndComputeLeaderboards(long playerId, bool addedActivities)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var player = await context.Players.FirstOrDefaultAsync(p => p.Id == playerId)
            ?? throw new InvalidDataException($"Player {playerId} does not exist");

        if (!addedActivities && !player.NeedsFullCheck)
        {
            return;
        }

        await ComputeLeaderboardsForPlayer(player);
        if (await ShouldComputeCallToArmsLeaderboards(player))
        {
            await ComputeCallToArmsLeaderboards(player);
        }
    }
}