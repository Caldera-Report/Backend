using API.Clients.Abstract;
using Domain.Data;
using Domain.DB;
using Domain.DestinyApi;
using Domain.DTO;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Crawler.Services
{
    public class PlayerCrawler : BackgroundService
    {
        private IDbContextFactory<AppDbContext> _contextFactory;
        private readonly IDestiny2ApiClient _client;
        private readonly ChannelWriter<CharacterWorkItem> _output;
        private readonly ILogger<PlayerCrawler> _logger;
        private readonly ConcurrentDictionary<long, int> _playerCharacterWorkCount;

        private const int MaxConcurrentPlayers = 20;
        private static readonly DateTime ActivityCutoffUtc = new DateTime(2025, 7, 15);

        public PlayerCrawler(
            IDestiny2ApiClient client,
            ChannelWriter<CharacterWorkItem> output,
            ILogger<PlayerCrawler> logger,
            IDbContextFactory<AppDbContext> contextFactory,
            ConcurrentDictionary<long, int> playerCharacterWorkCount)
        {
            _client = client;
            _output = output;
            _logger = logger;
            _contextFactory = contextFactory;
            _playerCharacterWorkCount = playerCharacterWorkCount;
        }

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            _logger.LogInformation("Player crawler started processing queue with {MaxConcurrency} concurrent workers.", MaxConcurrentPlayers);
            var activeTasks = new List<Task>();

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    while (activeTasks.Count >= MaxConcurrentPlayers)
                    {
                        var completedTask = await Task.WhenAny(activeTasks);
                        activeTasks.Remove(completedTask);
                        await completedTask;
                    }

                    var playerQueueId = await GetNextPlayerQueueItem(ct);
                    if (playerQueueId == null)
                    {
                        if (activeTasks.Count == 0)
                        {
                            await Task.Delay(1000, ct);
                        }
                        else
                        {
                            var completedTask = await Task.WhenAny(activeTasks);
                            activeTasks.Remove(completedTask);
                            await completedTask;
                        }
                        continue;
                    }

                    activeTasks.Add(ProcessPlayerAsync(playerQueueId, ct));
                }

                await Task.WhenAll(activeTasks);
                _logger.LogInformation("Player crawler completed.");
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Player crawler cancellation requested.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in player crawler loop.");
            }
        }

        private async Task<long?> GetNextPlayerQueueItem(CancellationToken ct)
        {
            try
            {
                await using var context = await _contextFactory.CreateDbContextAsync(ct);
                var processingStatus = (int)PlayerQueueStatus.Processing;
                var queuedStatus = (int)PlayerQueueStatus.Queued;
                var errorStatus = (int)PlayerQueueStatus.Error;
                var maxAttempts = 3;

                var playerValues = await context.Database.SqlQuery<PlayerCrawlQueue>($@"
                    UPDATE ""PlayerCrawlQueue""
                    SET ""Status"" = {processingStatus}, ""Attempts"" = ""Attempts"" + 1
                    WHERE ""Id"" = (
                        SELECT ""Id""
                        FROM ""PlayerCrawlQueue""
                        WHERE ""Status"" IN ({queuedStatus}, {errorStatus})
                            AND ""Attempts"" < {maxAttempts}
                        ORDER BY ""Id""
                        FOR UPDATE SKIP LOCKED
                        LIMIT 1
                    )
                    RETURNING *").ToListAsync(ct);
                var playerValue = playerValues.FirstOrDefault();

                return playerValue?.PlayerId ?? null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching next player queue item.");
                return null;
            }
        }

        private async Task ProcessPlayerAsync(long playerId, CancellationToken ct)
        {
            try
            {
                await using var context = await _contextFactory.CreateDbContextAsync(ct);
                var playerValue = await context.PlayerCrawlQueue.FirstOrDefaultAsync(p => p.PlayerId == playerId, ct);
                if (playerValue is null)
                {
                    _logger.LogWarning("Player queue item for PlayerId {PlayerId} not found; skipping.", playerId);
                    return;
                }
                var player = await context.Players.FirstOrDefaultAsync(p => p.Id == playerValue.PlayerId, ct);
                if (player is null)
                {
                    _logger.LogWarning("Player {PlayerId} not found in database; skipping work item.", playerValue.Id);
                    return;
                }

                var characters = await GetCharactersForPlayer(playerValue.PlayerId, player.MembershipType, context, ct);
                var lastPlayedActivityDate = await context.ActivityReports
                    .AsNoTracking()
                    .Where(r => r.Players.Any(p => p.PlayerId == playerValue.PlayerId) && !r.NeedsFullCheck)
                    .OrderByDescending(r => r.Date)
                    .Select(r => (DateTime?)r.Date)
                    .FirstOrDefaultAsync(ct);

                var queuedCharacters = 0;
                IEnumerable<KeyValuePair<string, DestinyCharacterComponent>> charactersToProcess;
                if (player.LastCrawlStarted == null || player.NeedsFullCheck)
                {
                    charactersToProcess = characters;
                }
                else
                {
                    charactersToProcess = characters.Where(c => c.Value.dateLastPlayed > (player.LastCrawlStarted ?? ActivityCutoffUtc));
                }
                foreach (var character in charactersToProcess)
                {
                    _playerCharacterWorkCount.AddOrUpdate(playerValue.PlayerId, 1, (_, existing) => existing + 1);
                    var workItem = new CharacterWorkItem(playerValue.PlayerId, character.Key, lastPlayedActivityDate ?? ActivityCutoffUtc);
                    await _output.WriteAsync(workItem, ct);
                    queuedCharacters++;
                }

                if (queuedCharacters == 0)
                {
                    player.NeedsFullCheck = false;
                    playerValue.Status = PlayerQueueStatus.Completed;
                    playerValue.ProcessedAt = DateTime.UtcNow;
                    _playerCharacterWorkCount.TryRemove(playerValue.PlayerId, out _);
                    await context.SaveChangesAsync(ct);
                    _logger.LogInformation("No new activities for player {PlayerId}; marked as completed without queueing characters.", playerValue.PlayerId);
                }
                else
                {
                    player.LastCrawlStarted = DateTime.UtcNow;
                    await context.SaveChangesAsync(ct);
                    _logger.LogInformation("Queued {CharacterCount} characters for player {PlayerId}.", queuedCharacters, playerValue.PlayerId);
                }
            }
            catch (DestinyApiException ex) when (ex.ErrorCode == 1601)
            {
                _logger.LogError("Player {PlayerId} does not exist", playerValue.PlayerId);
                try
                {
                    await using var context = await _contextFactory.CreateDbContextAsync(ct);
                    context.PlayerCrawlQueue.Remove(playerValue);
                    var player = await context.Players.FirstOrDefaultAsync(p => p.Id == playerValue.PlayerId, ct);
                    if (player != null)
                    {
                        context.Players.Remove(player);
                    }
                    await context.SaveChangesAsync(ct);
                }
                catch (Exception innerEx)
                {
                    _logger.LogError(innerEx, "Error removing player {PlayerId}", playerValue.PlayerId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing player {PlayerId}.", playerValue.PlayerId);
                try
                {
                    await using var context = await _contextFactory.CreateDbContextAsync(ct);
                    var queueItem = await context.PlayerCrawlQueue.FirstOrDefaultAsync(p => p.Id == playerValue.Id, ct);
                    if (queueItem != null)
                    {
                        queueItem.Status = PlayerQueueStatus.Error;
                        await context.SaveChangesAsync(ct);
                    }
                }
                catch (Exception innerEx)
                {
                    _logger.LogError(innerEx, "Error updating player queue status to Error for player {PlayerId}.", playerValue.PlayerId);
                }
            }
        }

        public async Task<Dictionary<string, DestinyCharacterComponent>> GetCharactersForPlayer(long membershipId, int membershipType, AppDbContext context, CancellationToken ct)
        {
            try
            {
                var characters = await _client.GetCharactersForPlayer(membershipId, membershipType, ct);

                if (characters.ErrorCode == 1665)
                {
                    _logger.LogWarning("Player {PlayerId} has a private profile; skipping.", membershipId);
                    return new Dictionary<string, DestinyCharacterComponent>();
                }

                await CheckPlayerNameAndEmblem(characters.Response, membershipId, context, ct);
                return characters.Response.characters.data;
            }
            catch
            {
                throw;
            }
        }

        public async Task CheckPlayerNameAndEmblem(DestinyProfileResponse profile, long id, AppDbContext context, CancellationToken ct)
        {
            var player = await context.Players.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (player == null)
            {
                _logger.LogWarning("Skipping display name sync; player {PlayerId} not found.", id);
                return;
            }

            if (player.DisplayName != profile.profile.data.userInfo.bungieGlobalDisplayName ||
                player.DisplayNameCode != profile.profile.data.userInfo.bungieGlobalDisplayNameCode)
            {
                player.DisplayName = profile.profile.data.userInfo.bungieGlobalDisplayName;
                player.DisplayNameCode = profile.profile.data.userInfo.bungieGlobalDisplayNameCode;
                player.FullDisplayName = player.DisplayName + "#" + player.DisplayNameCode;
                context.Players.Update(player);
                await context.SaveChangesAsync(ct);
                _logger.LogInformation("Updated display information for player {PlayerId}.", id);
            }

            var lastPlayedCharacter = profile.characters.data.Values
                .OrderByDescending(cid => profile.characters.data[cid.characterId].dateLastPlayed)
                .FirstOrDefault();

            if (lastPlayedCharacter != null)
            {
                if (player.LastPlayedCharacterEmblemPath != lastPlayedCharacter.emblemPath || player.LastPlayedCharacterBackgroundPath != lastPlayedCharacter.emblemBackgroundPath)
                {
                    player.LastPlayedCharacterEmblemPath = lastPlayedCharacter.emblemPath;
                    player.LastPlayedCharacterBackgroundPath = lastPlayedCharacter.emblemBackgroundPath;
                    context.Players.Update(player);
                    await context.SaveChangesAsync(ct);
                    _logger.LogInformation("Updated last played character emblem for player {PlayerId}.", id);
                }
            }
        }
    }
}
