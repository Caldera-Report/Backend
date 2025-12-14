using CalderaReport.Clients.Abstract;
using CalderaReport.Domain.Data;
using CalderaReport.Domain.DB;
using CalderaReport.Domain.DestinyApi;
using CalderaReport.Domain.Enums;
using CalderaReport.Services.Abstract;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using StackExchange.Redis;
using System;
using System.Numerics;

namespace CalderaReport.Services;

public class CrawlerService : ICrawlerService
{
    private static readonly DateTime ActivityCutoffUtc = new DateTime(2025, 7, 15, 19, 0, 0);
    private readonly IBungieClient _bungieClient;
    private readonly IDatabase _redis;
    private readonly IDbContextFactory<AppDbContext> _contextFactory;

    public CrawlerService(IDbContextFactory<AppDbContext> contextFactory, IBungieClient bungieClient)
    {
        _contextFactory = contextFactory;
        _bungieClient = bungieClient;
    }

    public async Task<IEnumerable<KeyValuePair<string, DestinyCharacterComponent>>> GetCharactersForCrawl(Player player)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var characters = await GetCharactersForPlayer(player);

        if (player.LastCrawlStarted == null || player.NeedsFullCheck)
        {
            return characters;
        }
        else
        {
            return characters.Where(c => c.Value.dateLastPlayed > (player.LastCrawlStarted ?? ActivityCutoffUtc)).ToList();
        }
    }

    private async Task<Dictionary<string, DestinyCharacterComponent>> GetCharactersForPlayer(Player player)
    {
        var characters = await _bungieClient.GetCharactersForPlayer(player.Id, player.MembershipType);

        await CheckPlayerNameAndEmblem(characters.Response, player.Id);
        return characters.Response.characters.data;
    }

    private async Task CheckPlayerNameAndEmblem(DestinyProfileResponse profile, long playerId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var player = await context.Players.FirstOrDefaultAsync(p => p.Id == playerId);
        if (player == null) {
            throw new InvalidOperationException($"Player with ID {playerId} not found.");
        }

        if (player.DisplayName != profile.profile.data.userInfo.bungieGlobalDisplayName ||
            player.DisplayNameCode != profile.profile.data.userInfo.bungieGlobalDisplayNameCode)
        {
            player.DisplayName = profile.profile.data.userInfo.bungieGlobalDisplayName;
            player.DisplayNameCode = profile.profile.data.userInfo.bungieGlobalDisplayNameCode;
            player.FullDisplayName = player.DisplayName + "#" + player.DisplayNameCode;
            context.Players.Update(player);
            await context.SaveChangesAsync();
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
                await context.SaveChangesAsync();
            }
        }
    }

    public async Task<DateTime> GetLastPlayedActivityDateForPlayer(Player player)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var lastPlayedActivityDate = await context.ActivityReports
            .AsNoTracking()
            .Where(r => r.Players.Any(p => p.PlayerId == player.Id) && !r.NeedsFullCheck)
            .OrderByDescending(r => r.Date)
            .Select(r => (DateTime?)r.Date)
            .FirstOrDefaultAsync();
        return lastPlayedActivityDate ?? ActivityCutoffUtc;
    }

    public async Task<IEnumerable<ActivityReport>> CrawlCharacter(Player player, string characterId, DateTime lastPlayedActivityDate)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var page = 0;
        var hasReachedLastUpdate = false;
        var activityCount = 250;
        var existingReportIds = player.NeedsFullCheck ? new HashSet<long>() : await context.ActivityReportPlayers
            .Where(arp => arp.PlayerId == player.Id)
            .Select(arp => arp.ActivityReportId)
            .ToHashSetAsync();

        try
        {
            while (!hasReachedLastUpdate)
            {
                var response = await _bungieClient.GetHistoricalStatsForCharacter(player.Id, player.MembershipType, characterId, page, activityCount);
                if (response.Response?.activities == null || !response.Response.activities.Any())
                    break;
                page++;
                var activityHashMap = await _redis.GetActivityHashMapAsync(_redis);
                foreach (var activityReport in response.Response.activities)
                {
                    hasReachedLastUpdate = activityReport.period <= lastPlayedActivityDate;
                    if (activityReport.period < ActivityCutoffUtc || hasReachedLastUpdate)
                        break;
                    var rawHash = activityReport.activityDetails.referenceId;
                    if (!activityHashMap.TryGetValue(rawHash, out var canonicalId))
                        continue;

                    if (!long.TryParse(activityReport.activityDetails.instanceId, out var instanceId))
                        continue;

                    if (existingReportIds.Contains(instanceId))
                        continue;

                    reportsBag.Add(new ActivityReport
                    {
                        Id = instanceId,
                        ActivityId = canonicalId,
                        Date = activityReport.period,
                        NeedsFullCheck = activityReport.values["playerCount"].basic.value != 1,
                        Players = new List<ActivityReportPlayer>
                            {
                                new ActivityReportPlayer
                                {
                                    PlayerId = player.Id,
                                    ActivityReportId = instanceId,
                                    SessionId = reportsBag.Count(ar => ar.Id == instanceId && ar.Players.Any(arp => arp.PlayerId == player.Id)) + 1,
                                    Score = (int)activityReport.values["score"].basic.value,
                                    ActivityId = canonicalId,
                                    Duration = TimeSpan.FromSeconds(activityReport.values["activityDurationSeconds"].basic.value),
                                    Completed = activityReport.values["completed"].basic.value == 1 && activityReport.values["completionReason"].basic.value != 2.0,
                                }
                            }
                    });

                }
                if (response.Response.activities.Last().period < ActivityCutoffUtc)
                    break;
            }
        }
        catch (DestinyApiException ex) when (ex.ErrorCode == 1665)
        {
            _logger.LogWarning(ex, "Historical stats throttled for player {PlayerId} character {CharacterId}.", player.Id, characterId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching activity reports for player: {PlayerId}, character: {CharacterId}", player.Id, characterId);
            throw;
        }
    }

    public async Task LoadCrawler()
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var isCrawling = await context.PlayerCrawlQueue.AnyAsync(pcq => pcq.Status == PlayerQueueStatus.Queued || pcq.Status == PlayerQueueStatus.Processing);
        if (!isCrawling)
        {
            await context.Database.ExecuteSqlRawAsync(
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
}
