using CalderaReport.Clients.Abstract;
using CalderaReport.Domain.Data;
using CalderaReport.Domain.DB;
using CalderaReport.Domain.DestinyApi;
using CalderaReport.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using StackExchange.Redis;

namespace CalderaReport.Tests.Services;

public class PlayerServiceTests
{
    private readonly Mock<IBungieClient> _bungieClientMock;
    private readonly Mock<IDbContextFactory<AppDbContext>> _contextFactoryMock;
    private readonly Mock<IConnectionMultiplexer> _redisMock;
    private readonly Mock<IDatabase> _databaseMock;
    private readonly PlayerService _service;

    public PlayerServiceTests()
    {
        _bungieClientMock = new Mock<IBungieClient>();
        _contextFactoryMock = new Mock<IDbContextFactory<AppDbContext>>();
        _redisMock = new Mock<IConnectionMultiplexer>();
        _databaseMock = new Mock<IDatabase>();

        _redisMock.Setup(r => r.GetDatabase(Moq.It.IsAny<int>(), Moq.It.IsAny<object>()))
            .Returns(_databaseMock.Object);

        _service = new PlayerService(
            _bungieClientMock.Object,
            _contextFactoryMock.Object,
            _redisMock.Object);
    }

    [Fact]
    public async Task GetPlayer_WithValidId_ReturnsPlayer()
    {
        var playerId = 123456L;
        var expectedPlayer = new Player
        {
            Id = playerId,
            DisplayName = "TestPlayer",
            DisplayNameCode = 1234,
            MembershipType = 3,
            FullDisplayName = "TestPlayer#1234"
        };

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: "TestDb_GetPlayer")
            .Options;
        using var context = new AppDbContext(options);
        context.Players.Add(expectedPlayer);
        await context.SaveChangesAsync();

        _contextFactoryMock.Setup(f => f.CreateDbContextAsync(default))
            .ReturnsAsync(context);

        var result = await _service.GetPlayer(playerId);

        result.Should().NotBeNull();
        result.Id.Should().Be(playerId);
        result.DisplayName.Should().Be("TestPlayer");
    }

    [Fact]
    public async Task SearchForPlayer_WithBungieName_CallsSearchByBungieName()
    {
        var playerName = "TestPlayer#1234";
        var userInfoCards = new List<UserInfoCard>
        {
            new UserInfoCard
            {
                membershipId = "123456",
                membershipType = 3,
                bungieGlobalDisplayName = "TestPlayer",
                bungieGlobalDisplayNameCode = 1234,
                applicableMembershipTypes = new List<int> { 3 }
            }
        };

        var searchRequest = new ExactSearchRequest
        {
            displayName = "TestPlayer",
            displayNameCode = 1234
        };

        _bungieClientMock.Setup(c => c.PerformSearchByBungieName(Moq.It.IsAny<ExactSearchRequest>(), -1))
            .ReturnsAsync(new DestinyApiResponse<List<UserInfoCard>>
            {
                Response = userInfoCards.ToList(),
                ErrorCode = 1,
                ErrorStatus = "Success",
                Message = "Success",
                MessageData = new Dictionary<string, string>()
            });

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: "TestDb_SearchForPlayer")
            .Options;
        using var context = new AppDbContext(options);
        _contextFactoryMock.Setup(f => f.CreateDbContextAsync(default))
            .ReturnsAsync(context);

        var result = await _service.SearchForPlayer(playerName);

        result.Should().NotBeEmpty();
        result.First().DisplayName.Should().Be("TestPlayer");
    }
}
