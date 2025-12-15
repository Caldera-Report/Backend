using CalderaReport.Domain.DB;
using CalderaReport.Domain.DestinyApi;

namespace CalderaReport.Services.Abstract;

public interface ICrawlerService
{
    public Task<Dictionary<string, DestinyCharacterComponent>> GetCharactersForCrawl(Player player);
    public Task<DateTime> GetLastPlayedActivityDateForPlayer(Player player);
    public Task<IEnumerable<ActivityReport>> CrawlCharacter(Player player, string characterId, DateTime lastPlayedActivityDate);
    public Task LoadCrawler();
}