using StackExchange.Redis;

namespace Crawler.Frontend.Services;

public class CrawlerTriggerService : ICrawlerTriggerService
{
    private readonly RedisChannel ChannelName = new("crawler:pipeline:run", RedisChannel.PatternMode.Auto);
    private readonly IConnectionMultiplexer _redis;

    public CrawlerTriggerService(IConnectionMultiplexer redis)
    {
        _redis = redis;
    }

    public async Task TriggerAsync(CancellationToken cancellationToken = default)
    {
        var sub = _redis.GetSubscriber();
        await sub.PublishAsync(ChannelName, "manual-trigger");
    }
}
