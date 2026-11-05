using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class OrganizerStateToolPolicyTests
{
    private static OrganizerModRow Row(
        string id, string path, bool hs = false, ProteusRole role = ProteusRole.None) => new()
    {
        Identifier = id, Name = id, Author = "A", CurrentPath = path, ProposedPath = path,
        Category = ModCategory.Gear, SubCategory = "Top",
        HeliosphereManaged = hs, ProteusRole = role,
    };

    private static OrganizerState StateWith(ToolPolicy policy, params OrganizerModRow[] rows)
    {
        var state = new OrganizerState();
        state.SetToolPolicy(policy);
        state.LoadScan(rows, new HashSet<string>());
        return state;
    }

    private static bool IsProtected(OrganizerState state, string id) =>
        state.Mods.Single(m => m.Identifier == id).Protected;

    [Fact]
    public void Default_ProtectsBothToolsMods_AndTheManagedMod()
    {
        var state = StateWith(ToolPolicy.Default,
            Row("hs", "x/hs", hs: true), Row("pt", "Proteus Upscales/pt", role: ProteusRole.Made),
            Row("pm", "Proteus", role: ProteusRole.Managed), Row("plain", "Gear/plain"));

        Assert.True(IsProtected(state, "hs"));
        Assert.True(IsProtected(state, "pt"));
        Assert.True(IsProtected(state, "pm"));
        Assert.False(IsProtected(state, "plain"));
    }

    [Theory]
    [InlineData(ToolMode.SortInside)]
    [InlineData(ToolMode.Normal)]
    public void NonProtectedModes_LeaveTheToolsModsUnprotected(ToolMode mode)
    {
        var state = StateWith(new ToolPolicy(mode, mode, false),
            Row("hs", "x/hs", hs: true), Row("pt", "Proteus Upscales/pt", role: ProteusRole.Made));

        Assert.False(IsProtected(state, "hs"));
        Assert.False(IsProtected(state, "pt"));
    }

    [Fact]
    public void TheTwoToolsHaveIndependentModes()
    {
        var state = StateWith(new ToolPolicy(ToolMode.Protected, ToolMode.Normal, false),
            Row("hs", "x/hs", hs: true), Row("pt", "p/pt", role: ProteusRole.Made));

        Assert.True(IsProtected(state, "hs"));
        Assert.False(IsProtected(state, "pt"));
    }

    [Fact]
    public void TheManagedMod_IgnoresTheThreeModes_ButFollowsTheRootOption()
    {
        var modes = new ToolPolicy(ToolMode.Normal, ToolMode.Normal, false);
        var atRoot = new ToolPolicy(ToolMode.Protected, ToolMode.Protected, true);

        Assert.True(IsProtected(StateWith(modes, Row("pm", "Proteus", role: ProteusRole.Managed)), "pm"));
        Assert.False(IsProtected(StateWith(atRoot, Row("pm", "Proteus", role: ProteusRole.Managed)), "pm"));
    }

    [Fact]
    public void ChangingThePolicy_RecomputesEveryRowAtOnce()
    {
        var state = StateWith(ToolPolicy.Default, Row("pt", "p/pt", role: ProteusRole.Made));
        Assert.True(IsProtected(state, "pt"));

        state.SetToolPolicy(new ToolPolicy(ToolMode.Protected, ToolMode.SortInside, false));
        Assert.False(IsProtected(state, "pt"));

        state.SetToolPolicy(ToolPolicy.Default);
        Assert.True(IsProtected(state, "pt"));
        Assert.Equal(ToolPolicy.Default, state.ToolPolicy);
    }

    [Fact]
    public void APerModUntick_IsAnOverride_ThatARescanClears_AndTheModeSurvives()
    {
        var state = StateWith(ToolPolicy.Default, Row("pt", "p/pt", role: ProteusRole.Made));

        state.SetProtected("pt", false);
        Assert.False(IsProtected(state, "pt"));

        // an unrelated recompute (a folder rule change) must not re-protect it
        state.SetFolderProtected("somewhere/else", true);
        Assert.False(IsProtected(state, "pt"));

        state.LoadScan([Row("pt", "p/pt", role: ProteusRole.Made)], new HashSet<string>());
        Assert.True(IsProtected(state, "pt"));            // the rescan clears the override
        Assert.Equal(ToolPolicy.Default, state.ToolPolicy); // and keeps the mode
    }

    [Fact]
    public void AnExplicitProtectionOrAProtectedFolder_BeatsTheOverride_ForAProteusMod()
    {
        var state = StateWith(new ToolPolicy(ToolMode.Protected, ToolMode.Normal, false),
            Row("pt", "Tools/pt", role: ProteusRole.Made));

        state.SetProtected("pt", true);
        Assert.True(IsProtected(state, "pt"));

        state.SetProtected("pt", false);
        state.SetFolderProtected("Tools", true);
        Assert.True(IsProtected(state, "pt"));
    }

    [Fact]
    public void SetHeliosphereProtection_StillWorksForHeliosphereMods_AndIgnoresProteusMods()
    {
        var state = StateWith(ToolPolicy.Default,
            Row("hs", "x/hs", hs: true), Row("pt", "p/pt", role: ProteusRole.Made));

        state.SetHeliosphereProtection(false);

        Assert.False(IsProtected(state, "hs"));
        Assert.True(IsProtected(state, "pt"));
    }
}
