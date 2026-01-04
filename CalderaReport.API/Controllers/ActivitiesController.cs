using CalderaReport.Domain.DTO.Responses;
using CalderaReport.Services.Abstract;
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

    /// <summary>
    /// Gets all activities
    /// </summary>
    /// <response code="200">Activities found with no errors</response>
    [ProducesResponseType(typeof(IEnumerable<OpTypeDto>), StatusCodes.Status200OK)]
    [HttpGet]
    public async Task<ActionResult<IEnumerable<OpTypeDto>>> GetActivities()
    {
        _logger.LogInformation("Recieved request to get all activities");
        var ops = await _activityService.GetAllActivities();
        return Ok(ops);
    }
}
