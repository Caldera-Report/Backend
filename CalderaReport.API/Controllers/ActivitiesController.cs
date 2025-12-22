using API.Models.Responses;
using CalderaReport.API.Telemetry;
using CalderaReport.Services.Abstract;
using Facet.Extensions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CalderaReport.API.Controllers;

[Route("api/[controller]")]
[ApiController]
public class ActivitiesController : ControllerBase
{
    private readonly IActivityService _activityService;
    private readonly ILogger<ActivitiesController> _logger;

    public ActivitiesController(ILogger<ActivitiesController> logger, IActivityService activityService)
    {
        _logger = logger;
        _activityService = activityService;
    }


    [HttpGet]
    public async Task<IActionResult> GetActivities()
    {
        using var activity = APITelemetry.StartActivity("API.GetActivities");
        activity?.SetTag("api.name", nameof(GetActivities));
        _logger.LogInformation("Recieved request to get all activities");
        try
        {
            var ops = await _activityService.GetAllActivities();
            return Ok(ops);

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while getting activities");
            activity?.SetTag("error", true);
            activity?.SetTag("error.message", ex.Message);
            return StatusCode(StatusCodes.Status500InternalServerError, "An error occurred while processing your request.");
        }
    }
}