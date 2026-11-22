namespace PenumbraOrganizer.Plugin.Tests.Organizer;

using PenumbraOrganizer.Core.Models;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Workbook;

public class WorkbookAdapterApplyImportResultTests
{
    private static OrganizerState MakeStateWithOneRow(string identifier, string name, string currentPath, bool initiallyProtected = false)
    {
        var state = new OrganizerState();
        state.LoadScan(
            [new OrganizerModRow { Identifier = identifier, Name = name, Author = "Author", CurrentPath = currentPath, ProposedPath = currentPath }],
            initiallyProtected ? new HashSet<string> { identifier } : new HashSet<string>());
        return state;
    }

    private static WorkbookImportResult MakeResult(params WorkbookImportRow[] rows)
        => new("workbook.xlsx", "export-1", DateTimeOffset.UtcNow, "scan-1", "install-1", rows, [], [], "ok");

    [Fact]
    public void ResolvedDestination_RecombinesWithCurrentNameNotIdentifier()
    {
        var state = MakeStateWithOneRow("Bibo+ Medieval (Penumbra)_1_1_0", "Bibo+ Medieval Dress", "Gear/Bibo+ Medieval Dress");
        var result = MakeResult(new WorkbookImportRow(
            2, "Bibo+ Medieval (Penumbra)_1_1_0", "Bibo+ Medieval Dress", "Tsar", "Gear", "Gear", false, "Tsar/Gear", "Gear", "Tsar/Gear"));

        WorkbookAdapter.ApplyImportResult(state, result);

        Assert.Equal("Tsar/Gear/Bibo+ Medieval Dress", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void NullResolvedDestination_DoesNotChangeProposedPath()
    {
        var state = MakeStateWithOneRow("Foo", "Foo Mod", "Gear/Foo Mod");
        var result = MakeResult(new WorkbookImportRow(
            2, "Foo", "Foo Mod", "Author", "Gear", "Gear", true, "", "Gear", null));

        WorkbookAdapter.ApplyImportResult(state, result);

        Assert.Equal("Gear/Foo Mod", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void ProtectedAppliedUnconditionally_EvenWithNullResolvedDestination()
    {
        var state = MakeStateWithOneRow("Foo", "Foo Mod", "Gear/Foo Mod");
        var result = MakeResult(new WorkbookImportRow(
            2, "Foo", "Foo Mod", "Author", "Gear", "Gear", true, "", "Gear", null));

        WorkbookAdapter.ApplyImportResult(state, result);

        Assert.True(state.Mods.Single().Protected);
    }

    [Fact]
    public void UnresolvedProtectionFalse_UnprotectsAPreviouslyProtectedRow()
    {
        var state = MakeStateWithOneRow("Foo", "Foo Mod", "Gear/Foo Mod", initiallyProtected: true);
        var result = MakeResult(new WorkbookImportRow(
            2, "Foo", "Foo Mod", "Author", "Gear", "Gear", false, "", "Gear", null));

        WorkbookAdapter.ApplyImportResult(state, result);

        Assert.False(state.Mods.Single().Protected);
    }

    [Fact]
    public void UnprotectAndMoveInSameRow_AppliesBothCorrectly()
    {
        var state = MakeStateWithOneRow("Foo", "Foo Mod", "Gear/Foo Mod", initiallyProtected: true);
        var result = MakeResult(new WorkbookImportRow(
            2, "Foo", "Foo Mod", "Author", "Gear", "Gear", false, "Tsar/Gear", "Gear", "Tsar/Gear"));

        WorkbookAdapter.ApplyImportResult(state, result);

        var row = state.Mods.Single();
        Assert.False(row.Protected);
        Assert.Equal("Tsar/Gear/Foo Mod", row.ProposedPath);
    }

    [Fact]
    public void UnknownIdentifier_IsSkippedWithoutThrowing()
    {
        var state = MakeStateWithOneRow("Foo", "Foo Mod", "Gear/Foo Mod");
        var result = MakeResult(new WorkbookImportRow(
            2, "DoesNotExist", "Ghost Mod", "Author", "Gear", "Gear", false, "Gear", "Gear", "Gear"));

        var exception = Record.Exception(() => WorkbookAdapter.ApplyImportResult(state, result));

        Assert.Null(exception);
    }

    [Fact]
    public void AnImportedLeaf_IsUsedInsteadOfTheModName()
    {
        var state = MakeStateWithOneRow("id", "Mod", "Old/Mod");
        var result = MakeResult(new WorkbookImportRow(
            2, "id", "Mod", "a", "Old", "Gear", false, "New", "Gear", "New", Leaf: "Mod (2)"));

        WorkbookAdapter.ApplyImportResult(state, result);

        Assert.Equal("New/Mod (2)", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void ANameWithASlash_IsStagedAsPenumbraWouldStoreIt()
    {
        var state = MakeStateWithOneRow("id", "A/B", "Old/A" + (char)92 + "B");
        var result = MakeResult(new WorkbookImportRow(
            2, "id", "A/B", "a", "Old", "Gear", false, "New", "Gear", "New"));

        WorkbookAdapter.ApplyImportResult(state, result);

        Assert.Equal("New/A" + (char)92 + "B", state.Mods.Single().ProposedPath);
    }
}
