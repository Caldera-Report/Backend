using CalderaReport.Domain.DestinyApi;
using CalderaReport.Domain.DTO.Requests;
using CalderaReport.Domain.DTO.Responses;
using CalderaReport.Domain.Errors;
using CalderaReport.Services.Abstract;
using Hangfire;
using Microsoft.AspNetCore.Mvc;
using System.Text.RegularExpressions;

namespace CalderaReport.API.Controllers;

[Route("api/[controller]")]
[ApiController]
public class PlayersController : ControllerBase
{
    private readonly ILogger<PlayersController> _logger;
    private readonly IPlayerService _playerService;
    private readonly ICrawlerService _crawlerService;

    public PlayersController(ILogger<PlayersController> logger, IPlayerService playerService, ICrawlerService crawlerService)
    {
        _logger = logger;
        _playerService = playerService;
        _crawlerService = crawlerService;
    }

    /// <summary>
    /// Retrieves detailed information about a player by their unique identifier.
    /// </summary>
    /// <param name="playerId">The unique identifier of the player whose information is to be retrieved.</param>
    /// <returns>An <see cref="ActionResult{T}"/> containing a <see cref="PlayerDto"/> with the player's information if found;
    /// otherwise, a 404 Not Found response with an error message.</returns>
    [ProducesResponseType(typeof(PlayerDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status404NotFound)]
    [HttpGet("{playerId}")]
    public async Task<ActionResult<PlayerDto>> GetPlayerInfo(long playerId)
    {
        _logger.LogInformation("GetPlayerInfo request received for player ID {PlayerId}.", playerId);
        var playerInfo = await _playerService.GetPlayer(playerId);
        if (playerInfo == null)
        {
            _logger.LogWarning("Player with ID {PlayerId} not found.", playerId);
            return NotFound(new ApiError($"Player with ID {playerId} does not exist", StatusCodes.Status404NotFound));
        }

        return Ok(new PlayerDto(playerInfo));
    }

    /// <summary>
    /// Searches for players matching the specified criteria in the request and returns a collection of player search
    /// results.
    /// </summary>
    /// <remarks>If the player name appears to be a 19-digit numeric ID, the search will first attempt to find
    /// a player by that ID. If no player is found by ID, or if the player name is not a 19-digit number, the search
    /// will proceed by name. The search may query both the local database and external sources as needed.</remarks>
    /// <param name="request">The search criteria used to find players. Must include a non-empty player name. If the player name is a 19-digit
    /// number, the search will attempt to find a player by ID.</param>
    /// <returns>An <see cref="ActionResult{T}"/> containing a collection of <see cref="PlayerSearchDto"/> objects that match the
    /// search criteria. Returns a 400 Bad Request if the player name is missing.</returns>
    [ProducesResponseType(typeof(IEnumerable<PlayerSearchDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status400BadRequest)]
    [HttpPost("search")]
    public async Task<ActionResult<IEnumerable<PlayerSearchDto>>> SearchPlayers([FromBody] SearchRequest request)
    {
        var playerName = request.playerName;
        _logger.LogInformation("Search request received for player {PlayerName}.", playerName);

        if (string.IsNullOrEmpty(playerName))
        {
            _logger.LogWarning("Search request rejected due to missing player name.");
            return BadRequest(new ApiError("Player name is required", StatusCodes.Status400BadRequest));
        }

        if (Regex.IsMatch(playerName, @"^\d{19}$"))
        {
            try
            {
                var response = await _playerService.GetPlayer(long.Parse(playerName));
                if (response != null)
                {
                    return Ok(new PlayerSearchDto(response));
                }
            }
            catch (ArgumentException ex) when (ex.Message.Contains("not found"))
            { // Swallow to allow for searching of Bungie API (just in case)
            }
        }

        var searchResults = await _playerService.SearchDbForPlayer(playerName);
        if (searchResults.Count() == 0)
            searchResults = await _playerService.SearchForPlayer(playerName);
        return Ok(searchResults.Select(p => new PlayerSearchDto(p)));
    }

    /// <summary>
    /// Retrieves a list of activity reports for the specified player and activity.
    /// </summary>
    /// <param name="playerId">The unique identifier of the player whose activity reports are to be retrieved.</param>
    /// <param name="activityId">The unique identifier of the activity for which reports are requested.</param>
    /// <returns>An <see cref="ActionResult{T}"/> containing an <see cref="ActivityReportListDto"/> with the player's reports for
    /// the specified activity. Returns an empty list if no reports are found.</returns>
    [ProducesResponseType(typeof(ActivityReportListDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status404NotFound)]
    [HttpGet("{playerId}/stats/{activityId}")]
    public async Task<ActionResult<ActivityReportListDto>> GetPlayerReportsForActivity(long playerId, long activityId)
    {
        _logger.LogInformation("Request recieved for playerId {playerId}, activity {activityId}", playerId, activityId);

        try
        {
            var reports = await _playerService.GetPlayerReportsForActivityAsync(playerId, activityId);
            var averageMs = reports.Count(r => r.Completed) > 0 ? reports.Where(r => r.Completed).Select(r => r.Duration.TotalMilliseconds).Average() : 0;
            var average = TimeSpan.FromMilliseconds(averageMs);
            var fastest = reports.OrderBy(r => r.Duration).FirstOrDefault(r => r.Completed);
            var recent = reports.OrderByDescending(r => r.Date).FirstOrDefault();

            return Ok(new ActivityReportListDto
            {
                Reports = reports.OrderBy(arpd => arpd.Date).ToList(),
                Average = average,
                Best = fastest,
                Recent = recent,
                CountCompleted = reports.Count(r => r.Completed)
            });
        }
        catch (ArgumentException)
        {
            return NotFound(new ApiError($"Either the playerId or activityId provided does not exist", StatusCodes.Status404NotFound));
        }
    }

    /// <summary>
    /// Initiates a crawl to load activity reports for the specified player and enqueues leaderboard processing if
    /// available.
    /// </summary>
    /// <remarks>This method triggers background processing for leaderboard updates if the background job
    /// system is configured. If the background job system is unavailable, leaderboard processing is skipped without
    /// affecting the activity report loading.</remarks>
    /// <param name="playerId">The unique identifier of the player whose activity reports are to be loaded.</param>
    /// <returns>An HTTP 204 No Content response if the activity reports are loaded successfully; HTTP 404 Not Found if the
    /// player does not exist; or HTTP 403 Forbidden if the player's account is private.</returns>
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status403Forbidden)]
    [HttpPost("{playerId}/load")]
    public async Task<ActionResult> LoadPlayerActivityReports(long playerId)
    {
        _logger.LogInformation("Activities load requested for player {MembershipId}.", playerId);

        try
        {
            var addedReports = await _crawlerService.CrawlPlayer(playerId);

            try
            {
                BackgroundJob.Enqueue<ILeaderboardService>("leaderboards-api", s => s.CheckAndComputeLeaderboards(playerId, addedReports));
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("JobStorage", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(ex, "Hangfire is not configured; skipping leaderboard job enqueue for player {PlayerId}.", playerId);
            }

            return NoContent();
        }
        catch (DestinyApiException ex) when (Enum.TryParse(ex.ErrorCode.ToString(), out BungieErrorCodes result) && result == BungieErrorCodes.AccountNotFound)
        {
            return NotFound(new ApiError($"Player {playerId} does not exist", StatusCodes.Status404NotFound));
        }
        catch (DestinyApiException ex) when (Enum.TryParse(ex.ErrorCode.ToString(), out BungieErrorCodes result) && result == BungieErrorCodes.PrivateAccount)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ApiError($"Player {playerId} has chosen to keep this information private", StatusCodes.Status403Forbidden));
        }
    }
}
