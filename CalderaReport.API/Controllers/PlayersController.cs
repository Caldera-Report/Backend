using API.Models.Responses;
using CalderaReport.API.Telemetry;
using CalderaReport.Domain.DestinyApi;
using CalderaReport.Domain.DTO.Requests;
using CalderaReport.Domain.DTO.Responses;
using CalderaReport.Services.Abstract;
using Facet.Extensions;
using Hangfire;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
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

    [HttpGet("{playerId}")]
    public async Task<IActionResult> GetPlayerInfo(long playerId)
    {
        using var activity = APITelemetry.StartActivity("API.GetPlayerInfo");
        activity?.SetTag("api.name", nameof(GetPlayerInfo));
        activity?.SetTag("api.player.id", playerId);
        _logger.LogInformation("GetPlayerInfo request received for player ID {PlayerId}.", playerId);
        try
        {
            var playerInfo = await _playerService.GetPlayer(playerId);
            if (playerInfo == null)
            {
                _logger.LogWarning("Player with ID {PlayerId} not found.", playerId);
                return NotFound($"Player with ID {playerId} does not exist");
            }
            return new OkObjectResult(playerInfo);
        }
        catch (Exception ex)
        {
            activity?.AddException(ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex, "Error retrieving info for player ID {PlayerId}.", playerId);
            return StatusCode(StatusCodes.Status500InternalServerError, ex.Message);
        }
    }

    [HttpPost("search")]
    public async Task<IActionResult> SearchPlayers([FromBody] SearchRequest request)
    {
        using var activity = APITelemetry.StartActivity("API.SearchForPlayer");
        activity?.SetTag("api.name", nameof(SearchPlayers));
        activity?.SetTag("api.player.query", request.playerName);
        var playerName = request.playerName;
        _logger.LogInformation("Search request received for player {PlayerName}.", playerName);

        if (string.IsNullOrEmpty(playerName))
        {
            _logger.LogWarning("Search request rejected due to missing player name.");
            return new BadRequestObjectResult("Player name is required");
        }
        try
        {
            if (Regex.IsMatch(playerName, @"^\d{19}$"))
            {
                try
                {
                    var response = await _playerService.GetPlayer(long.Parse(playerName));
                    return Ok(response);
                }
                catch (ArgumentException ex) when (ex.Message.Contains("not found"))
                { //Swallow to allow for searching of bungie api (just in case)
                }
            }

            var searchResults = await _playerService.SearchDbForPlayer(playerName);
            if (searchResults.Count() == 0)
                searchResults = await _playerService.SearchForPlayer(playerName);
            return Ok(searchResults.Select(p => p.ToFacet<PlayerSearchDto>()));
        }
        catch (Exception ex)
        {
            activity?.AddException(ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex, "Error searching for player {PlayerName}.", playerName);
            return StatusCode(StatusCodes.Status500InternalServerError, ex.Message);
        }
    }

    [HttpGet("{playerId}/stats/{activityId}")]
    public async Task<IActionResult> GetPlayerReportsForActivity(long playerId, long activityId)
    {
        using var activity = APITelemetry.StartActivity("API.GetReportsForActivity");
        activity?.SetTag("api.name", nameof(GetPlayerReportsForActivity));
        activity?.SetTag("api.player.query", playerId);
        activity?.SetTag("api.player.activity.query", activityId);
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
        catch (Exception ex)
        {
            activity?.AddException(ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex, "An error occurred while fetching reports for player: {playerId}, activity: {activityId}", playerId, activityId);
            return StatusCode(StatusCodes.Status500InternalServerError, ex.Message);
        }
    }

    [HttpPost("{playerId}/load")]
    public async Task<IActionResult> LoadPlayerActivityReports(long playerId)
    {
        using var activity = APITelemetry.StartActivity("PlayerFunctions.LoadPlayerActivities");
        activity?.SetTag("api.function.name", nameof(LoadPlayerActivityReports));
        activity?.SetTag("api.player.membershipId", playerId);
        _logger.LogInformation("Activities load requested for player {MembershipId}.", playerId);

        try
        {
            var addedReports = await _crawlerService.CrawlPlayer(playerId);

            try
            {
                BackgroundJob.Enqueue<ILeaderboardService>(s => s.CheckAndComputeLeaderboards(playerId, addedReports));
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("JobStorage", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(ex, "Hangfire is not configured; skipping leaderboard job enqueue for player {PlayerId}.", playerId);
            }
            
            return NoContent();
        }
        catch (DestinyApiException ex) when (Enum.TryParse(ex.ErrorCode.ToString(), out BungieErrorCodes result) && result == BungieErrorCodes.AccountNotFound)
        {
            return NotFound($"Player {playerId} does not exist");
        }
        catch (DestinyApiException ex) when (Enum.TryParse(ex.ErrorCode.ToString(), out BungieErrorCodes result) && result == BungieErrorCodes.PrivateAccount)
        {
            return StatusCode(StatusCodes.Status403Forbidden, $"Player {playerId} has chosen to keep this information private");
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, ex.Message);
        }
    }
}
