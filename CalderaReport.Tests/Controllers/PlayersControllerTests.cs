using CalderaReport.API.Controllers;
using CalderaReport.Domain.DB;
using CalderaReport.Domain.DestinyApi;
using CalderaReport.Domain.DTO.Requests;
using CalderaReport.Domain.DTO.Responses;
using CalderaReport.Domain.Enums;
using CalderaReport.Services.Abstract;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

namespace CalderaReport.Tests.Controllers;

public class PlayersControllerTests
{
    private readonly Mock<ILogger<PlayersController>> _loggerMock;
    private readonly Mock<IPlayerService> _playerServiceMock;
    private readonly Mock<ICrawlerService> _crawlerServiceMock;
    private readonly PlayersController _controller;

    public PlayersControllerTests()
    {
        _loggerMock = new Mock<ILogger<PlayersController>>();
        _playerServiceMock = new Mock<IPlayerService>();
        _crawlerServiceMock = new Mock<ICrawlerService>();
        _controller = new PlayersController(_loggerMock.Object, _playerServiceMock.Object, _crawlerServiceMock.Object);
    }

    [Fact]
    public async Task GetPlayerInfo_WithValidPlayerId_ReturnsOkResult()
    {
        var playerId = 123456L;
        var player = new Player
        {
            Id = playerId,
            DisplayName = "TestPlayer",
            DisplayNameCode = 1234,
            MembershipType = 3,
            FullDisplayName = "TestPlayer#1234"
        };

        _playerServiceMock.Setup(s => s.GetPlayer(playerId))
            .ReturnsAsync(player);

        var result = await _controller.GetPlayerInfo(playerId);

        result.Should().BeOfType<OkObjectResult>();
        var okResult = result as OkObjectResult;
        okResult!.Value.Should().BeEquivalentTo(new API.Models.Responses.PlayerDto(player));
    }

    [Fact]
    public async Task GetPlayerInfo_WithInvalidPlayerId_ReturnsNotFound()
    {
        var playerId = 123456L;
        _playerServiceMock.Setup(s => s.GetPlayer(playerId))
            .ReturnsAsync((Player?)null);

        var result = await _controller.GetPlayerInfo(playerId);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task GetPlayerInfo_WhenExceptionThrown_ReturnsInternalServerError()
    {
        var playerId = 123456L;
        _playerServiceMock.Setup(s => s.GetPlayer(playerId))
            .ThrowsAsync(new Exception("Database error"));

        var result = await _controller.GetPlayerInfo(playerId);

        result.Should().BeOfType<ObjectResult>();
        var objectResult = result as ObjectResult;
        objectResult!.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
    }

    [Fact]
    public async Task SearchPlayers_WithValidPlayerName_ReturnsOkResult()
    {
        var request = new SearchRequest { playerName = "TestPlayer" };
        var players = new List<Player>
        {
            new Player { Id = 123L, DisplayName = "TestPlayer", DisplayNameCode = 1234, MembershipType = 3, FullDisplayName = "TestPlayer1234" }
        };

        _playerServiceMock.Setup(s => s.SearchDbForPlayer(request.playerName))
            .ReturnsAsync(players);

        var result = await _controller.SearchPlayers(request);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task SearchPlayers_WithEmptyPlayerName_ReturnsBadRequest()
    {
        var request = new SearchRequest { playerName = "" };

        var result = await _controller.SearchPlayers(request);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task SearchPlayers_WithMembershipId_SearchesById()
    {
        var membershipId = "1234567890123456789";
        var request = new SearchRequest { playerName = membershipId };
        var player = new Player
        {
            Id = long.Parse(membershipId),
            DisplayName = "TestPlayer",
            DisplayNameCode = 1234,
            MembershipType = 3,
            FullDisplayName = "TestPlayer#1234"
        };

        _playerServiceMock.Setup(s => s.GetPlayer(long.Parse(membershipId)))
            .ReturnsAsync(player);

        var result = await _controller.SearchPlayers(request);

        result.Should().BeOfType<OkObjectResult>();
        _playerServiceMock.Verify(s => s.GetPlayer(long.Parse(membershipId)), Times.Once);
    }

    [Fact]
    public async Task LoadPlayerActivityReports_WithValidPlayerId_ReturnsNoContent()
    {
        var playerId = 123456L;
        _crawlerServiceMock.Setup(s => s.CrawlPlayer(playerId))
            .ReturnsAsync(true);

        var result = await _controller.LoadPlayerActivityReports(playerId);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task LoadPlayerActivityReports_WithNonExistentPlayer_ReturnsNotFound()
    {
        var playerId = 123456L;
        var errorResponse = new DestinyApiResponseError
        {
            ErrorCode = (int)BungieErrorCodes.AccountNotFound,
            ErrorStatus = "AccountNotFound",
            Message = "Account not found",
            MessageData = new Dictionary<string, string>()
        };
        _crawlerServiceMock.Setup(s => s.CrawlPlayer(playerId))
            .ThrowsAsync(new DestinyApiException(errorResponse));

        var result = await _controller.LoadPlayerActivityReports(playerId);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task LoadPlayerActivityReports_WithPrivateAccount_ReturnsForbidden()
    {
        var playerId = 123456L;
        var errorResponse = new DestinyApiResponseError
        {
            ErrorCode = (int)BungieErrorCodes.PrivateAccount,
            ErrorStatus = "PrivateAccount",
            Message = "Account is private",
            MessageData = new Dictionary<string, string>()
        };
        _crawlerServiceMock.Setup(s => s.CrawlPlayer(playerId))
            .ThrowsAsync(new DestinyApiException(errorResponse));

        var result = await _controller.LoadPlayerActivityReports(playerId);

        var objectResult = result as ObjectResult;
        objectResult!.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task GetPlayerReportsForActivity_WithValidIds_ReturnsActivityReportList()
    {
        var playerId = 123456L;
        var activityId = 789L;
        var reports = new List<ActivityReportPlayerDto>
        {
            new ActivityReportPlayerDto
            {
                Duration = TimeSpan.FromMinutes(10),
                Completed = true,
                Date = DateTime.UtcNow
            },
            new ActivityReportPlayerDto
            {
                Duration = TimeSpan.FromMinutes(15),
                Completed = true,
                Date = DateTime.UtcNow.AddDays(-1)
            }
        };

        _playerServiceMock.Setup(s => s.GetPlayerReportsForActivityAsync(playerId, activityId))
            .ReturnsAsync(reports);

        var result = await _controller.GetPlayerReportsForActivity(playerId, activityId);

        result.Should().BeOfType<OkObjectResult>();
        var okResult = result as OkObjectResult;
        okResult!.Value.Should().BeOfType<ActivityReportListDto>();
    }
}
