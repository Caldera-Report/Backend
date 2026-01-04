using CalderaReport.Domain.DTO.Requests;
using CalderaReport.Domain.DTO.Responses;
using CalderaReport.Domain.Enums;
using CalderaReport.Domain.Errors;
using CalderaReport.Services.Abstract;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;

namespace CalderaReport.API.Controllers;

[Route("api/[controller]")]
[ApiController]
public class LeaderboardsController : ControllerBase
{
    private readonly ILeaderboardService _leaderboardService;
    private readonly IPlayerService _playerService;

    public LeaderboardsController(ILeaderboardService leaderboardService, IPlayerService playerService)
    {
        _leaderboardService = leaderboardService;
        _playerService = playerService;
    }

    /// <summary>
    /// Get the leaderboard for a specific activity and leaderboard type.
    /// </summary>
    /// <param name="leaderboardType">The type of leaderboard to retrieve.</param>
    /// <param name="activityId">The ID of the activity.</param>
    /// <param name="count">The number of entries to retrieve.</param>
    /// <param name="offset">The offset for pagination.</param>
    /// <response code="200">Activities found with no errors</response>
    /// <response code="400">Invalid leaderboard type.</response>
    [ProducesResponseType(typeof(IEnumerable<LeaderboardDTO>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status400BadRequest)]
    [HttpGet("{leaderboardType}/{activityId}")]
    public async Task<ActionResult<IEnumerable<LeaderboardDTO>>> GetLeaderboard(int leaderboardType, long activityId, [FromQuery][Required] int count, [FromQuery][Required] int offset)
    {
        if (!Enum.TryParse(leaderboardType.ToString(), out LeaderboardTypes type) || !Enum.IsDefined(type))
            return BadRequest(new ApiError("Invalid leaderboard type.", StatusCodes.Status400BadRequest));

        var leaderboard = await _leaderboardService.GetLeaderboard(activityId, type, count, offset);
        return Ok(leaderboard);
    }

    /// <summary>
    /// Search the leaderboard for a specific activity and leaderboard type by player name.
    /// </summary>
    /// <param name="leaderboardType">The type of leaderboard to search.</param>
    /// <param name="activityId">The ID of the activity.</param>
    /// <param name="request">The search request containing the player name.</param>
    /// response code="200">Players found with no errors</response>
    /// response code="400">Invalid leaderboard type.</response>
    [ProducesResponseType(typeof(IEnumerable<LeaderboardDTO>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status400BadRequest)]
    [HttpPost("{leaderboardType}/{activityId}/search")]
    public async Task<ActionResult<IEnumerable<LeaderboardDTO>>> SearchLeaderboardForPlayer(int leaderboardType, long activityId, [FromBody][Required] SearchRequest request)
    {
        if (!Enum.TryParse(leaderboardType.ToString(), out LeaderboardTypes type) || !Enum.IsDefined(type))
            return BadRequest(new ApiError("Invalid leaderboard type.", StatusCodes.Status400BadRequest));

        var players = await _playerService.SearchDbForPlayer(request.playerName);
        var playerIds = players.Select(p => p.Id).ToList();

        var leaderboard = await _leaderboardService.GetLeaderboardsForPlayer(playerIds, activityId, type);
        return Ok(leaderboard);
    }
}
