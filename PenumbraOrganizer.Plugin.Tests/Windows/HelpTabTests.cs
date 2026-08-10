using PenumbraOrganizer.Plugin.Windows;

namespace PenumbraOrganizer.Plugin.Tests.Windows;

public class HelpTabTests
{
    [Fact]
    public void EverySectionTopicResolves_AndHasABody()
    {
        Assert.All(HelpTab.Sections, section =>
        {
            Assert.True(Help.TryGet(section.Topic, out _), $"{section.Topic.Id} is not in the resource");
            Assert.False(string.IsNullOrWhiteSpace(Help.Body(section.Topic)), $"{section.Topic.Id} has no body");
        });
    }

    [Fact]
    public void EveryTopicWithABody_AppearsInSomeSection()
    {
        // The other half of the both-directions rule. Section order lives in code, so a body added
        // to the resource and never listed here renders nowhere and nothing would say so.
        var rendered = HelpTab.Sections
            .SelectMany(s => s.Controls.Append(s.Topic))
            .Select(t => t.Id)
            .ToHashSet(StringComparer.Ordinal);

        var withBody = HelpTopics.All.Where(t => Help.Body(t) is not null).Select(t => t.Id);

        Assert.Empty(withBody.Where(id => !rendered.Contains(id)));
    }

    [Fact]
    public void EveryControlTopicAppearsInExactlyOneSection()
    {
        // Every control topic is reachable from the Help tab, and none is listed twice. Without the
        // first half, a control could have a tooltip everywhere and no entry in the reference; the
        // second half catches the copy-paste that lands one control under two tabs.
        var listed = HelpTab.Sections.SelectMany(s => s.Controls).Select(t => t.Id).ToList();

        Assert.Empty(listed.GroupBy(id => id, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key));
        Assert.Empty(HelpTopicUsage.ReferencedByControls.Select(t => t.Id).Except(listed, StringComparer.Ordinal));
    }

    [Fact]
    public void SectionsAreNotEmpty()
    {
        // Guards against the list being emptied or the reflection-free constant list going stale;
        // every assertion above passes vacuously over zero sections.
        Assert.NotEmpty(HelpTab.Sections);
    }

    [Fact]
    public void SectionTopicsCarryNoShort_SoTheyNeverLookLikeTooltipContent()
    {
        // Sections are read in the Help tab, not hovered. A short here would also make
        // EveryTopicWithAShort_IsDeclaredAsUsedByAControl demand a widget that does not exist.
        Assert.All(HelpTab.Sections, s => Assert.Null(Help.Short(s.Topic)));
    }

    [Fact]
    public void TheGitHubLink_CarriesAVersionTag_NotMain()
    {
        // Pointing at main serves the newest guide to someone on an older build.
        Assert.DoesNotContain("/blob/main/", HelpTab.GuideUrl);
    }

    [Fact]
    public void TheDiscordLink_IsAPermanentInvite_AndNotVersionPinned()
    {
        // Baked into every shipped binary, so an expiring invite would rot in releases that can no
        // longer be changed. The version check is the inverse of GuideUrl's: pinning this to a
        // release would be the bug, since an older build should still reach a live server.
        Assert.StartsWith("https://discord.gg/", HelpTab.DiscordUrl);

        var version = typeof(PenumbraOrganizer.Plugin.Plugin).Assembly.GetName().Version!.ToString(4);
        Assert.DoesNotContain(version, HelpTab.DiscordUrl);
    }

    [Fact]
    public void TheGitHubLink_CarriesThisBuildsOwnVersion()
    {
        // Catches the realistic regression: the version gets bumped for a release and the URL is
        // forgotten, so the Help tab quietly serves the PREVIOUS release's guide.
        //
        // It cannot prove the tag was actually pushed - that stays a release-checklist item - but a
        // URL that does not even name this build is wrong before anyone reaches GitHub.
        var version = typeof(PenumbraOrganizer.Plugin.Plugin).Assembly.GetName().Version!.ToString(4);

        Assert.Contains($"/blob/{version}/", HelpTab.GuideUrl);
    }

    [Fact]
    public void TheGitHubLink_PointsAtTheGuideInThisRepo()
    {
        // A version tag alone is not enough - the URL also has to reach the right file. This is the
        // half a "not main" assertion cannot cover.
        Assert.StartsWith("https://github.com/monstersghost/PenumbraOrganizerPlugin/blob/", HelpTab.GuideUrl);
        Assert.EndsWith("/docs/USER_GUIDE.md", HelpTab.GuideUrl);
    }
}
