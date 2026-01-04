using CalderaReport.Domain.Data;
using CalderaReport.Domain.DB;
using CalderaReport.Domain.DTO.Responses;
using CalderaReport.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using StackExchange.Redis;
using System.Text.Json;

namespace CalderaReport.Tests.Services;

public class ActivityServiceTests
{
    private readonly Mock<IConnectionMultiplexer> _redisMock;
    private readonly Mock<IDatabase> _databaseMock;
    private readonly Mock<IDbContextFactory<AppDbContext>> _contextFactoryMock;
    private readonly ActivityService _service;

    public ActivityServiceTests()
    {
        _redisMock = new Mock<IConnectionMultiplexer>();
        _databaseMock = new Mock<IDatabase>();
        _contextFactoryMock = new Mock<IDbContextFactory<AppDbContext>>();

        _redisMock.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(_databaseMock.Object);

        _service = new ActivityService(_redisMock.Object, _contextFactoryMock.Object);
    }

    [Fact]
    public async Task GetAllActivities_WhenCached_ReturnsCachedData()
    {
        var cachedActivities = new List<OpTypeDto>
        {
            new OpTypeDto { Id = 1, Name = "Strike", Activities = new List<ActivityDto>() }
        };
        var cachedJson = JsonSerializer.Serialize(cachedActivities);

        _databaseMock.Setup(d => d.StringGetAsync("activities:all", It.IsAny<CommandFlags>()))
            .ReturnsAsync(new RedisValue(cachedJson));

        var result = await _service.GetAllActivities();

        result.Should().NotBeEmpty();
        result.Should().HaveCount(1);
        result.First().Name.Should().Be("Strike");
    }

    [Fact]
    public async Task GetAllActivities_WhenNotCached_QueriesDatabaseAndCachesResult()
    {
        var opType = new OpType
        {
            Id = 1,
            Name = "Strike"
        };

        var activities = new List<Activity>
        {
            new Activity { Id = 100, Name = "Test Strike", Enabled = true, OpTypeId = 1, ImageURL = "test.jpg", OpType = opType }
        };

        _databaseMock.Setup(d => d.StringGetAsync("activities:all", It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisValue.Null);

        _databaseMock.Setup(d => d.StringSetAsync(
                "activities:all",
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<bool>(),
                It.IsAny<When>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: "TestDb_GetAllActivities")
            .Options;
        using var context = new AppDbContext(options);
        context.OpTypes.Add(opType);
        context.Activities.AddRange(activities);
        await context.SaveChangesAsync();

        _contextFactoryMock.Setup(f => f.CreateDbContextAsync(default))
            .ReturnsAsync(context);

        var result = await _service.GetAllActivities();

        result.Should().NotBeEmpty();
        _databaseMock.Verify(d => d.StringSetAsync(
            "activities:all",
            It.IsAny<RedisValue>(),
            It.IsAny<TimeSpan?>(),
            It.IsAny<bool>(),
            It.IsAny<When>(),
            It.IsAny<CommandFlags>()), Times.Once);
    }

}
