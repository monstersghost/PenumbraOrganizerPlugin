using System.Diagnostics;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Classification;

public class NpcNameMatcherPerfTests
{
    [Fact]
    public void ConstructionAndFullScanPass_CompletesWithinAGenerousBound()
    {
        var npcs = Enumerable.Range(0, 4000).Select(i => $"Test Npc Name {i}").ToList();
        var enemies = Enumerable.Range(0, 4000).Select(i => $"Test Enemy Name {i}").ToList();
        var bosses = Enumerable.Range(0, 4000).Select(i => $"Test Boss Name {i}").ToList();
        var modNames = Enumerable.Range(0, 500).Select(i => $"Some Ordinary Mod {i}").ToList();

        var stopwatch = Stopwatch.StartNew();
        var matcher = new NpcNameMatcher(npcs, enemies, bosses);
        foreach (var modName in modNames)
            matcher.Match(modName);
        stopwatch.Stop();

        // Generous on purpose: this guards against reintroducing thousands of separate
        // compiled Regex objects (which was seconds-to-tens-of-seconds slow), not against
        // ordinary variance in a single combined-regex-per-category build.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"Matcher construction + scan took {stopwatch.Elapsed}, expected under 5s.");
    }
}
