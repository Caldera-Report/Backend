using CalderaReport.Domain.Data;
using CalderaReport.Domain.DB;
using CalderaReport.Domain.Enums;
using CalderaReport.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace CalderaReport.Tests.Services;

public class LeaderboardServiceTests
{
    private readonly Mock<IDbContextFactory<AppDbContext>> _contextFactoryMock;
    private readonly LeaderboardService _service;

    public LeaderboardServiceTests()
    {
        _contextFactoryMock = new Mock<IDbContextFactory<AppDbContext>>();
        _service = new LeaderboardService(_contextFactoryMock.Object);
    }

    [Fact]
    public async Task GetLeaderboard_WithValidActivityId_ReturnsLeaderboard()
    {
        var activityId = 100L;
        var activity = new Activity { Id = activityId, Name = "Test Activity", Enabled = true, OpTypeId = 1, ImageURL = "test.jpg", OpType = new OpType { Id = 1, Name = "Test" } };
        var players = new List<Player>
        {
            new Player { Id = 1, DisplayName = "Player1", DisplayNameCode = 1234, MembershipType = 3, FullDisplayName = "Player1#1234" },
            new Player { Id = 2, DisplayName = "Player2", DisplayNameCode = 5678, MembershipType = 3, FullDisplayName = "Player2#5678" }
        };
        var leaderboards = new List<PlayerLeaderboard>
        {
            new PlayerLeaderboard
            {
                PlayerId = 1,
                ActivityId = activityId,
                LeaderboardType = LeaderboardTypes.FastestCompletion,
                Data = 300,
                Player = players[0]
            },
            new PlayerLeaderboard
            {
                PlayerId = 2,
                ActivityId = activityId,
                LeaderboardType = LeaderboardTypes.FastestCompletion,
                Data = 400,
                Player = players[1]
            }
        };

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: "TestDb_GetLeaderboard")
            .Options;
        using var context = new AppDbContext(options);
        context.Activities.Add(activity);
        context.Players.AddRange(players);
        context.PlayerLeaderboards.AddRange(leaderboards);
        await context.SaveChangesAsync();

        _contextFactoryMock.Setup(f => f.CreateDbContext())
            .Returns(context);

        var result = await _service.GetLeaderboard(activityId, LeaderboardTypes.FastestCompletion, 10, 0);

        result.Should().NotBeEmpty();
        result.Should().HaveCount(2);
        result.First().Rank.Should().Be(1);
        result.First().Player.DisplayName.Should().Be("Player1");
    }

    [Fact]
    public async Task GetLeaderboard_WithInvalidActivityId_ThrowsArgumentException()
    {
        var activityId = 999L;

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: "TestDb_InvalidActivity")
            .Options;
        using var context = new AppDbContext(options);

        _contextFactoryMock.Setup(f => f.CreateDbContext())
            .Returns(context);

        var act = async () => await _service.GetLeaderboard(activityId, LeaderboardTypes.FastestCompletion, 10, 0);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("Activity does not exist");
    }

    [Fact]
    public async Task GetLeaderboard_WithOffsetAndCount_ReturnsPaginatedResults()
    {
        var activityId = 100L;
        var activity = new Activity { Id = activityId, Name = "Test Activity", Enabled = true, OpTypeId = 1, ImageURL = "test.jpg", OpType = new OpType { Id = 1, Name = "Test" } };
        var players = Enumerable.Range(1, 20).Select(i => new Player
        {
            Id = i,
            DisplayName = $"Player{i}",
            DisplayNameCode = 1000 + i,
            MembershipType = 3,
            FullDisplayName = $"Player{i}#{1000 + i}"
        }).ToList();

        var leaderboards = players.Select((p, i) => new PlayerLeaderboard
        {
            PlayerId = p.Id,
            ActivityId = activityId,
            LeaderboardType = LeaderboardTypes.FastestCompletion,
            Data = 100 + i * 10,
            Player = p
        }).ToList();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: "TestDb_Pagination")
            .Options;
        using var context = new AppDbContext(options);
        context.Activities.Add(activity);
        context.Players.AddRange(players);
        context.PlayerLeaderboards.AddRange(leaderboards);
        await context.SaveChangesAsync();

        _contextFactoryMock.Setup(f => f.CreateDbContext())
            .Returns(context);

        var result = await _service.GetLeaderboard(activityId, LeaderboardTypes.FastestCompletion, 5, 5);

        result.Should().HaveCount(5);
        result.First().Rank.Should().Be(6);
    }

    [Fact]
    public async Task GetLeaderboardsForPlayer_WithValidPlayerIds_ReturnsPlayerLeaderboards()
    {
        var playerIds = new List<long> { 1, 2 };
        var activityId = 100L;
        var activity = new Activity { Id = activityId, Name = "Test Activity", Enabled = true, OpTypeId = 1, ImageURL = "test.jpg", OpType = new OpType { Id = 1, Name = "Test" } };
        var players = new List<Player>
        {
            new Player { Id = 1, DisplayName = "Player1", DisplayNameCode = 1234, MembershipType = 3, FullDisplayName = "Player1#1234" },
            new Player { Id = 2, DisplayName = "Player2", DisplayNameCode = 5678, MembershipType = 3, FullDisplayName = "Player2#5678" }
        };
        var leaderboards = new List<PlayerLeaderboard>
        {
            new PlayerLeaderboard
            {
                PlayerId = 1,
                ActivityId = activityId,
                LeaderboardType = LeaderboardTypes.HighestScore,
                Data = 1000,
                Player = players[0]
            },
            new PlayerLeaderboard
            {
                PlayerId = 2,
                ActivityId = activityId,
                LeaderboardType = LeaderboardTypes.HighestScore,
                Data = 900,
                Player = players[1]
            }
        };

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: "TestDb_PlayerLeaderboards")
            .Options;
        using var context = new AppDbContext(options);
        context.Activities.Add(activity);
        context.Players.AddRange(players);
        context.PlayerLeaderboards.AddRange(leaderboards);
        await context.SaveChangesAsync();

        _contextFactoryMock.Setup(f => f.CreateDbContext())
            .Returns(() => new AppDbContext(options));

        var result = await _service.GetLeaderboardsForPlayer(playerIds, activityId, LeaderboardTypes.HighestScore);

        result.Should().NotBeEmpty();
        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetLeaderboardsForPlayer_WithEmptyPlayerIds_ReturnsEmptyList()
    {
        var playerIds = new List<long>();
        var activityId = 100L;

        var result = await _service.GetLeaderboardsForPlayer(playerIds, activityId, LeaderboardTypes.FastestCompletion);

        result.Should().BeEmpty();
    }
}
