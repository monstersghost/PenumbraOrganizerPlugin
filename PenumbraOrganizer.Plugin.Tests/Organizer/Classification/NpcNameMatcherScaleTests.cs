using System.Diagnostics;
using System.Reflection;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Classification;

public class NpcNameMatcherScaleTests
{
    [Fact]
    public void Build_And_MatchAtFullWikiScale_CompletesQuickly()
    {
        // 25,000 names, above the 20,115 a full scrape produces.
        //
        // The FIRST token must vary. "Synthetic Name {i}" puts all 25,000 into a single bucket,
        // which is the exact opposite of the real distribution (9,886 buckets, median 1, max 185)
        // and turns every Match into a linear scan of 25,000 candidates. That measures nothing
        // useful and would likely blow this test's own budget.
        var names = Enumerable.Range(0, 25_000).Select(i => $"Synth{i} Name {i}").ToArray();

        var sw = Stopwatch.StartNew();
        var matcher = new NpcNameMatcher(names, [], []);
        var built = sw.ElapsedMilliseconds;

        sw.Restart();
        for (var i = 0; i < 2_000; i++)
            matcher.Match($"Some Mod About Synth{i} Name {i} And Things");
        var matched = sw.ElapsedMilliseconds;

        Assert.True(built < 2_000, $"build took {built}ms");
        Assert.True(matched < 2_000, $"2,000 matches took {matched}ms");
    }

    [Fact]
    public void MissAtFullWikiScale_IsAlsoCheap()
    {
        // A miss is the common case for a real library: most mods are not named after a character.
        // It is also the worst case for a structure that only short-circuits on a hit, so it is
        // measured separately rather than assumed to follow from the hit path.
        var matcher = new NpcNameMatcher(
            Enumerable.Range(0, 25_000).Select(i => $"Synth{i} Name {i}").ToArray(), [], []);

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 2_000; i++)
            matcher.Match("Ordinary Gear Retexture With Several Plain Words");

        Assert.True(sw.ElapsedMilliseconds < 2_000, $"2,000 misses took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void NpcNameMatcher_StoresNoRegexState()
    {
        // Named for what it actually proves. It inspects FIELD TYPES, so it catches a stored
        // Regex - the shape that caused the original problem - but a method-local `new Regex(...)`
        // or a static `Regex.IsMatch` call would pass. The real defence against a return to
        // pattern matching at scale is the timing tests above, not this.
        var referenced = typeof(NpcNameMatcher)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .Select(f => f.FieldType.FullName ?? "");

        Assert.DoesNotContain(referenced, n => n.Contains("System.Text.RegularExpressions"));
    }
}
