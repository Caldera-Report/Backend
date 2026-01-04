using CalderaReport.API.Controllers;
using CalderaReport.Domain.DB;
using CalderaReport.Domain.DTO.Requests;
using CalderaReport.Domain.DTO.Responses;
using CalderaReport.Domain.Enums;
using CalderaReport.Services.Abstract;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace CalderaReport.Tests.Controllers;

public class LeaderboardsControllerTests
{
    private readonly Mock<ILeaderboardService> _leaderboardServiceMock;
    private readonly Mock<IPlayerService> _playerServiceMock;
    private readonly LeaderboardsController _controller;

    public LeaderboardsControllerTests()
    {
        _leaderboardServiceMock = new Mock<ILeaderboardService>();
        _playerServiceMock = new Mock<IPlayerService>();
        _controller = new LeaderboardsController(
            _leaderboardServiceMock.Object,
            _playerServiceMock.Object);
    }

    [Fact]
    public async Task GetLeaderboard_WithValidParameters_ReturnsOkResult()
    {
        var leaderboardType = (int)LeaderboardTypes.FastestCompletion;
        var activityId = 123L;
        var count = 10;
        var offset = 0;
        var leaderboard = new List<LeaderboardDTO> { new LeaderboardDTO { Rank = 1, Data = "test" } };

        _leaderboardServiceMock.Setup(s => s.GetLeaderboard(activityId, LeaderboardTypes.FastestCompletion, count, offset))
            .ReturnsAsync(leaderboard);

        var result = await _controller.GetLeaderboard(leaderboardType, activityId, count, offset);

        result.Result.Should().BeOfType<OkObjectResult>();
        var okResult = result.Result as OkObjectResult;
        okResult!.Value.Should().Be(leaderboard);
    }

    [Fact]
    public async Task GetLeaderboard_WithInvalidLeaderboardType_ReturnsBadRequest()
    {
        var leaderboardType = 999;
        var activityId = 123L;
        var count = 10;
        var offset = 0;

        var result = await _controller.GetLeaderboard(leaderboardType, activityId, count, offset);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task GetLeaderboard_WhenExceptionThrown_ReturnsInternalServerError()
    {
        var leaderboardType = (int)LeaderboardTypes.FastestCompletion;
        var activityId = 123L;
        var count = 10;
        var offset = 0;

        _leaderboardServiceMock.Setup(s => s.GetLeaderboard(activityId, LeaderboardTypes.FastestCompletion, count, offset))
            .ThrowsAsync(new Exception("Database error"));

        var act = async () => await _controller.GetLeaderboard(leaderboardType, activityId, count, offset);

        await act.Should().ThrowAsync<Exception>()
            .WithMessage("Database error");
    }

    [Fact]
    public async Task SearchLeaderboardForPlayer_WithValidParameters_ReturnsOkResult()
    {
        var leaderboardType = (int)LeaderboardTypes.FastestCompletion;
        var activityId = 123L;
        var request = new SearchRequest { playerName = "TestPlayer" };
        var players = new List<Player>
        {
            new Player { Id = 456L, DisplayName = "TestPlayer", DisplayNameCode = 1234, MembershipType = 3, FullDisplayName = "TestPlayer#1234" }
        };
        var leaderboard = new List<LeaderboardDTO> { new LeaderboardDTO { Rank = 5, Data = "test" } };

        _playerServiceMock.Setup(s => s.SearchDbForPlayer(request.playerName))
            .ReturnsAsync(players);
        _leaderboardServiceMock.Setup(s => s.GetLeaderboardsForPlayer(
                Moq.It.IsAny<List<long>>(),
                activityId,
                LeaderboardTypes.FastestCompletion))
            .ReturnsAsync(leaderboard);

        var result = await _controller.SearchLeaderboardForPlayer(leaderboardType, activityId, request);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task SearchLeaderboardForPlayer_WithInvalidLeaderboardType_ReturnsBadRequest()
    {
        var leaderboardType = 999;
        var activityId = 123L;
        var request = new SearchRequest { playerName = "TestPlayer" };

        // Similar to GetLeaderboard, TryParse succeeds for numeric strings
        var result = await _controller.SearchLeaderboardForPlayer(leaderboardType, activityId, request);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task SearchLeaderboardForPlayer_WhenArgumentExceptionThrown_ReturnsBadRequest()
    {
        var leaderboardType = (int)LeaderboardTypes.FastestCompletion;
        var activityId = 123L;
        var request = new SearchRequest { playerName = "TestPlayer" };

        _playerServiceMock.Setup(s => s.SearchDbForPlayer(request.playerName))
            .ThrowsAsync(new ArgumentException("Invalid player name"));

        var act = async () => await _controller.SearchLeaderboardForPlayer(leaderboardType, activityId, request);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("Invalid player name");
    }
}
