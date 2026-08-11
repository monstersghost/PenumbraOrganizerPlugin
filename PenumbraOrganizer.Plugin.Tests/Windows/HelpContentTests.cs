using PenumbraOrganizer.Plugin.Windows;

namespace PenumbraOrganizer.Plugin.Tests.Windows;

public class HelpContentTests
{
    [Fact]
    public void EveryTopicConstant_ResolvesInTheResource()
    {
        var missing = HelpTopics.All.Where(t => !Help.TryGet(t, out _)).Select(t => t.Id);
        Assert.Empty(missing);
    }

    [Fact]
    public void TopicsAreDiscoveredByReflection_NotAnEmptyList()
    {
        // HelpTopics.All is built by reflection, so a bug there would make every other test in this
        // class pass vacuously over zero topics.
        Assert.NotEmpty(HelpTopics.All);
    }

    [Fact]
    public void EveryTopicHasATitle()
        => Assert.All(HelpTopics.All, t => Assert.False(string.IsNullOrWhiteSpace(Help.Title(t))));

    [Fact]
    public void EveryTopicHasAtLeastOneContentField()
    {
        // short/body/step are each optional, but a topic with none of them is dead weight.
        Assert.All(HelpTopics.All, t =>
            Assert.True(Help.Short(t) is not null || Help.Body(t) is not null || Help.Step(t) is not null));
    }

    [Fact]
    public void NoShortContainsANewlineOrExceedsTheLengthCap()
    {
        foreach (var t in HelpTopics.All)
        {
            var s = Help.Short(t);
            if (s is null) continue;
            Assert.DoesNotContain('\n', s);
            Assert.DoesNotContain('\r', s);   // a lone CR breaks the line just as a LF does
            Assert.True(s.Length <= 200, $"{t.Id} short is {s.Length} chars");
        }
    }

    [Fact]
    public void MissingResource_ThrowsWithAMessageNamingIt()
    {
        // A packaging bug, not a runtime condition - the same treatment NpcNameListStore gives its
        // seed. The message has to name the resource, because that is the only clue a report from
        // a user's log will carry.
        var ex = Assert.Throws<InvalidOperationException>(
            () => Help.ReadEmbedded(typeof(Help).Assembly, "PenumbraOrganizer.Plugin.Resources.no-such-file.json"));

        Assert.Contains("no-such-file.json", ex.Message);
    }

    [Fact]
    public void TheRealResourceIsActuallyEmbedded()
    {
        // Guards the csproj entry specifically. Without it every accessor above returns null and
        // the failure surfaces in-game as tooltips that never appear, with nothing in the log.
        Assert.False(string.IsNullOrWhiteSpace(
            Help.ReadEmbedded(typeof(Help).Assembly, Help.ResourceName)));
    }

    [Fact]
    public void DuplicateIds_AreRejectedRatherThanSilentlyWinning()
    {
        // Dictionary construction would throw anyway, but with a message that names neither the
        // file nor the id. Two entries for one control is a plausible merge outcome.
        const string json = """
            {"topics":[{"id":"a","title":"A","short":"x"},{"id":"a","title":"A again","short":"y"}]}
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => Help.Parse(json, "test.json"));

        Assert.Contains("test.json", ex.Message);
        Assert.Contains("a", ex.Message);
    }

    [Fact]
    public void AllContentIsAscii_SoEveryGlyphRendersInGame()
    {
        // Tooltips render through ImGui's font atlas, which does not carry every codepoint a text
        // editor will happily paste. An em dash or a curly quote copied out of USER_GUIDE.md renders
        // as a box or as nothing, and nothing in the build or the log would say so.
        //
        // This is also the repo's recurring transcription hazard: three separate times a pasted
        // glyph has reached code as the wrong codepoint with every test still green. Assert the
        // specific failure rather than trusting a reviewer to spot U+2019 against U+0027.
        foreach (var topic in HelpTopics.All)
        {
            foreach (var (field, text) in new[]
                     {
                         ("title", Help.Title(topic)), ("short", Help.Short(topic)),
                         ("body", Help.Body(topic)), ("step", Help.Step(topic)),
                     })
            {
                if (text is null)
                    continue;

                // Escapes only, never pasted literals. The previous version of this very line carried a
                // raw U+007F where it claimed to show an escape - invisible in review, and exactly the
                // failure this test exists to catch.
                // Printable ASCII, plus newline. A stray tab, CR or NUL is as unrenderable in ImGui
                // as an em dash, and a c > U+007F check alone would wave all three through - but
                // body and step are deliberately multi-paragraph, so LF has to stay legal. Shorts
                // are held to single-line separately, by NoShortContainsANewline... above.
                var offender = text.FirstOrDefault(c => c != '\n' && (c < ' ' || c > '~'));
                Assert.True(
                    offender == default,
                    $"{topic.Id} {field} contains non-ASCII U+{(int)offender:X4} ('{offender}'). "
                    + "Use a plain hyphen and a straight apostrophe.");
            }
        }
    }

    [Fact]
    public void EveryTopicWithAShort_IsDeclaredAsUsedByAControl()
    {
        // NAMED FOR WHAT IT PROVES. HelpTopicUsage.ReferencedByControls is a hand-maintained list,
        // so this is a DECLARED-USAGE CONSISTENCY check: it catches a topic whose tooltip text nobody
        // ever intends to show. It does NOT prove any control calls Help.Tooltip - a topic can appear
        // in help-content.json, in HelpTopics, and in this list while the widget has no call at all.
        //
        // Actual wiring is protected by three things, none of them this test: code review of the
        // call-site convention, the immediately-after-widget rule, and the manual in-game pass over
        // every control including disabled ones.
        var declared = HelpTopicUsage.ReferencedByControls;
        var withShort = HelpTopics.All.Where(t => Help.Short(t) is not null);

        Assert.Empty(withShort.Except(declared));
    }

    [Fact]
    public void EveryDeclaredUsage_IsAKnownTopic()
    {
        // The other direction, and cheap. Without it, renaming or deleting a topic leaves a stale
        // entry in the hand-maintained list that nothing ever flags.
        Assert.Empty(HelpTopicUsage.ReferencedByControls.Except(HelpTopics.All));
    }

    [Fact]
    public void DeclaredUsage_ListsEachTopicOnce()
    {
        // A duplicate would survive both Except checks above while quietly meaning two controls
        // claim the same explanation - usually a copy-paste during wiring.
        Assert.Empty(HelpTopicUsage.ReferencedByControls
            .GroupBy(t => t.Id, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key));
    }

    [Fact]
    public void EveryTopicIdIsUsedByAConstant_NotOrphanedInTheResource()
    {
        // The other direction: content written for an id no constant references can never be shown.
        var declared = HelpTopics.All.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var inResource = Help.Parse(
            Help.ReadEmbedded(typeof(Help).Assembly, Help.ResourceName), Help.ResourceName).Keys;

        Assert.Empty(inResource.Where(id => !declared.Contains(id)));
    }
}
