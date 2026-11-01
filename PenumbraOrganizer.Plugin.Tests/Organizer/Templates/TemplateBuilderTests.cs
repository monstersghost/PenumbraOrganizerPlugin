namespace PenumbraOrganizer.Plugin.Tests.Organizer.Templates;

using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Templates;

public class TemplateBuilderTests
{
    private static OrganizerModRow Row(
        string identifier, string name, string currentPath, string? proposedPath = null) => new()
    {
        Identifier = identifier,
        Name = name,
        Author = "Tsar",
        CurrentPath = currentPath,
        ProposedPath = proposedPath ?? currentPath,
        Category = ModCategory.Gear,
    };

    private static TemplateMetadata Metadata(
        string name = "My layout",
        Dictionary<string, string>? labels = null) => new(
            name, "Akako", "Notes",
            new TemplateFallback(SortStrategy.TypeOnly, SplitGear: false, SplitNpc: true),
            labels ?? new Dictionary<string, string>());

    private static TemplateBuildResult Build(
        IReadOnlyCollection<OrganizerModRow> rows,
        IReadOnlySet<string>? included = null,
        IReadOnlyCollection<string>? folders = null,
        TemplateMetadata? metadata = null) =>
        TemplateBuilder.Build(
            rows,
            included ?? rows.Select(r => r.Identifier).ToHashSet(StringComparer.Ordinal),
            folders ?? [],
            metadata ?? Metadata(),
            createdWithVersion: "0.6.0.0",
            createdAtUtc: new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Build_IncludedRow_BecomesEntryKeyedOnNormalizedName()
    {
        var result = Build([Row("id1", "Bibo+ Medieval (Penumbra)_1_1_0", "Gear/Top/Bibo+ Medieval")]);

        var entry = Assert.Single(result.Document.Entries);
        Assert.Equal("bibo+ medieval", entry.N);
        Assert.Equal("Gear/Top", entry.F);
    }

    [Fact]
    public void Build_ExcludedRow_IsAbsentFromEntries()
    {
        var rows = new[]
        {
            Row("id1", "Kept", "Gear/Top/Kept"),
            Row("id2", "Dropped", "Gear/Top/Dropped"),
        };

        var result = Build(rows, included: new HashSet<string>(["id1"], StringComparer.Ordinal));

        Assert.Equal("kept", Assert.Single(result.Document.Entries).N);
    }

    // The spec's named trap: a user could sort, review a new structure, export, and unknowingly
    // share the OLD layout. Export must read the applied organization, never the pending proposal.
    [Fact]
    public void Build_UsesCurrentPath_NotProposedPath()
    {
        var result = Build([Row("id1", "Mod", "Applied/Folder/Mod", proposedPath: "Proposed/Folder/Mod")]);

        Assert.Equal("Applied/Folder", Assert.Single(result.Document.Entries).F);
    }

    // The format's destination is folder-only and rejects an empty folder on import, so a mod at
    // the library root carries no shareable folder. Counted rather than warned per row: a library
    // with 200 root mods would otherwise produce 200 warnings.
    [Fact]
    public void Build_RootLevelRow_IsSkippedAndCounted()
    {
        var result = Build([Row("id1", "Loose Mod", "Loose Mod")]);

        Assert.Empty(result.Document.Entries);
        Assert.Equal(1, result.RootLevelSkipped);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Build_RowNormalizingToEmptyKey_IsSkipped()
    {
        var result = Build([Row("id1", "_1_0", "Gear/Top/_1_0")]);

        Assert.Empty(result.Document.Entries);
    }

    // Two included rows normalizing to one key would collapse into a single entry, silently
    // dropping the other. Same rule the import side applies to a conflicting duplicate: omit the
    // whole group rather than pick a winner.
    [Fact]
    public void Build_CollidingNormalizedNames_OmitsGroupAndWarnsOnce()
    {
        var rows = new[]
        {
            Row("id1", "Fancy Hat_1_0", "Gear/Head/Fancy Hat"),
            Row("id2", "Fancy Hat (Penumbra)", "Gear/Top/Fancy Hat"),
        };

        var result = Build(rows);

        Assert.Empty(result.Document.Entries);
        Assert.Equal(
            new TemplateWarning(TemplateWarningCode.ExportNameCollision, "fancy hat"),
            Assert.Single(result.Warnings));
    }

    // Two rows that normalize alike AND already sit in the same folder are not a conflict: the
    // entry they would produce is identical either way, so it is emitted.
    [Fact]
    public void Build_CollidingNamesInSameFolder_EmitsOneEntryWithoutWarning()
    {
        var rows = new[]
        {
            Row("id1", "Fancy Hat_1_0", "Gear/Head/Fancy Hat"),
            Row("id2", "Fancy Hat (Penumbra)", "Gear/Head/Fancy Hat (2)"),
        };

        var result = Build(rows);

        Assert.Equal("Gear/Head", Assert.Single(result.Document.Entries).F);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Build_MultipleCollisions_WarnInOrdinalKeyOrder()
    {
        var rows = new[]
        {
            Row("id1", "Zebra", "A/Zebra"),
            Row("id2", "Zebra (Penumbra)", "B/Zebra"),
            Row("id3", "Alpha", "A/Alpha"),
            Row("id4", "Alpha (Penumbra)", "B/Alpha"),
        };

        var result = Build(rows);

        Assert.Equal(["alpha", "zebra"], result.Warnings.Select(w => w.Subject));
    }

    [Fact]
    public void Build_EntriesAreOrderedByKey()
    {
        var rows = new[]
        {
            Row("id1", "Zebra", "Gear/Zebra"),
            Row("id2", "Alpha", "Gear/Alpha"),
        };

        Assert.Equal(["alpha", "zebra"], Build(rows).Document.Entries.Select(e => e.N));
    }

    [Fact]
    public void Build_IncludedFolders_ArePassedThroughSortedAndDeduplicated()
    {
        var result = Build([], folders: ["Gear/Top", "Characters", "Gear/Top"]);

        Assert.Equal(["Characters", "Gear/Top"], result.Document.Folders);
    }

    [Fact]
    public void Build_MapsMetadataAndProvenance()
    {
        var result = Build(
            [],
            metadata: Metadata("Akako's layout", new Dictionary<string, string> { ["Others"] = "_Unsorted" }));

        Assert.Equal(TemplateCodec.SupportedFormatVersion, result.Document.FormatVersion);
        Assert.Equal("Akako's layout", result.Document.Name);
        Assert.Equal("Akako", result.Document.Author);
        Assert.Equal("Notes", result.Document.Description);
        Assert.Equal("TypeOnly", result.Document.FallbackStrategy);
        Assert.False(result.Document.FallbackSplitGear);
        Assert.True(result.Document.FallbackSplitNpc);
        Assert.Equal("_Unsorted", result.Document.FolderLabels["Others"]);
        Assert.Equal("0.6.0.0", result.Document.CreatedWithVersion);
        Assert.Equal("2026-08-11T12:00:00Z", result.Document.CreatedAtUtc);
    }

    // Whatever the screen produces has to survive the importer, including the empty case an author
    // reaches by excluding everything.
    [Fact]
    public void Build_ExcludingEveryRow_StillProducesADecodableTemplate()
    {
        var result = Build(
            [Row("id1", "Mod", "Gear/Top/Mod")],
            included: new HashSet<string>(StringComparer.Ordinal));

        var decoded = TemplateCodec.DecodeJson(TemplateCodec.EncodeJson(result.Document));

        Assert.True(decoded.Succeeded);
        Assert.Empty(decoded.Template!.EntriesByNormalizedName);
    }

    [Fact]
    public void Build_Output_RoundTripsThroughTheCodec()
    {
        var result = Build(
            [Row("id1", "Bibo+ Medieval", "Gear/Top/Bibo+ Medieval")],
            folders: ["Gear/Top"],
            metadata: Metadata(labels: new Dictionary<string, string> { ["Gear"] = "Equipment" }));

        var decoded = TemplateCodec.DecodeJson(TemplateCodec.EncodeJson(result.Document));

        Assert.True(decoded.Succeeded);
        Assert.Equal("Gear/Top", decoded.Template!.EntriesByNormalizedName["bibo+ medieval"]);
        Assert.Equal("Equipment", decoded.Template.FolderLabels["Gear"]);
        Assert.Equal(
            new TemplateFallback(SortStrategy.TypeOnly, SplitGear: false, SplitNpc: true),
            decoded.Template.Fallback);
    }

    // A protected row is still part of the author's applied organization, so it is exportable. This
    // is the opposite of the apply direction, where protection means "do not move me".
    [Fact]
    public void Build_ProtectedRow_IsExportableWhenIncluded()
    {
        var row = Row("id1", "Guarded", "Gear/Top/Guarded");
        row.Protected = true;

        Assert.Equal("guarded", Assert.Single(Build([row]).Document.Entries).N);
    }
}
