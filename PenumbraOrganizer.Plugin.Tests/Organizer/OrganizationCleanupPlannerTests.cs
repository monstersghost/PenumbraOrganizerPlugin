using System.Text.Json;
using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class OrganizationCleanupPlannerTests
{
    private static OrganizationJson Make(params (string Path, FolderData Data)[] folders)
    {
        var result = new OrganizationJson { Version = 1 };
        foreach (var (path, data) in folders)
            result.Folders[path] = data;
        return result;
    }

    private static IReadOnlySet<string> Occupied(params string[] folders) =>
        folders.ToHashSet(StringComparer.Ordinal);

    // --- GetVirtualParent ---

    [Fact]
    public void GetVirtualParent_RootLevelMod_ReturnsNull()
        => Assert.Null(OrganizationCleanupPlanner.GetVirtualParent("ModName"));

    [Fact]
    public void GetVirtualParent_OneLevel_ReturnsFolder()
        => Assert.Equal("A", OrganizationCleanupPlanner.GetVirtualParent("A/B"));

    [Fact]
    public void GetVirtualParent_TwoLevels_ReturnsFullParentPath()
        => Assert.Equal("A/B", OrganizationCleanupPlanner.GetVirtualParent("A/B/C"));

    [Fact]
    public void GetVirtualParent_TrailingSlash_TrimmedNotOwnPath()
        => Assert.Equal("A", OrganizationCleanupPlanner.GetVirtualParent("A/B/"));

    [Fact]
    public void GetVirtualParent_LeadingSlash_TreatedAsRootLevel()
        => Assert.Null(OrganizationCleanupPlanner.GetVirtualParent("/Mod"));

    // --- DetectOrphaned ---

    [Fact]
    public void DetectOrphaned_OccupiedExactMatch_NotOrphaned()
    {
        var data = Make(("Creators/Alice", new FolderData()));

        var (plain, customized) = OrganizationCleanupPlanner.DetectOrphaned(data, Occupied("Creators/Alice"));

        Assert.Empty(plain);
        Assert.Empty(customized);
    }

    [Fact]
    public void DetectOrphaned_AncestorOfOccupied_NotOrphaned()
    {
        // "Creators" has no mod directly in it, but its descendant does — never prunable.
        var data = Make(("Creators", new FolderData()));

        var (plain, _) = OrganizationCleanupPlanner.DetectOrphaned(data, Occupied("Creators/Alice"));

        Assert.Empty(plain);
    }

    [Fact]
    public void DetectOrphaned_PrefixWithoutSegmentBoundary_IsOrphaned()
    {
        // "Body" must NOT count as ancestor of "BodyMods/Author" — segment boundary required.
        var data = Make(("Body", new FolderData()));

        var (plain, _) = OrganizationCleanupPlanner.DetectOrphaned(data, Occupied("BodyMods/Author"));

        Assert.Equal(["Body"], plain);
    }

    [Fact]
    public void DetectOrphaned_PlainEmpty_AllKnownFieldsNullAndNoExtensionData()
    {
        var data = Make(("Old/Empty", new FolderData()));

        var (plain, customized) = OrganizationCleanupPlanner.DetectOrphaned(data, Occupied());

        Assert.Equal(["Old/Empty"], plain);
        Assert.Empty(customized);
    }

    [Fact]
    public void DetectOrphaned_KnownCustomization_ClassifiesCustomized()
    {
        var data = Make(("Favorites", new FolderData { ExpandedColor = 123, SortMode = "FoldersFirst" }));

        var (plain, customized) = OrganizationCleanupPlanner.DetectOrphaned(data, Occupied());

        Assert.Empty(plain);
        var entry = Assert.Single(customized);
        Assert.Equal("Favorites", entry.Path);
        Assert.Contains("color", entry.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FoldersFirst", entry.Description);
    }

    [Fact]
    public void DetectOrphaned_UnknownFieldsOnly_ClassifiesCustomizedNotPlain()
    {
        // An entry customized only via a field this plugin doesn't know about must get the
        // higher-friction treatment — ExtensionData exists to protect data we can't interpret.
        using var doc = JsonDocument.Parse("true");
        var folder = new FolderData
        {
            ExtensionData = new Dictionary<string, JsonElement> { ["FutureFlag"] = doc.RootElement.Clone() },
        };
        var data = Make(("Mystery", folder));

        var (plain, customized) = OrganizationCleanupPlanner.DetectOrphaned(data, Occupied());

        Assert.Empty(plain);
        var entry = Assert.Single(customized);
        Assert.Equal("Mystery", entry.Path);
    }

    [Fact]
    public void DetectOrphaned_OutputSortedAscendingOrdinal()
    {
        var data = Make(("Zebra", new FolderData()), ("Alpha", new FolderData()));

        var (plain, _) = OrganizationCleanupPlanner.DetectOrphaned(data, Occupied());

        Assert.Equal(["Alpha", "Zebra"], plain);
    }

    // --- NormalizeFolderPath ---

    [Fact]
    public void NormalizeFolderPath_TrimsLeadingAndTrailingSlashes()
        => Assert.Equal("Gear/Feet", OrganizationCleanupPlanner.NormalizeFolderPath("/Gear/Feet/"));

    [Fact]
    public void NormalizeFolderPath_ConvertsBackslashesToForwardSlashes()
        => Assert.Equal("Gear/Feet", OrganizationCleanupPlanner.NormalizeFolderPath(@"Gear\Feet"));

    [Fact]
    public void NormalizeFolderPath_CollapsesRepeatedSeparators()
        => Assert.Equal("Gear/Feet", OrganizationCleanupPlanner.NormalizeFolderPath("Gear//Feet"));

    [Fact]
    public void NormalizeFolderPath_WhitespaceOnly_ReturnsNull()
        => Assert.Null(OrganizationCleanupPlanner.NormalizeFolderPath("   "));

    [Fact]
    public void NormalizeFolderPath_EmptyString_ReturnsNull()
        => Assert.Null(OrganizationCleanupPlanner.NormalizeFolderPath(""));

    [Fact]
    public void NormalizeFolderPath_Null_ReturnsNull()
        => Assert.Null(OrganizationCleanupPlanner.NormalizeFolderPath(null));

    // --- IsUnderAnyProtectedFolder ---

    [Fact]
    public void IsUnderAnyProtectedFolder_ExactFolderMatch_ReturnsTrue()
        => Assert.True(OrganizationCleanupPlanner.IsUnderAnyProtectedFolder(
            "Gear/Feet/Boots", Occupied("Gear/Feet")));

    [Fact]
    public void IsUnderAnyProtectedFolder_NestedUnderProtectedFolder_ReturnsTrue()
        => Assert.True(OrganizationCleanupPlanner.IsUnderAnyProtectedFolder(
            "Gear/Feet/Sub/Boots", Occupied("Gear/Feet")));

    [Fact]
    public void IsUnderAnyProtectedFolder_UnrelatedFolder_ReturnsFalse()
        => Assert.False(OrganizationCleanupPlanner.IsUnderAnyProtectedFolder(
            "Face/Boots", Occupied("Gear/Feet")));

    [Fact]
    public void IsUnderAnyProtectedFolder_BareStartsWithWouldFalseMatch_ButDoesNot()
        => Assert.False(OrganizationCleanupPlanner.IsUnderAnyProtectedFolder(
            "BodyMods/Author/Mod", Occupied("Body")));

    [Fact]
    public void IsUnderAnyProtectedFolder_SiblingWithSharedPrefix_DoesNotMatch()
        => Assert.False(OrganizationCleanupPlanner.IsUnderAnyProtectedFolder(
            "Gear/FeetExtra/Mod", Occupied("Gear/Feet")));

    [Fact]
    public void IsUnderAnyProtectedFolder_RootLevelMod_ReturnsFalse()
        => Assert.False(OrganizationCleanupPlanner.IsUnderAnyProtectedFolder(
            "ModAtRoot", Occupied("Gear")));

    // --- Prune ---

    [Fact]
    public void Prune_RemovesSelectedFoldersOnly()
    {
        var data = Make(("Keep", new FolderData()), ("Remove", new FolderData()));

        var pruned = OrganizationCleanupPlanner.Prune(data, Occupied("Remove"));

        Assert.True(pruned.Folders.ContainsKey("Keep"));
        Assert.False(pruned.Folders.ContainsKey("Remove"));
    }

    [Fact]
    public void Prune_LeavesSeparatorsUntouched()
    {
        var data = Make(("Remove", new FolderData()));
        data.Separators["MySep"] = new SeparatorData { Folder = false, CreationDate = 42 };

        var pruned = OrganizationCleanupPlanner.Prune(data, Occupied("Remove"));

        Assert.Same(data.Separators, pruned.Separators);
    }

    [Fact]
    public void Prune_DoesNotMutateInput()
    {
        var data = Make(("Remove", new FolderData()));

        _ = OrganizationCleanupPlanner.Prune(data, Occupied("Remove"));

        Assert.True(data.Folders.ContainsKey("Remove"));
    }

    [Fact]
    public void Prune_CarriesVersionAndExtensionData()
    {
        using var doc = JsonDocument.Parse("\"kept\"");
        var data = Make(("Remove", new FolderData()));
        data.ExtensionData = new Dictionary<string, JsonElement> { ["FutureTopLevel"] = doc.RootElement.Clone() };

        var pruned = OrganizationCleanupPlanner.Prune(data, Occupied("Remove"));

        Assert.Equal(1, pruned.Version);
        Assert.Same(data.ExtensionData, pruned.ExtensionData);
    }
}
