using CalderaReport.Services;
using FluentAssertions;
using System.Collections.Concurrent;
using System.Reflection;

namespace CalderaReport.Tests.Services;

public class CrawlerServiceTests
{
    [Fact]
    public void GetNextSessionId_WhenCalledMultipleTimes_IncrementsPerKey()
    {
        var counters = new ConcurrentDictionary<(long ReportId, long PlayerId), int>();
        var method = typeof(CrawlerService)
            .GetMethod("GetNextSessionId", BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull();
        var first = (int)method!.Invoke(null, new object[] { counters, 100L, 42L })!;
        var second = (int)method.Invoke(null, new object[] { counters, 100L, 42L })!;
        var otherKey = (int)method.Invoke(null, new object[] { counters, 101L, 42L })!;

        first.Should().Be(1);
        second.Should().Be(2);
        otherKey.Should().Be(1);
    }
}
