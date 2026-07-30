namespace PenumbraOrganizer.Plugin.Tests.Organizer.Templates;

using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Templates;

/// <summary>
/// The end-to-end claim this feature rests on: a document authored against one library places
/// mods correctly in a different library, with no identity binding of any kind (unlike the
/// workbook, which hard-errors across installs).
/// </summary>
public class TemplateInteropTests
{
    private static OrganizerModRow Row(
        string identifier, string name, ModCategory? category, string? subCategory = null,
        string author = "Tsar") => new()
    {
        Identifier = identifier,
        Name = name,
        Author = author,
        CurrentPath = name,
        ProposedPath = name,
        Category = category,
        SubCategory = subCategory,
    };

    private static string Same(string value) => value;

    // The author's library: mods sit in hand-made folders that no strategy would generate.
    private static OrganizationTemplate AuthorTemplate() => new()
    {
        FormatVersion = 1,
        Name = "Akako's layout",
        Author = "Akako",
        FallbackStrategy = "ModTypeDetailed",
        FolderLabels = new Dictionary<string, string> { ["Others"] = "_Unsorted" },
        Folders = ["Characters/Nyx", "Gear/Head", "_Unsorted"],
        Entries =
        [
            new TemplateEntry("bibo+ medieval", "Characters/Nyx"),
            new TemplateEntry("fancy hat", "Gear/Head"),
            new TemplateEntry("mod the importer does not own", "Characters/Nyx"),
        ],
    };

    [Fact]
    public void ShareCode_FromOneLibrary_PlacesMods_InAnother()
    {
        var code = TemplateCodec.EncodeShareCode(AuthorTemplate());

        // A different library: one shared mod under a different install suffix, one shared mod
        // named identically, one mod the author has never heard of, one unclassified mod.
        var state = new OrganizerState();
        state.LoadScan(
            [
                Row("dir_a_1_1_0", "Bibo+ Medieval (Penumbra)_1_1_0", ModCategory.Gear, "Top"),
                Row("dir_b", "Fancy Hat", ModCategory.Gear, "Head"),
                Row("dir_c", "Importer Only Mod", ModCategory.Hair),
                Row("dir_d", "Mystery", category: null),
            ],
            new HashSet<string>());

        var decoded = TemplateCodec.DecodeShareCode(code);
        Assert.True(decoded.Succeeded);

        var plan = TemplatePlanner.Plan(decoded.Template!, state.Mods, Same, decoded.Warnings);
        var report = state.ApplyTemplate(plan);

        var byIdentifier = state.Mods.ToDictionary(m => m.Identifier, m => m.ProposedPath);

        // Matched despite the install suffix the importer's copy carries.
        Assert.Equal("Characters/Nyx/Bibo+ Medieval (Penumbra)_1_1_0", byIdentifier["dir_a_1_1_0"]);
        Assert.Equal("Gear/Head/Fancy Hat", byIdentifier["dir_b"]);

        // Unmatched: placed by the declared fallback strategy, not left behind.
        Assert.Equal("Hair/Importer Only Mod", byIdentifier["dir_c"]);
        Assert.Equal("Review/Mystery", byIdentifier["dir_d"]);

        Assert.Equal(2, report.RowsMatchedByEntry);
        Assert.Equal(2, report.RowsPlacedByFallback);
        Assert.Equal(1, report.TemplateEntriesUnmatched);
    }

    [Fact]
    public void JsonFile_FromOneLibrary_ProducesTheSameResultAsTheShareCode()
    {
        var template = AuthorTemplate();
        OrganizerModRow[] Rows() =>
        [
            Row("dir_a", "Bibo+ Medieval", ModCategory.Gear, "Top"),
            Row("dir_c", "Importer Only Mod", ModCategory.Hair),
        ];

        string ApplyVia(TemplateDecodeResult decoded)
        {
            var state = new OrganizerState();
            state.LoadScan(Rows(), new HashSet<string>());
            state.ApplyTemplate(TemplatePlanner.Plan(decoded.Template!, state.Mods, Same, decoded.Warnings));
            return string.Join('|', state.Mods.Select(m => $"{m.Identifier}={m.ProposedPath}"));
        }

        Assert.Equal(
            ApplyVia(TemplateCodec.DecodeJson(TemplateCodec.EncodeJson(template))),
            ApplyVia(TemplateCodec.DecodeShareCode(TemplateCodec.EncodeShareCode(template))));
    }

    // Narrow by design: this guards against the two identity fields the workbook format carries
    // being reintroduced by name. It is NOT by itself proof that templates are portable -- that
    // is what the two placement tests above establish, by actually placing mods across two
    // different libraries. The decode assertion below is the part that checks behavior: a
    // template must decode fully on its own, with no library, no installation, and no
    // OrganizerState in scope for it to be validated against.
    [Fact]
    public void Template_IsNotBoundToAnyInstallation()
    {
        var json = TemplateCodec.EncodeJson(AuthorTemplate());

        Assert.DoesNotContain("installationIdentity", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("scanIdentity", json, StringComparison.OrdinalIgnoreCase);

        // No OrganizerState, no mod library, nothing installation-specific is available here --
        // if decoding ever came to depend on the importing library, this would stop compiling or
        // stop succeeding.
        var decoded = TemplateCodec.DecodeJson(json);

        Assert.True(decoded.Succeeded);
        Assert.Equal("Akako's layout", decoded.Template!.Name);
        Assert.Equal(3, decoded.Template.EntriesByNormalizedName.Count);
    }
}
