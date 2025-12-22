using CalderaReport.Clients.Abstract;
using CalderaReport.Domain.DB;
using CalderaReport.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace CalderaReport.Tests.Integration;

public class PlayerServiceIntegrationTests : IntegrationTestBase
{
    [Fact]
    public async Task GetPlayer_WithRealDatabase_ReturnsPlayer()
    {
        var player = new Player
        {
            Id = 123456789L,
            DisplayName = "IntegrationTestPlayer",
            DisplayNameCode = 9999,
            MembershipType = 3,
            FullDisplayName = "IntegrationTestPlayer#9999"
        };

        _dbContext.Players.Add(player);
        await _dbContext.SaveChangesAsync();

        var bungieClientMock = new Mock<IBungieClient>();
        var service = new PlayerService(bungieClientMock.Object, _contextFactory, _redis);

        var result = await service.GetPlayer(player.Id);

        result.Should().NotBeNull();
        result.Id.Should().Be(player.Id);
        result.DisplayName.Should().Be("IntegrationTestPlayer");
    }

    [Fact]
    public async Task SearchDbForPlayer_WithRealDatabase_ReturnsMatchingPlayers()
    {
        var players = new List<Player>
        {
            new Player { Id = 1, DisplayName = "TestPlayer1", DisplayNameCode = 1111, MembershipType = 3, FullDisplayName = "TestPlayer1#1111" },
            new Player { Id = 2, DisplayName = "TestPlayer2", DisplayNameCode = 2222, MembershipType = 3, FullDisplayName = "TestPlayer2#2222" },
            new Player { Id = 3, DisplayName = "OtherPlayer", DisplayNameCode = 3333, MembershipType = 3, FullDisplayName = "OtherPlayer#3333" }
        };

        _dbContext.Players.AddRange(players);
        await _dbContext.SaveChangesAsync();

        var bungieClientMock = new Mock<IBungieClient>();
        var service = new PlayerService(bungieClientMock.Object, _contextFactory, _redis);

        var result = await service.SearchDbForPlayer("Test");

        result.Should().NotBeEmpty();
        result.Count().Should().BeGreaterThanOrEqualTo(2);
        result.Should().Contain(p => p.DisplayName == "TestPlayer1");
        result.Should().Contain(p => p.DisplayName == "TestPlayer2");
    }

}
