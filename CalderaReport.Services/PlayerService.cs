using API.Domain.DTO.Responses;
using CalderaReport.Clients.Abstract;
using CalderaReport.Domain.Data;
using CalderaReport.Domain.DB;
using CalderaReport.Domain.DestinyApi;
using CalderaReport.Services.Abstract;
using Facet.Extensions.EFCore;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace CalderaReport.Services;

public class PlayerService : IPlayerService
{
    private readonly IBungieClient _client;
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly IDatabase _cache;

    public PlayerService(IBungieClient client, IDbContextFactory<AppDbContext> contextFactory, IConnectionMultiplexer redis)
    {
        _client = client;
        _contextFactory = contextFactory;
        _cache = redis.GetDatabase();
    }

    public async Task<Player?> GetPlayer(long? id)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var player = await context.Players
            .FirstOrDefaultAsync(p => p.Id == id);
        return player ?? null;
    }

    public async Task<IEnumerable<Player>> SearchForPlayer(string playerName)
    {
        var hasBungieId = playerName.Length > 5 && playerName[^5] == '#';

        var response = hasBungieId ?
            await SearchByBungieName(playerName) :
            await SearchByPrefix(playerName);

        var filteredMemberships = response
                .Where(m => m.applicableMembershipTypes.Count > 0)
                .DistinctBy(r => r.membershipId)
                .Select(uic => new Player
                {
                    Id = long.Parse(uic.membershipId),
                    MembershipType = uic.membershipType,
                    DisplayName = uic.bungieGlobalDisplayName,
                    DisplayNameCode = uic.bungieGlobalDisplayNameCode,
                    FullDisplayName = $"{uic.bungieGlobalDisplayName}#{uic.bungieGlobalDisplayNameCode:0000}"
                })
                .ToList();

        await AddSearchResultsToDb(filteredMemberships);

        return filteredMemberships;
    }

    private async Task AddSearchResultsToDb(List<Player> result)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        if (result is null || result.Count == 0)
            return;

        var distinct = result
            .GroupBy(r => (r.Id, r.MembershipType))
            .Select(g => g.First())
            .ToList();

        var membershipIds = distinct.Select(r => r.Id).Distinct().ToList();
        var membershipTypes = distinct.Select(r => r.MembershipType).Distinct().ToList();

        var existingKeys = await context.Players
            .AsNoTracking()
            .Where(p => membershipIds.Contains(p.Id) && membershipTypes.Contains(p.MembershipType))
            .Select(p => new ValueTuple<long, int>(p.Id, p.MembershipType))
            .ToListAsync();

        var existingSet = existingKeys.ToHashSet();

        var newPlayers = new List<Player>(distinct.Count);

        foreach (var membership in distinct)
        {
            var key = (membership.Id, membership.MembershipType);
            if (existingSet.Contains(key))
                continue;

            newPlayers.Add(membership);
        }

        if (newPlayers.Count > 0)
        {
            context.Players.AddRange(newPlayers);
            await context.SaveChangesAsync();
        }
    }

    private async Task<IEnumerable<UserInfoCard>> SearchByBungieName(string playerName)
    {
        var bungieId = int.Parse(playerName[^4..]);
        playerName = playerName[..^5];

        var player = new ExactSearchRequest
        {
            displayName = playerName,
            displayNameCode = bungieId
        };

        var response = await _client.PerformSearchByBungieName(player, -1);

        var results = response.Response;

        return results;
    }

    private async Task<IEnumerable<UserInfoCard>> SearchByPrefix(string playerName)
    {
        var page = 0;
        var player = new UserSearchPrefixRequest
        {
            displayNamePrefix = playerName
        };
        var response = await _client.PerformSearchByPrefix(player, page);
        var hasMore = response.Response.hasMore;
        while (hasMore)
        {
            page++;
            var nextResponse = await _client.PerformSearchByPrefix(player, page);
            response.Response.searchResults.AddRange(nextResponse.Response.searchResults);
            hasMore = nextResponse.Response.hasMore;
        }

        return response.Response.searchResults.SelectMany(r => r.destinyMemberships);
    }

    public async Task<IEnumerable<Player>> SearchDbForPlayer(string query)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var results = await context.Players
            .Where(p => EF.Functions.ILike(p.FullDisplayName, $"%{query}%"))
            .OrderBy(p => p.FullDisplayName)
            .Take(25)
            .ToListAsync();
        return results;
    }

    public async Task<Dictionary<string, DestinyCharacterComponent>> GetCharactersForPlayer(long membershipId, int membershipType)
    {
        var characters = await _client.GetCharactersForPlayer(membershipId, membershipType);
        if (characters.ErrorCode == 1665)
        {
            // user is private
            return new Dictionary<string, DestinyCharacterComponent>();
        }
        await CheckPlayerName(characters.Response, membershipId);
        return characters.Response.characters.data;
    }
    public async Task CheckPlayerName(DestinyProfileResponse profile, long id)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var player = await context.Players.FirstOrDefaultAsync(p => p.Id == id);
        if (player == null)
            return;
        if (player.DisplayName != profile.profile.data.userInfo.bungieGlobalDisplayName ||
            player.DisplayNameCode != profile.profile.data.userInfo.bungieGlobalDisplayNameCode)
        {
            player.DisplayName = profile.profile.data.userInfo.bungieGlobalDisplayName;
            player.DisplayNameCode = profile.profile.data.userInfo.bungieGlobalDisplayNameCode;
            player.FullDisplayName = $"{player.DisplayName}#{player.DisplayNameCode:0000}";
            context.Players.Update(player);
            await context.SaveChangesAsync();
        }
    }

    public async Task<IEnumerable<ActivityReportPlayerDto>> GetPlayerReportsForActivityAsync(long playerId, long activityId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var reports = await context.ActivityReportPlayers
            .Where(arp => arp.ActivityId == activityId && arp.PlayerId == playerId)
            .ToFacetsAsync<ActivityReportPlayerDto>();

        return reports;
    }
}
