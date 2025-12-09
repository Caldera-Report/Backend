using API.Domain.DTO.Responses;
using API.Models.Responses;
using API.Services.Abstract;
using Domain.Data;
using Domain.DB;
using Domain.DTO.Responses;
using Domain.Enums;
using Facet.Extensions;
using Facet.Extensions.EFCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System.Globalization;
using System.Text.Json;

namespace API.Services
{
    public class QueryService : IQueryService
    {
        private readonly AppDbContext _context;
        private readonly IDatabase _cache;
        private readonly ILogger<QueryService> _logger;
        private readonly IDbContextFactory<AppDbContext> _contextFactory;

        public QueryService(AppDbContext context, IConnectionMultiplexer redis, ILogger<QueryService> logger, IDbContextFactory<AppDbContext> contextFactory)
        {
            _context = context;
            _cache = redis.GetDatabase();
            _logger = logger;
            _contextFactory = contextFactory;
        }

        public async Task<List<OpTypeDto>> GetAllActivitiesAsync()
        {
            try
            {
                var activities = await _cache.StringGetAsync("activities:all");
                if (activities.HasValue)
                {
                    return JsonSerializer.Deserialize<List<OpTypeDto>>(activities.ToString())
                        ?? new List<OpTypeDto>();
                }
                else
                {
                    await CacheAllActivitiesAsync();
                    activities = await _cache.StringGetAsync("activities:all");
                    if (activities.HasValue)
                    {
                        return JsonSerializer.Deserialize<List<OpTypeDto>>(activities.ToString())
                            ?? new List<OpTypeDto>();
                    }
                    else
                    {
                        return new List<OpTypeDto>();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving activities from database");
                throw;
            }
        }

        public async Task CacheAllActivitiesAsync()
        {
            try
            {
                var activities = await _context.OpTypes
                    .Include(o => o.Activities.Where(a => a.Enabled))
                    .Where(o => o.Activities.Any(a => a.Enabled))
                    .ToListAsync();
                await _cache.StringSetAsync("activities:all", JsonSerializer.SerializeToUtf8Bytes(activities.Select(a => a.ToFacet<OpTypeDto>()).ToList()), new TimeSpan(1, 1, 0, 0));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error caching activities");
                throw;
            }
        }

        public async Task<PlayerDto> GetPlayerAsync(long id)
        {
            try
            {
                var player = await _context.Players
                    .Where(p => p.Id == id)
                    .FirstFacetAsync<PlayerDto>();
                if (player is null)
                    throw new Exception("Player not found");
                return player;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error retrieving player {id} from database");
                throw;
            }
        }

        public async Task<Player> GetPlayerDbObject(long id)
        {
            try
            {
                var player = await _context.Players
                    .FirstOrDefaultAsync(p => p.Id == id);
                if (player is null)
                    throw new ArgumentException("Player not found");
                return player;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error retrieving player {id} from database");
                throw;
            }
        }

        public async Task<ActivityReportListDto> GetPlayerReportsForActivityAsync(long playerId, long activityId)
        {
            try
            {
                var reports = await _context.ActivityReportPlayers
                    .Where(arp => arp.ActivityId == activityId && arp.PlayerId == playerId)
                    .ToFacetsAsync<ActivityReportPlayerDto>();
                var averageMs = reports.Count(r => r.Completed) > 0 ? reports.Where(r => r.Completed).Select(r => r.Duration.TotalMilliseconds).Average() : 0;
                var average = TimeSpan.FromMilliseconds(averageMs);
                var fastest = reports.OrderBy(r => r.Duration).FirstOrDefault(r => r.Completed);
                var recent = reports.OrderByDescending(r => r.Date).FirstOrDefault();
                return new ActivityReportListDto
                {
                    Reports = reports.OrderBy(arpd => arpd.Date).ToList(),
                    Average = average,
                    Best = fastest,
                    Recent = recent,
                    CountCompleted = reports.Count(r => r.Completed)
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error retrieving activity reports for player {playerId} and activity {activityId}");
                throw;
            }
        }

        public async Task<List<LeaderboardResponse>> GetLeaderboardAsync(long activityId, LeaderboardTypes type, int count, int offset)
        {
            try
            {
                var query = _context.PlayerLeaderboards
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
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving completions leaderboard for activity {ActivityId}", activityId);
                throw;
            }
        }

        public async Task UpdatePlayerEmblems(Player player, string backgroundEmblemPath, string emblemPath)
        {
            try
            {
                player.LastPlayedCharacterEmblemPath = emblemPath;
                player.LastPlayedCharacterBackgroundPath = backgroundEmblemPath;
                _context.Players.Update(player);
                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error updating emblem for player {player.Id}");
                throw;
            }
        }

        public async Task<DateTime> GetPlayerLastPlayedActivityDate(long membershipId)
        {
            var lastActivity = await _context.ActivityReports
                .AsNoTracking()
                .Include(r => r.Players)
                .Where(r => r.Players.Any(p => p.PlayerId == membershipId) && !r.NeedsFullCheck)
                .OrderByDescending(r => r.Date)
                .Select(r => (DateTime?)r.Date)
                .FirstOrDefaultAsync();
            return lastActivity ?? new DateTime(2025, 7, 15);
        }

        public async Task<List<PlayerSearchDto>> SearchForPlayer(string query)
        {
            try
            {
                var results = await _context.Players
                    .Where(p => EF.Functions.ILike(p.FullDisplayName, $"%{query}%"))
                    .OrderBy(p => p.FullDisplayName)
                    .Take(25)
                    .ToFacetsAsync<PlayerSearchDto>();
                return results;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error searching for player with query {Query}", query);
                throw;
            }
        }

        public async Task<List<LeaderboardResponse>> GetLeaderboardsForPlayer(string playerName, long activityId, LeaderboardTypes type)
        {
            try
            {
                var playerIds = await _context.Players
                    .Where(p => EF.Functions.ILike(p.FullDisplayName, $"%{playerName}%"))
                    .Select(p => p.Id)
                    .ToListAsync();

                if (playerIds.Count == 0)
                {
                    return new List<LeaderboardResponse>();
                }

                var query = _context.PlayerLeaderboards
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
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving leaderboards for player {PlayerName}", playerName);
                throw;
            }
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

        public async Task LoadCrawler()
        {
            try
            {
                var isCrawling = await _context.PlayerCrawlQueue.AnyAsync(pcq => pcq.Status == PlayerQueueStatus.Queued || pcq.Status == PlayerQueueStatus.Processing);
                if (!isCrawling)
                {
                    await _context.Database.ExecuteSqlRawAsync(
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
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading crawler queue");
                throw;
            }
        }
    }
}
