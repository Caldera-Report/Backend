using CalderaReport.Domain.DB;
using CalderaReport.Domain.Enums;
using CalderaReport.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace CalderaReport.Tests.Integration;

public class LeaderboardServiceIntegrationTests : IntegrationTestBase
{
    [Fact]
    public async Task GetLeaderboard_WithRealDatabase_ReturnsCorrectLeaderboard()
    {
        var activity = new Activity
        {
            Id = 100,
            Name = "IntegrationTestActivity",
            Enabled = true,
            OpTypeId = 1,
            ImageURL = "test.jpg",
            OpType = new OpType { Id = 1, Name = "Test" }
        };

        var players = new List<Player>
        {
            new Player { Id = 1001, DisplayName = "FastPlayer", DisplayNameCode = 1111, MembershipType = 3, FullDisplayName = "FastPlayer#1111" },
            new Player { Id = 1002, DisplayName = "SlowPlayer", DisplayNameCode = 2222, MembershipType = 3, FullDisplayName = "SlowPlayer#2222" }
        };

        var leaderboards = new List<PlayerLeaderboard>
        {
            new PlayerLeaderboard
            {
                PlayerId = 1001,
                ActivityId = 100,
                LeaderboardType = LeaderboardTypes.FastestCompletion,
                Data = 300
            },
            new PlayerLeaderboard
            {
                PlayerId = 1002,
                ActivityId = 100,
                LeaderboardType = LeaderboardTypes.FastestCompletion,
                Data = 500
            }
        };

        _dbContext.Activities.Add(activity);
        _dbContext.Players.AddRange(players);
        _dbContext.PlayerLeaderboards.AddRange(leaderboards);
        await _dbContext.SaveChangesAsync();

        var service = new LeaderboardService(_contextFactory, _redis);

        var result = await service.GetLeaderboard(100, LeaderboardTypes.FastestCompletion, 10, 0);

        result.Should().NotBeEmpty();
        result.Should().HaveCount(2);
        result.First().Rank.Should().Be(1);
        result.First().Player.DisplayName.Should().Be("FastPlayer");
        result.Last().Rank.Should().Be(2);
        result.Last().Player.DisplayName.Should().Be("SlowPlayer");
    }

    [Fact]
    public async Task GetLeaderboard_WithPagination_ReturnsCorrectPage()
    {
        var activity = new Activity
        {
            Id = 200,
            Name = "PaginationTestActivity",
            Enabled = true,
            OpTypeId = 1,
            ImageURL = "test.jpg",
            OpType = new OpType { Id = 1, Name = "Test" }
        };

        var players = Enumerable.Range(1, 15).Select(i => new Player
        {
            Id = 2000 + i,
            DisplayName = $"Player{i}",
            DisplayNameCode = 3000 + i,
            MembershipType = 3,
            FullDisplayName = $"Player{i}#{1000 + i}"
        }).ToList();

        var leaderboards = players.Select((p, i) => new PlayerLeaderboard
        {
            PlayerId = p.Id,
            ActivityId = 200,
            LeaderboardType = LeaderboardTypes.HighestScore,
            Data = 1000 - (i * 10)
        }).ToList();

        _dbContext.Activities.Add(activity);
        _dbContext.Players.AddRange(players);
        _dbContext.PlayerLeaderboards.AddRange(leaderboards);
        await _dbContext.SaveChangesAsync();

        var service = new LeaderboardService(_contextFactory, _redis);

        var result = await service.GetLeaderboard(200, LeaderboardTypes.HighestScore, 5, 5);

        result.Should().HaveCount(5);
        result.First().Rank.Should().Be(6);
        result.Last().Rank.Should().Be(10);
    }

    [Fact]
    public async Task GetLeaderboardsForPlayer_WithRealDatabase_ReturnsPlayerRankings()
    {
        var activity = new Activity
        {
            Id = 300,
            Name = "PlayerSearchTestActivity",
            Enabled = true,
            OpTypeId = 1,
            ImageURL = "test.jpg",
            OpType = new OpType { Id = 1, Name = "Test" }
        };

        var targetPlayer = new Player
        {
            Id = 3001,
            DisplayName = "TargetPlayer",
            DisplayNameCode = 4001,
            MembershipType = 3,
            FullDisplayName = "TargetPlayer#4001"
        };

        var otherPlayers = Enumerable.Range(1, 10).Select(i => new Player
        {
            Id = 3100 + i,
            DisplayName = $"OtherPlayer{i}",
            DisplayNameCode = 4100 + i,
            MembershipType = 3,
            FullDisplayName = $"OtherPlayer{i}#{4100 + i}"
        }).ToList();

        var allPlayers = new List<Player> { targetPlayer };
        allPlayers.AddRange(otherPlayers);

        var leaderboards = allPlayers.Select((p, i) => new PlayerLeaderboard
        {
            PlayerId = p.Id,
            ActivityId = 300,
            LeaderboardType = LeaderboardTypes.FastestCompletion,
            Data = 200 + (i * 50)
        }).ToList();

        _dbContext.Activities.Add(activity);
        _dbContext.Players.AddRange(allPlayers);
        _dbContext.PlayerLeaderboards.AddRange(leaderboards);
        await _dbContext.SaveChangesAsync();

        var service = new LeaderboardService(_contextFactory, _redis);

        var result = await service.GetLeaderboardsForPlayer(new List<long> { 3001 }, 300, LeaderboardTypes.FastestCompletion);

        result.Should().NotBeEmpty();
        result.Should().HaveCount(1);
        result.First().Player.DisplayName.Should().Be("TargetPlayer");
        result.First().Rank.Should().Be(1);
    }

}
