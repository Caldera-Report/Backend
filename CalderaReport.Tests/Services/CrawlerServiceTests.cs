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

    [Fact]
    public void TryResolveCanonicalActivityName_UsesExactOrSuffixMatchesWithoutSubstringBleed()
    {
        var canonicalNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Conquest: //node.ovrd.AVALON//"] = "Conquest: //node.ovrd.AVALON//",
            ["Ultimate Conquest: //node.ovrd.AVALON//"] = "Ultimate Conquest: //node.ovrd.AVALON//",
            ["The Warrior"] = "The Warrior"
        };

        var method = typeof(CrawlerService)
            .GetMethod("TryResolveCanonicalActivityName", BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull();

        var conquest = (string?)method!.Invoke(null, new object[] { "Conquest: //node.ovrd.AVALON//", canonicalNames });
        var ultimate = (string?)method.Invoke(null, new object[] { "Ultimate Conquest: //node.ovrd.AVALON//", canonicalNames });
        var empireHunt = (string?)method.Invoke(null, new object[] { "Empire Hunt: The Warrior", canonicalNames });

        conquest.Should().Be("Conquest: //node.ovrd.AVALON//");
        ultimate.Should().Be("Ultimate Conquest: //node.ovrd.AVALON//");
        empireHunt.Should().Be("The Warrior");
    }

    [Fact]
    public void NormalizeActivityName_CollapsesWhitespaceAndStripsCustomizeSuffix()
    {
        var method = typeof(CrawlerService)
            .GetMethod("NormalizeActivityName", BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull();

        var raw = "Empire Hunt:\u00A0  The Dark Priestess: Customize";
        var normalized = (string?)method!.Invoke(null, new object[] { raw });

        normalized.Should().Be("Empire Hunt: The Dark Priestess");
    }
}
