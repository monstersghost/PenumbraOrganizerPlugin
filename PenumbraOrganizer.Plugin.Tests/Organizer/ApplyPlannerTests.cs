using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class ApplyPlannerTests
{
    private static OrganizerModRow MakeRow(string identifier, string currentPath, string proposedPath) => new()
    {
        Identifier = identifier,
        Name = identifier,
        Author = "SomeAuthor",
        CurrentPath = currentPath,
        ProposedPath = proposedPath,
    };

    [Fact]
    public void BuildBackup_EmptyInput_ReturnsEmpty()
    {
        var result = ApplyPlanner.BuildBackup([]);

        Assert.Empty(result);
    }

    [Fact]
    public void BuildBackup_SingleRow_RecordsPreviousPathFromCurrentPathNotProposedPath()
    {
        var row = MakeRow("Foo", "Old/Foo", "New/Foo");

        var result = ApplyPlanner.BuildBackup([row]);

        var entry = Assert.Single(result);
        Assert.Equal("Foo", entry.Identifier);
        Assert.Equal("Old/Foo", entry.PreviousPath);
    }

    [Fact]
    public void BuildBackup_MultipleRows_SortedAscendingByIdentifier()
    {
        var rowB = MakeRow("Bravo", "Old/B", "New/B");
        var rowA = MakeRow("Alpha", "Old/A", "New/A");

        var result = ApplyPlanner.BuildBackup([rowB, rowA]);

        Assert.Equal(["Alpha", "Bravo"], result.Select(e => e.Identifier));
    }

    [Fact]
    public void BuildBackup_DuplicateIdentifier_DeduplicatesToOneEntry()
    {
        var first = MakeRow("Foo", "Old/Foo", "New/Foo");
        var duplicate = MakeRow("Foo", "Old/Foo", "New/Foo");

        var result = ApplyPlanner.BuildBackup([first, duplicate]);

        Assert.Single(result);
    }

    [Fact]
    public void BlockingIdentifiers_NoIssues_ReturnsEmptySet()
    {
        var validation = new ReviewResult([], new Dictionary<string, List<string>>());

        var result = ApplyPlanner.BlockingIdentifiers(validation);

        Assert.Empty(result);
    }

    [Fact]
    public void BlockingIdentifiers_ProtectedViolationsOnly_ReturnsThoseIdentifiers()
    {
        var validation = new ReviewResult(["Foo", "Bar"], new Dictionary<string, List<string>>());

        var result = ApplyPlanner.BlockingIdentifiers(validation);

        Assert.Equal(new HashSet<string> { "Foo", "Bar" }, result);
    }

    [Fact]
    public void BlockingIdentifiers_PathCollisionsOnly_ReturnsUnionOfAllIdentifiersInEveryGroup()
    {
        var collisions = new Dictionary<string, List<string>>
        {
            ["Creator/Foo"] = ["Foo_2", "Foo_3"],
        };
        var validation = new ReviewResult([], collisions);

        var result = ApplyPlanner.BlockingIdentifiers(validation);

        Assert.Equal(new HashSet<string> { "Foo_2", "Foo_3" }, result);
    }

    [Fact]
    public void BlockingIdentifiers_BothPresent_ReturnsUnionWithoutDuplicates()
    {
        var collisions = new Dictionary<string, List<string>>
        {
            ["Creator/Foo"] = ["Foo_2", "Shared"],
        };
        var validation = new ReviewResult(["Shared", "Bar"], collisions);

        var result = ApplyPlanner.BlockingIdentifiers(validation);

        Assert.Equal(new HashSet<string> { "Foo_2", "Shared", "Bar" }, result);
    }

    [Fact]
    public void Retain_KeepSuccessfulTrue_ReturnsOnlySuccessfulEntries()
    {
        var entries = new List<BackupEntry> { new("Foo", "Old/Foo"), new("Bar", "Old/Bar") };
        var results = new List<ApplyResult> { new("Foo", true, null), new("Bar", false, "ModMissing") };

        var result = ApplyPlanner.Retain(entries, results, keepSuccessful: true);

        var entry = Assert.Single(result);
        Assert.Equal("Foo", entry.Identifier);
    }

    [Fact]
    public void Retain_KeepSuccessfulFalse_ReturnsOnlyFailedEntries()
    {
        var entries = new List<BackupEntry> { new("Foo", "Old/Foo"), new("Bar", "Old/Bar") };
        var results = new List<ApplyResult> { new("Foo", true, null), new("Bar", false, "ModMissing") };

        var result = ApplyPlanner.Retain(entries, results, keepSuccessful: false);

        var entry = Assert.Single(result);
        Assert.Equal("Bar", entry.Identifier);
    }

    [Fact]
    public void Retain_EntryWithNoMatchingResult_ExcludedRegardlessOfKeepSuccessful()
    {
        var entries = new List<BackupEntry> { new("Foo", "Old/Foo") };
        var results = new List<ApplyResult>();

        Assert.Empty(ApplyPlanner.Retain(entries, results, keepSuccessful: true));
        Assert.Empty(ApplyPlanner.Retain(entries, results, keepSuccessful: false));
    }

    [Fact]
    public void Retain_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(ApplyPlanner.Retain([], [], keepSuccessful: true));
    }

    [Fact]
    public void FolderPathCollisions_NoExistingFolders_ReturnsEmpty()
    {
        var row = MakeRow("Foo", "Old/Foo", "Gear/Foo");

        var result = ApplyPlanner.FolderPathCollisions([row], new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.Empty(result);
    }

    [Fact]
    public void FolderPathCollisions_ProposedPathMatchesExistingFolder_ReturnsIdentifier()
    {
        var row = MakeRow("Foo", "Old/Foo", "Gear/Foo");
        var existingFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Gear/Foo" };

        var result = ApplyPlanner.FolderPathCollisions([row], existingFolders);

        Assert.Equal(["Foo"], result);
    }

    [Fact]
    public void FolderPathCollisions_ProposedPathMatchesExistingFolder_CaseInsensitive()
    {
        var row = MakeRow("Foo", "Old/Foo", "Gear/Foo");
        var existingFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "gear/foo" };

        var result = ApplyPlanner.FolderPathCollisions([row], existingFolders);

        Assert.Equal(["Foo"], result);
    }

    [Fact]
    public void FolderPathCollisions_NoMatchingRow_ReturnsEmpty()
    {
        var row = MakeRow("Foo", "Old/Foo", "Gear/Foo");
        var existingFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Gear/UnrelatedFolder" };

        var result = ApplyPlanner.FolderPathCollisions([row], existingFolders);

        Assert.Empty(result);
    }

    [Fact]
    public void FolderPathCollisions_MixOfMatchingAndNonMatching_ReturnsOnlyMatching()
    {
        var colliding = MakeRow("Foo", "Old/Foo", "Gear/Foo");
        var clean = MakeRow("Bar", "Old/Bar", "Gear/Bar");
        var existingFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Gear/Foo" };

        var result = ApplyPlanner.FolderPathCollisions([colliding, clean], existingFolders);

        Assert.Equal(["Foo"], result);
    }

    [Fact]
    public void OrderMovesForApply_EmptyInput_ReturnsEmpty()
    {
        var result = ApplyPlanner.OrderMovesForApply([]);

        Assert.Empty(result);
    }

    [Fact]
    public void OrderMovesForApply_SingleMoveToFreePath_ReturnsThatMoveUnchanged()
    {
        var move = new ModMove("Foo", "Gear/Foo", "Gear/Foo (2)");

        var result = ApplyPlanner.OrderMovesForApply([move]);

        Assert.Equal([new ApplyStep("Foo", "Gear/Foo (2)")], result);
    }

    [Fact]
    public void OrderMovesForApply_ChainEndingAtFreePath_ProcessesInReverseSoTargetsAreVacatedFirst()
    {
        // A wants B's current slot; B wants a fresh, unoccupied path. B must move first.
        var a = new ModMove("A", "P1", "P2");
        var b = new ModMove("B", "P2", "P3");

        var result = ApplyPlanner.OrderMovesForApply([a, b]);

        Assert.Equal([new ApplyStep("B", "P3"), new ApplyStep("A", "P2")], result);
    }

    [Fact]
    public void OrderMovesForApply_TwoWaySwap_BreaksCycleWithTemporaryPath()
    {
        // X and Y each want the other's current slot - a direct swap deadlocks in a single pass.
        var x = new ModMove("X", "P0", "P2");
        var y = new ModMove("Y", "P2", "P0");

        var result = ApplyPlanner.OrderMovesForApply([x, y], _ => "TEMP");

        Assert.Equal(
            [
                new ApplyStep("X", "TEMP"),
                new ApplyStep("Y", "P0"),
                new ApplyStep("X", "P2"),
            ],
            result);
    }

    [Fact]
    public void OrderMovesForApply_ThreeWayRotation_BreaksCycleAndDrainsRemainderInReverse()
    {
        // Reproduces the real "When that face..." failure: X->Y->Z->X.
        var x = new ModMove("X", "P0", "P2");
        var y = new ModMove("Y", "P2", "P3");
        var z = new ModMove("Z", "P3", "P0");

        var result = ApplyPlanner.OrderMovesForApply([x, y, z], _ => "TEMP");

        Assert.Equal(
            [
                new ApplyStep("X", "TEMP"),
                new ApplyStep("Z", "P0"),
                new ApplyStep("Y", "P3"),
                new ApplyStep("X", "P2"),
            ],
            result);
    }

    [Fact]
    public void OrderMovesForApply_IndependentGroups_EachResolvedWithoutCrossInterference()
    {
        var freeChainA = new ModMove("A", "P1", "P2");
        var freeChainB = new ModMove("B", "P2", "P3");
        var swapX = new ModMove("X", "Q0", "Q1");
        var swapY = new ModMove("Y", "Q1", "Q0");

        var result = ApplyPlanner.OrderMovesForApply([freeChainA, freeChainB, swapX, swapY], _ => "TEMP");

        Assert.Equal(5, result.Count);
        Assert.Contains(new ApplyStep("B", "P3"), result);
        Assert.Contains(new ApplyStep("A", "P2"), result);
        Assert.Contains(new ApplyStep("X", "TEMP"), result);
        Assert.Contains(new ApplyStep("Y", "Q0"), result);
        Assert.Contains(new ApplyStep("X", "Q1"), result);
    }

    [Fact]
    public void OrderMovesForApply_DefaultTemporaryPathFactory_ProducesPathDistinctFromEveryRealPath()
    {
        var x = new ModMove("X", "P0", "P2");
        var y = new ModMove("Y", "P2", "P0");

        var result = ApplyPlanner.OrderMovesForApply([x, y]);

        var realPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "P0", "P2" };
        var tempStep = Assert.Single(result, s => s.Identifier == "X" && !realPaths.Contains(s.TargetPath));
        Assert.False(string.IsNullOrWhiteSpace(tempStep.TargetPath));
    }
}
