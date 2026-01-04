using CalderaReport.API.Controllers;
using CalderaReport.Domain.DTO.Responses;
using CalderaReport.Services.Abstract;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

namespace CalderaReport.Tests.Controllers;

public class ActivitiesControllerTests
{
    private readonly Mock<ILogger<ActivitiesController>> _loggerMock;
    private readonly Mock<IActivityService> _activityServiceMock;
    private readonly ActivitiesController _controller;

    public ActivitiesControllerTests()
    {
        _loggerMock = new Mock<ILogger<ActivitiesController>>();
        _activityServiceMock = new Mock<IActivityService>();
        _controller = new ActivitiesController(_loggerMock.Object, _activityServiceMock.Object);
    }

    [Fact]
    public async Task GetActivities_WithValidRequest_ReturnsOkResult()
    {
        var activities = new List<OpTypeDto>
        {
            new OpTypeDto
            {
                Id = 1,
                Name = "Strike",
                Activities = new List<ActivityDto>
                {
                    new ActivityDto { Id = "100", Name = "Test Strike" }
                }
            }
        };

        _activityServiceMock.Setup(s => s.GetAllActivities())
            .ReturnsAsync(activities);

        var result = await _controller.GetActivities();

        result.Should().BeOfType<OkObjectResult>();
        var okResult = result as OkObjectResult;
        okResult!.Value.Should().BeEquivalentTo(activities);
    }

    [Fact]
    public async Task GetActivities_WhenExceptionThrown_ReturnsInternalServerError()
    {
        _activityServiceMock.Setup(s => s.GetAllActivities())
            .ThrowsAsync(new Exception("Database connection failed"));

        var result = await _controller.GetActivities();

        result.Should().BeOfType<ObjectResult>();
        var objectResult = result as ObjectResult;
        objectResult!.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
    }

    [Fact]
    public async Task GetActivities_WithEmptyList_ReturnsOkWithEmptyList()
    {
        var activities = new List<OpTypeDto>();
        _activityServiceMock.Setup(s => s.GetAllActivities())
            .ReturnsAsync(activities);

        var result = await _controller.GetActivities();

        result.Should().BeOfType<OkObjectResult>();
        var okResult = result as OkObjectResult;
        var resultValue = okResult!.Value as IEnumerable<OpTypeDto>;
        resultValue.Should().BeEmpty();
    }
}
