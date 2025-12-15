using CalderaReport.Functions.Services.Abstract;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace CalderaReport.Functions.Services
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




    }
}
