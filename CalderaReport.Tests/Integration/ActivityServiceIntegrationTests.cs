using CalderaReport.Domain.DB;
using CalderaReport.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace CalderaReport.Tests.Integration;

public class ActivityServiceIntegrationTests : IntegrationTestBase
{
    [Fact]
    public async Task GetAllActivities_WithRealDatabaseAndRedis_ReturnsCachedActivities()
    {
        var opType = new OpType
        {
            Id = 1,
            Name = "IntegrationTestOpType",
            Activities = new List<Activity>
            {
                new Activity
                {
                    Id = 1001,
                    Name = "IntegrationTestActivity",
                    Enabled = true,
                    OpTypeId = 1,
                    ImageURL = "test.jpg",
                    OpType = new OpType { Id = 1, Name = "IntegrationTestOpType" }
                }
            }
        };

        _dbContext.OpTypes.Add(opType);
        await _dbContext.SaveChangesAsync();

        var service = new ActivityService(_redis, _contextFactory);

        var result = await service.GetAllActivities();

        result.Should().NotBeEmpty();
        var firstOpType = result.First();
        firstOpType.Name.Should().Be("IntegrationTestOpType");
        firstOpType.Activities.Should().HaveCount(1);
        firstOpType.Activities.First().Name.Should().Be("IntegrationTestActivity");

        var cachedValue = await _redis.GetDatabase().StringGetAsync("activities:all");
        cachedValue.HasValue.Should().BeTrue();
    }

    [Fact]
    public async Task GetAllActivities_SecondCall_ReturnsCachedData()
    {
        var opType = new OpType
        {
            Id = 2,
            Name = "CacheTestOpType",
            Activities = new List<Activity>
            {
                new Activity { Id = 2001, Name = "CacheTestActivity", Enabled = true, OpTypeId = 2, ImageURL = "test.jpg", OpType = new OpType { Id = 2, Name = "CacheTestOpType" } }
            }
        };

        _dbContext.OpTypes.Add(opType);
        await _dbContext.SaveChangesAsync();

        var service = new ActivityService(_redis, _contextFactory);

        var firstResult = await service.GetAllActivities();
        firstResult.Should().NotBeEmpty();

        var cachedValue = await _redis.GetDatabase().StringGetAsync("activities:all");
        cachedValue.HasValue.Should().BeTrue("first call should cache the result");

        var secondResult = await service.GetAllActivities();

        secondResult.Should().NotBeEmpty();
        secondResult.Should().BeEquivalentTo(firstResult);
    }
}
