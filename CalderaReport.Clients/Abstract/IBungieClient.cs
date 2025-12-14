using CalderaReport.Domain.DestinyApi;
using CalderaReport.Domain.Manifest;

namespace CalderaReport.Clients.Abstract
{
    public interface IBungieClient
    {
        public Task<DestinyApiResponse<DestinyProfileResponse>> GetCharactersForPlayer(long membershipId, int membershipType);
        public Task<DestinyApiResponse<DestinyActivityHistoryResults>> GetHistoricalStatsForCharacter(long membershipId, int membershipType, string characterId, int page, int activityCount);
        public Task<DestinyApiResponse<PostGameCarnageReportData>> GetPostGameCarnageReport(long activityId);
        public Task<DestinyApiResponse<UserSearchPrefixResponse>> PerformSearchByPrefix(UserSearchPrefixRequest name, int page);
        public Task<DestinyApiResponse<List<UserInfoCard>>> PerformSearchByBungieName(ExactSearchRequest player, int membershipTypeId);
        public Task<DestinyApiResponse<Manifest>> GetManifest();
        public Task<Dictionary<string, DestinyActivityDefinition>> GetActivityDefinitions(string url);
    }
}
