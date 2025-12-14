using CalderaReport.Domain.DB;
using CalderaReport.Domain.DestinyApi;

namespace CalderaReport.Services.Abstract;

public interface ICrawlerService
{
    public Task<IEnumerable<KeyValuePair<string, DestinyCharacterComponent>>> GetCharactersForCrawl(Player player);
}