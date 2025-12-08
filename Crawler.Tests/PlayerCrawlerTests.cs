using API.Clients.Abstract;
using Crawler.Services;
using Domain.Data;
using Domain.DB;
using Domain.DestinyApi;
using Domain.DTO;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;
using System.Collections.Concurrent;
using Domain.Enums;

namespace Crawler.Tests;

public class PlayerCrawlerTests
{
    [Fact]
    public async Task GetCharactersForPlayer_ReturnsCharactersForValidPlayer()
    {
        const long playerId = 12345;

        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        await using (var seedContext = new AppDbContext(options))
        {
            seedContext.Players.Add(new Player
            {
                Id = playerId,
                MembershipType = 2,
                DisplayName = "Guardian",
                DisplayNameCode = 7777,
                FullDisplayName = "Guardian#7777",
                NeedsFullCheck = false
            });
            await seedContext.SaveChangesAsync();
        }

        var contextFactory = new TestDbContextFactory(options);
        var cache = new MemoryCache(new MemoryCacheOptions());

        var databaseMock = new Mock<IDatabase>();
        var multiplexerMock = new Mock<IConnectionMultiplexer>();
        multiplexerMock
            .Setup(m => m.GetDatabase(Moq.It.IsAny<int>(), Moq.It.IsAny<object>()))
            .Returns(databaseMock.Object);

        var clientMock = new Mock<IDestiny2ApiClient>();

        var apiResponse = new DestinyApiResponse<DestinyProfileResponse>
        {
            Response = new DestinyProfileResponse
            {
                profile = new DestinyProfile
                {
                    data = new ProfileData
                    {
                        userInfo = new UserInfoCard
                        {
                            bungieGlobalDisplayName = "Guardian",
                            bungieGlobalDisplayNameCode = 7777
                        }
                    }
                },
                characters = new DictionaryComponentResponseOfint64AndDestinyCharacterComponent
                {
                    data = new Dictionary<string, DestinyCharacterComponent>
                    {
                        ["recent-character"] = new DestinyCharacterComponent
                        {
                            emblemBackgroundPath = "/img/emblem1.png",
                            emblemPath = "/img/emblem1a.png",
                            dateLastPlayed = new DateTime(2025, 7, 16),
                            characterId = "recent-character"
                        },
                        ["old-character"] = new DestinyCharacterComponent
                        {
                            emblemBackgroundPath = "/img/emblem2.png",
                            emblemPath = "/img/emblem2a.png",
                            dateLastPlayed = new DateTime(2025, 7, 10),
                            characterId = "old-character"
                        }
                    }
                }
            },
            ErrorCode = 1,
            ErrorStatus = "Ok",
            Message = "Success",
            MessageData = new Dictionary<string, string>()
        };

        clientMock.Setup(client => client.GetCharactersForPlayer(playerId, 2, Moq.It.IsAny<CancellationToken>()))
            .ReturnsAsync(apiResponse);

        var crawler = new PlayerCrawler(
            clientMock.Object,
            NullLogger<PlayerCrawler>.Instance,
            contextFactory,
            cache,
            multiplexerMock.Object);

        await using var context = await contextFactory.CreateDbContextAsync();
        var characters = await crawler.GetCharactersForPlayer(playerId, 2, context, CancellationToken.None);

        Assert.Equal(2, characters.Count);
        Assert.Contains("recent-character", characters.Keys);
        Assert.Contains("old-character", characters.Keys);

        clientMock.Verify(client => client.GetCharactersForPlayer(playerId, 2, Moq.It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CheckPlayerNameAndEmblem_UpdatesPlayerWhenDisplayNameChanged()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using (var seedContext = new AppDbContext(options))
        {
            seedContext.Players.Add(new Player
            {
                Id = 42,
                MembershipType = 3,
                DisplayName = "OldName",
                DisplayNameCode = 1234,
                FullDisplayName = "OldName#1234",
                NeedsFullCheck = true
            });
            await seedContext.SaveChangesAsync();
        }

        await using var context = new AppDbContext(options);

        var cache = new MemoryCache(new MemoryCacheOptions());

        var databaseMock = new Mock<IDatabase>();
        var multiplexerMock = new Mock<IConnectionMultiplexer>();
        multiplexerMock
            .Setup(m => m.GetDatabase(Moq.It.IsAny<int>(), Moq.It.IsAny<object>()))
            .Returns(databaseMock.Object);

        var clientMock = new Mock<IDestiny2ApiClient>();

        var crawler = new PlayerCrawler(
            clientMock.Object,
            NullLogger<PlayerCrawler>.Instance,
            new TestDbContextFactory(options),
            cache,
            multiplexerMock.Object);

        var profileResponse = new DestinyProfileResponse
        {
            profile = new DestinyProfile
            {
                data = new ProfileData
                {
                    userInfo = new UserInfoCard
                    {
                        bungieGlobalDisplayName = "NewName",
                        bungieGlobalDisplayNameCode = 5678
                    }
                }
            },
            characters = new DictionaryComponentResponseOfint64AndDestinyCharacterComponent
            {
                data = new Dictionary<string, DestinyCharacterComponent>
                {
                    ["char-1"] = new DestinyCharacterComponent
                    {
                        characterId = "char-1",
                        dateLastPlayed = DateTime.UtcNow,
                        emblemBackgroundPath = "bg",
                        emblemPath = "emblem"
                    }
                }
            }
        };

        await crawler.CheckPlayerNameAndEmblem(profileResponse, 42, context, CancellationToken.None);
        await context.SaveChangesAsync();

        var updated = await context.Players.FirstOrDefaultAsync(p => p.Id == 42);
        Assert.NotNull(updated);
        Assert.Equal("NewName", updated!.DisplayName);
        Assert.Equal(5678, updated.DisplayNameCode);
        Assert.Equal("emblem", updated.LastPlayedCharacterEmblemPath);
        Assert.Equal("bg", updated.LastPlayedCharacterBackgroundPath);
    }
}
