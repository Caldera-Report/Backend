using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Collections.Concurrent;

namespace CalderaReport.Crawler.Tests;

public class CharacterCrawlerTests
{
    [Fact]
    public async Task GetCharacterActivityReports_FiltersByCutoffAndMapsInstanceIds()
    {
        var databaseMock = new Mock<IDatabase>();
        databaseMock
            .Setup(db => db.HashGetAllAsync("activityHashMappings", Moq.It.IsAny<CommandFlags>()))
            .ReturnsAsync(new[]
            {
                new HashEntry("100", "200")
            });

        var multiplexerMock = new Mock<IConnectionMultiplexer>();
        multiplexerMock
            .Setup(m => m.GetDatabase(Moq.It.IsAny<int>(), Moq.It.IsAny<object>()))
            .Returns(databaseMock.Object);

        var cache = new MemoryCache(new MemoryCacheOptions());

        var clientMock = new Mock<IDestiny2ApiClient>();
        clientMock.Setup(client => client.GetHistoricalStatsForCharacter(1, 2, "character-1", 0, 250, Moq.It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DestinyApiResponse<DestinyActivityHistoryResults>
            {
                Response = new DestinyActivityHistoryResults
                {
                    activities = new List<DestinyHistoricalStatsPeriodGroup>
                    {
                        new DestinyHistoricalStatsPeriodGroup
                        {
                            period = new DateTime(2025, 7, 20),
                            activityDetails = new DestinyHistoricalStatsActivity
                            {
                                referenceId = 100,
                                instanceId = "987654321"
                            },
                            values = new Dictionary<string, DestinyHistoricalStatsValue>
                            {
                                { "playerCount", new DestinyHistoricalStatsValue
                                    {
                                        basic = new DestinyHistoricalStatsValuePair
                                        {
                                            value = 1
                                        }
                                    }
                                },
                                {
                                    "score", new DestinyHistoricalStatsValue
                                    {
                                        basic = new DestinyHistoricalStatsValuePair
                                        {
                                            value = 123456789
                                        }
                                    }
                                },
                                {
                                    "activityDurationSeconds", new DestinyHistoricalStatsValue
                                    {
                                        basic = new DestinyHistoricalStatsValuePair
                                        {
                                            value = 3600
                                        }
                                    }
                                },
                                {
                                    "completed", new DestinyHistoricalStatsValue
                                    {
                                        basic = new DestinyHistoricalStatsValuePair
                                        {
                                            value = 1
                                        }
                                    }
                                },
                                {
                                    "completionReason", new DestinyHistoricalStatsValue
                                    {
                                        basic = new DestinyHistoricalStatsValuePair
                                        {
                                            value = 1
                                        }
                                    }
                                }
                            }
                        },
                        new DestinyHistoricalStatsPeriodGroup
                        {
                            period = new DateTime(2025, 7, 10),
                            activityDetails = new DestinyHistoricalStatsActivity
                            {
                                referenceId = 100,
                                instanceId = "987654322"
                            },
                            values = new Dictionary<string, DestinyHistoricalStatsValue>()
                        }
                    }
                },
                ErrorCode = 1,
                ErrorStatus = "Ok",
                Message = "Success",
                MessageData = new Dictionary<string, string>()
            });

        var dbContextOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"CharacterCrawlerTests_{Guid.NewGuid()}")
            .Options;
        var contextFactory = new PooledDbContextFactory<AppDbContext>(dbContextOptions);

        var crawler = new PlayerCrawler(
            clientMock.Object,
            NullLogger<PlayerCrawler>.Instance,
            contextFactory,
            cache,
            multiplexerMock.Object);

        var player = new Player
        {
            Id = 1,
            MembershipType = 2,
            DisplayName = "Guardian",
            DisplayNameCode = 9999,
            NeedsFullCheck = false
        };

        var reportsBag = new ConcurrentBag<ActivityReport>();
        await crawler.GetCharacterActivityReports(player, new DateTime(2025, 7, 18), "character-1", reportsBag, CancellationToken.None);

        Assert.Single(reportsBag);
        Assert.Equal(987654321L, reportsBag.First().Id);

        clientMock.Verify(client => client.GetHistoricalStatsForCharacter(1, 2, "character-1", 0, 250, Moq.It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetCharacterActivityReports_ReturnsEmptyBagWhenPGCRNotFound()
    {
        var databaseMock = new Mock<IDatabase>();
        databaseMock
            .Setup(db => db.HashGetAllAsync("activityHashMappings", Moq.It.IsAny<CommandFlags>()))
            .ReturnsAsync(Array.Empty<HashEntry>());

        var multiplexerMock = new Mock<IConnectionMultiplexer>();
        multiplexerMock
            .Setup(m => m.GetDatabase(Moq.It.IsAny<int>(), Moq.It.IsAny<object>()))
            .Returns(databaseMock.Object);

        var cache = new MemoryCache(new MemoryCacheOptions());

        var clientMock = new Mock<IDestiny2ApiClient>();
        clientMock.Setup(client => client.GetHistoricalStatsForCharacter(Moq.It.IsAny<long>(), Moq.It.IsAny<int>(), Moq.It.IsAny<string>(), Moq.It.IsAny<int>(), Moq.It.IsAny<int>(), Moq.It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DestinyApiException(new DestinyApiResponseError
            {
                ErrorCode = 1665,
                ErrorStatus = "Ignored",
                Message = "PGCR not available",
                MessageData = new Dictionary<string, string>()
            }));

        var dbContextOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"CharacterCrawlerTests_{Guid.NewGuid()}")
            .Options;
        var contextFactory = new PooledDbContextFactory<AppDbContext>(dbContextOptions);

        var crawler = new PlayerCrawler(
            clientMock.Object,
            NullLogger<PlayerCrawler>.Instance,
            contextFactory,
            cache,
            multiplexerMock.Object);

        var player = new Player
        {
            Id = 77,
            MembershipType = 3,
            DisplayName = "Guardian",
            DisplayNameCode = 1111,
            NeedsFullCheck = false
        };

        var reportsBag = new ConcurrentBag<ActivityReport>();
        await crawler.GetCharacterActivityReports(player, DateTime.UtcNow, "character-77", reportsBag, CancellationToken.None);

        Assert.Empty(reportsBag);
    }
}
