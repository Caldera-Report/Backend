using CalderaReport.API.Telemetry;
using CalderaReport.Domain.DTO.Requests;
using CalderaReport.Domain.Enums;
using CalderaReport.Services.Abstract;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;

namespace CalderaReport.API.Controllers;

[Route("api/[controller]")]
[ApiController]
public class LeaderboardsController : ControllerBase
{
    private readonly ILeaderboardService _leaderboardService;
    private readonly IPlayerService _playerService;
    private readonly ILogger<LeaderboardsController> _logger;

    public LeaderboardsController(ILogger<LeaderboardsController> logger, ILeaderboardService leaderboardService, IPlayerService playerService)
    {
        _logger = logger;
        _leaderboardService = leaderboardService;
        _playerService = playerService;
    }

    [HttpGet("/{leaderboardType}/{activityId}")]
    public async Task<IActionResult> GetLeaderboard(int leaderboardType, long activityId, [FromQuery][Required] int count, [FromQuery][Required] int offset)
    {
        using var activity = APITelemetry.StartActivity("API.GetLeaderboard");
        activity?.SetTag("api.function.name", nameof(GetLeaderboard));
        activity?.SetTag("api.activity.id", activityId);
        activity?.SetTag("api.leaderboard.type", leaderboardType);
        try
        {
            if (!Enum.TryParse(leaderboardType.ToString(), out LeaderboardTypes type))
                return new BadRequestResult();
            var leaderboard = await _leaderboardService.GetLeaderboard(activityId, type, count, offset);
            return Ok(leaderboard);
        }
        catch (Exception ex)
        {
            activity?.AddException(ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex, "Error retrieving leaderboard for activity: {ActivityId}, leaderboard: {leaderboardType}.", activityId, leaderboardType);
            return StatusCode(StatusCodes.Status500InternalServerError, ex);
        }
    }

    [HttpPost("/{leaderboardType}/{activityId}")]
    public async Task<IActionResult> SearchLeaderboardForPlayer(int leaderboardType, long activityId, [FromBody][Required] SearchRequest request)
    {
        using var activity = APITelemetry.StartActivity("ActivityFunctions.SearchForPlayerLeaderboard");
        activity?.SetTag("api.function.name", nameof(SearchLeaderboardForPlayer));
        activity?.SetTag("api.activity.id", activityId);
        activity?.SetTag("api.leaderboard.type", leaderboardType);
        try
        {
            if (!Enum.TryParse(leaderboardType.ToString(), out LeaderboardTypes type))
                return new BadRequestResult();

            var players = await _playerService.SearchDbForPlayer(request.playerName);
            var playerIds = players.Select(p => p.Id).ToList();

            var leaderboard = await _leaderboardService.GetLeaderboardsForPlayer(playerIds, activityId, type);
            return Ok(leaderboard);
        }
        catch (ArgumentException ex)
        {
            activity?.AddException(ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex, "Invalid arguments");
            return StatusCode(StatusCodes.Status400BadRequest, ex);
        }
        catch (Exception ex)
        {
            activity?.AddException(ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex, "Error retrieving leaderboards for player search query {searchRequest}, activity: {activityId}, leaderboard type: {leaderboardType}", request.playerName, activityId, leaderboardType);
            return StatusCode(StatusCodes.Status500InternalServerError, ex);
        }
    }
}
