using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Windows;

namespace PenumbraOrganizer.Plugin.Tests.Windows;

/// <summary>The Sort tab's one-line status of the Heliosphere and Proteus modes (set on the Protect tab).</summary>
public class ToolModeStatusTextTests
{
    private const ToolMode P = ToolMode.Protected;
    private const ToolMode Inside = ToolMode.SortInside;
    private const ToolMode Normal = ToolMode.Normal;

    [Fact]
    public void Hidden_WhenNoToolModsAndBothModesAreTheDefault() =>
        Assert.Null(ToolModeStatusText.Build(0, P, 0, P));

    [Fact]
    public void BothProtected_WithMods_Warns()
    {
        var status = ToolModeStatusText.Build(22, P, 13, P)!;
        Assert.Equal(
            "22 Heliosphere mods: Protected (not sorted). 13 Proteus mods: Protected (not sorted). "
            + "Change this on the Protect tab.",
            status.Text);
        Assert.True(status.Warn);
    }

    [Fact]
    public void OnlyHeliosphereMods_ProteusLeftOut()
    {
        var status = ToolModeStatusText.Build(5, P, 0, P)!;
        Assert.Equal("5 Heliosphere mods: Protected (not sorted). Change this on the Protect tab.", status.Text);
        Assert.True(status.Warn);
    }

    [Fact]
    public void OnlyProteusMods_Singular()
    {
        var status = ToolModeStatusText.Build(0, P, 1, P)!;
        Assert.Equal("1 Proteus mod: Protected (not sorted). Change this on the Protect tab.", status.Text);
        Assert.True(status.Warn);
    }

    [Fact]
    public void SingularHeliosphere_PluralProteus()
    {
        Assert.Equal(
            "1 Heliosphere mod: Sort inside their folder. 2 Proteus mods: Treat as normal. Change this on the Protect tab.",
            ToolModeStatusText.Build(1, Inside, 2, Normal)!.Text);
    }

    [Fact]
    public void NotProtected_WithMods_IsMuted()
    {
        var status = ToolModeStatusText.Build(3, Normal, 4, Inside)!;
        Assert.Equal(
            "3 Heliosphere mods: Treat as normal. 4 Proteus mods: Sort inside their folder. Change this on the Protect tab.",
            status.Text);
        Assert.False(status.Warn);
    }

    [Fact]
    public void OneProtectedWithMods_OtherNormal_Warns()
    {
        var status = ToolModeStatusText.Build(3, Normal, 4, P)!;
        Assert.Equal(
            "3 Heliosphere mods: Treat as normal. 4 Proteus mods: Protected (not sorted). Change this on the Protect tab.",
            status.Text);
        Assert.True(status.Warn);
    }

    [Fact]
    public void NonDefaultMode_WithNoMods_IsStillShown_Muted()
    {
        var status = ToolModeStatusText.Build(0, Normal, 0, P)!;
        Assert.Equal("0 Heliosphere mods: Treat as normal. Change this on the Protect tab.", status.Text);
        Assert.False(status.Warn);

        Assert.Equal(
            "0 Proteus mods: Sort inside their folder. Change this on the Protect tab.",
            ToolModeStatusText.Build(0, P, 0, Inside)!.Text);
    }

    [Fact]
    public void ProtectedWithNoMods_ShownBesideTheOther_IsLeftOut()
    {
        var status = ToolModeStatusText.Build(0, P, 2, Normal)!;
        Assert.Equal("2 Proteus mods: Treat as normal. Change this on the Protect tab.", status.Text);
        Assert.False(status.Warn);
    }

    [Fact]
    public void UnknownStoredMode_ReadsAsProtected()
    {
        Assert.Null(ToolModeStatusText.Build(0, (ToolMode)99, 0, P));
        Assert.Equal(
            "2 Heliosphere mods: Protected (not sorted). Change this on the Protect tab.",
            ToolModeStatusText.Build(2, (ToolMode)99, 0, P)!.Text);
    }

    [Theory]
    [InlineData(P, P)]
    [InlineData(P, Inside)]
    [InlineData(P, Normal)]
    [InlineData(Inside, P)]
    [InlineData(Inside, Inside)]
    [InlineData(Inside, Normal)]
    [InlineData(Normal, P)]
    [InlineData(Normal, Inside)]
    [InlineData(Normal, Normal)]
    public void EveryModeCombination_WithMods_NamesBothTools_AndWarnsOnlyWhenOneIsProtected(ToolMode hs, ToolMode proteus)
    {
        var status = ToolModeStatusText.Build(2, hs, 3, proteus)!;
        Assert.StartsWith("2 Heliosphere mods: ", status.Text);
        Assert.Contains(" 3 Proteus mods: ", status.Text);
        Assert.EndsWith("Change this on the Protect tab.", status.Text);
        Assert.Equal(hs == P || proteus == P, status.Warn);
        Assert.All(status.Text, ch => Assert.True(ch < 128));
    }

    [Fact]
    public void Count_MatchesTheProtectTab_HeliosphereWithoutProteus_AndProteusMadeOnly()
    {
        OrganizerModRow Row(string id, bool hs = false, ProteusRole role = ProteusRole.None) => new()
        {
            Identifier = id, Name = id, Author = "a", CurrentPath = id, ProposedPath = id,
            HeliosphereManaged = hs, ProteusRole = role,
        };

        var counts = ToolModeStatusText.Count(
        [
            Row("h1", hs: true), Row("h2", hs: true), Row("p1", role: ProteusRole.Made),
            Row("both", hs: true, role: ProteusRole.Made), Row("Proteus", role: ProteusRole.Managed), Row("plain"),
        ]);

        Assert.Equal((2, 2), counts);
    }

    [Fact]
    public void ButtonLabel_IsAscii() =>
        Assert.All(ToolModeStatusText.OpenProtectTabLabel, ch => Assert.True(ch < 128));
}
