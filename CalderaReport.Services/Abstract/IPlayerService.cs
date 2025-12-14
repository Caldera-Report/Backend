using API.Domain.DTO.Responses;
using CalderaReport.Domain.DB;
using CalderaReport.Domain.DestinyApi;

namespace CalderaReport.Services.Abstract;

public interface IPlayerService
{
    public Task<Player> GetPlayer(long id);
    public Task<IEnumerable<Player>> SearchForPlayer(string playerName);
    public Task<IEnumerable<Player>> SearchDbForPlayer(string playerName);
    public Task<Dictionary<string, DestinyCharacterComponent>> GetCharactersForPlayer(long membershipId, int membershipType);
    public Task<IEnumerable<ActivityReportPlayerDto>> GetPlayerReportsForActivityAsync(long playerId, long activityId);
}

