namespace PenumbraOrganizer.Plugin.Tests.Organizer.Templates;

using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Templates;

public class TemplateExportFoldersTests
{
    private const string ValidOrganizationJson = """
    {"Version":1,"Folders":{"Gear/Head":{},"Empty/Bucket":{}},"Separators":{}}
    """;

    [Fact]
    public void Seed_UnionsKnownFoldersWithOrganizationJson()
    {
        var seed = TemplateExportFolders.Seed(["Gear/Head", "Characters"], ValidOrganizationJson);

        Assert.Equal(["Characters", "Empty/Bucket", "Gear/Head"], seed.Folders);
        Assert.False(seed.OrganizationJsonUnavailable);
    }

    // The whole reason organization.json is consulted: a folder holding no mods cannot appear in
    // KnownFolders, which is derived from mod paths.
    [Fact]
    public void Seed_EmptyFolderKnownOnlyToPenumbra_Survives()
    {
        var seed = TemplateExportFolders.Seed([], ValidOrganizationJson);

        Assert.Contains("Empty/Bucket", seed.Folders);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{ not json")]
    [InlineData("""{"Version":99,"Folders":{"Gear":{}}}""")]
    public void Seed_UnusableOrganizationJson_DegradesToKnownFoldersAndSaysSo(string? json)
    {
        var seed = TemplateExportFolders.Seed(["Characters"], json);

        Assert.Equal(["Characters"], seed.Folders);
        Assert.True(seed.OrganizationJsonUnavailable);
    }

    [Fact]
    public void Seed_NormalizesAndDeduplicates()
    {
        var seed = TemplateExportFolders.Seed(["/Gear/Head/", "Gear//Head", "Gear/Head"], null);

        Assert.Equal(["Gear/Head"], seed.Folders);
    }
}

public class TemplateExportSelectionTests
{
    private static OrganizerModRow Row(string identifier, string name, string currentPath) => new()
    {
        Identifier = identifier,
        Name = name,
        Author = "Tsar",
        CurrentPath = currentPath,
        ProposedPath = currentPath,
        Category = ModCategory.Gear,
    };

    private static readonly OrganizerModRow[] Library =
    [
        Row("head", "Fancy Hat", "Gear/Head/Fancy Hat"),
        Row("top", "Nice Shirt", "Gear/Top/Nice Shirt"),
        Row("char", "Nyx", "Characters/Nyx"),
        Row("boxed", "Gearbox Thing", "Gearbox/Gearbox Thing"),
    ];

    private static TemplateExportSelection Selection() =>
        new(Library, ["Gear", "Gear/Head", "Gear/Top", "Characters", "Gearbox"]);

    // Starting empty would make export mean "export nothing" for anyone who skims the screen.
    [Fact]
    public void NewSelection_IncludesEverything()
    {
        var selection = Selection();

        Assert.Equal(4, selection.IncludedRowCount);
        Assert.Equal(0, selection.ExcludedRowCount);
        Assert.Equal(5, selection.IncludedFolderCount);
    }

    [Fact]
    public void SetRow_ExcludesOnlyThatRow()
    {
        var selection = Selection();

        selection.SetRow("head", false);

        Assert.False(selection.IsRowIncluded("head"));
        Assert.True(selection.IsRowIncluded("top"));
        Assert.Equal(3, selection.IncludedRowCount);
        Assert.Equal(1, selection.ExcludedRowCount);
    }

    [Fact]
    public void SetFolder_ExcludesDescendantRows()
    {
        var selection = Selection();

        selection.SetFolder("Gear", false);

        Assert.False(selection.IsRowIncluded("head"));
        Assert.False(selection.IsRowIncluded("top"));
        Assert.True(selection.IsRowIncluded("char"));
    }

    // "Gear" must not swallow "Gearbox". Same boundary rule as protected folders.
    [Fact]
    public void SetFolder_DoesNotMatchOnBareStringPrefix()
    {
        var selection = Selection();

        selection.SetFolder("Gear", false);

        Assert.True(selection.IsRowIncluded("boxed"));
    }

    [Fact]
    public void SetFolder_AlsoTogglesTheFolderItself()
    {
        var selection = Selection();

        selection.SetFolder("Characters", false);

        Assert.False(selection.IsFolderIncluded("Characters"));
        Assert.DoesNotContain("Characters", selection.IncludedFolders);
    }

    [Fact]
    public void SetFolder_ReincludingRestoresDescendants()
    {
        var selection = Selection();

        selection.SetFolder("Gear", false);
        selection.SetFolder("Gear", true);

        Assert.True(selection.IsRowIncluded("head"));
        Assert.True(selection.IsRowIncluded("top"));
    }

    [Fact]
    public void SetAllRows_False_ExcludesEverythingButKeepsFolders()
    {
        var selection = Selection();

        selection.SetAllRows(false);

        Assert.Equal(0, selection.IncludedRowCount);
        Assert.Equal(5, selection.IncludedFolderCount);
    }

    [Fact]
    public void SetRow_UnknownIdentifier_IsIgnored()
    {
        var selection = Selection();

        selection.SetRow("nope", false);

        Assert.Equal(4, selection.IncludedRowCount);
    }

    [Fact]
    public void IncludedFolders_AreOrdinalSorted()
    {
        Assert.Equal(
            ["Characters", "Gear", "Gear/Head", "Gear/Top", "Gearbox"],
            Selection().IncludedFolders);
    }

    // The filter narrows what is shown and nothing else. A filtered "exclude all" that dropped
    // hidden rows would be a silent data-loss bug in a privacy screen.
    [Theory]
    [InlineData("Fancy Hat", "fancy", true)]
    [InlineData("Fancy Hat", "HAT", true)]
    [InlineData("Fancy Hat", "  hat  ", true)]
    [InlineData("Fancy Hat", "shirt", false)]
    [InlineData("Fancy Hat", "", true)]
    [InlineData("Fancy Hat", null, true)]
    public void MatchesFilter_IsCaseInsensitiveSubstring(string name, string? query, bool expected)
    {
        Assert.Equal(expected, TemplateExportSelection.MatchesFilter(name, query));
    }

    [Fact]
    public void MatchesFilter_IsNotConsultedByInclusion()
    {
        var selection = Selection();

        // Whatever the UI is filtering by, excluding "everything" excludes every row, not just the
        // visible ones -- and re-including does the same.
        selection.SetAllRows(false);
        selection.SetAllRows(true);

        Assert.Equal(4, selection.IncludedRowCount);
    }
}

public class TemplateShareCodeTests
{
    private static OrganizationTemplate Template(int entryCount) => new()
    {
        FormatVersion = 1,
        Name = "Sized",
        FallbackStrategy = "TypeOnly",
        Entries =
        [
            .. Enumerable.Range(0, entryCount)
                .Select(i => new TemplateEntry($"mod number {i} with a realistic name", $"Gear/Folder{i}")),
        ],
    };

    [Fact]
    public void Describe_SmallTemplate_FitsInAChatMessage()
    {
        var described = TemplateShareCode.Describe(Template(3));

        Assert.StartsWith(TemplateCodec.ShareCodePrefix, described.Code);
        Assert.False(described.ExceedsChatLimit);
        Assert.Equal(described.Code.Length, described.Length);
    }

    [Fact]
    public void Describe_LargeTemplate_ExceedsTheChatLimit()
    {
        Assert.True(TemplateShareCode.Describe(Template(2000)).ExceedsChatLimit);
    }

    [Fact]
    public void ChatMessageLimit_IsDiscordsMessageCap()
    {
        Assert.Equal(2000, TemplateShareCode.ChatMessageLimit);
    }

    // Walks entry counts upward until the flag flips, then checks the flip happened strictly past
    // the limit -- a code of exactly 2000 characters still pastes intact. Asserting the boundary
    // this way rather than restating "Length > Limit" keeps the test from being a tautology that
    // would pass against any comparison operator.
    [Fact]
    public void Describe_FlipsToExceeding_OnlyPastTheLimit()
    {
        TemplateShareCodeDescription? lastFitting = null;
        TemplateShareCodeDescription? firstExceeding = null;

        for (var entries = 1; entries <= 400 && firstExceeding is null; entries++)
        {
            var described = TemplateShareCode.Describe(Template(entries));
            if (described.ExceedsChatLimit)
                firstExceeding = described;
            else
                lastFitting = described;
        }

        Assert.NotNull(lastFitting);
        Assert.NotNull(firstExceeding);
        Assert.True(lastFitting.Length <= TemplateShareCode.ChatMessageLimit);
        Assert.True(firstExceeding.Length > TemplateShareCode.ChatMessageLimit);
    }

    [Fact]
    public void Describe_Output_DecodesBackToTheSameTemplate()
    {
        var described = TemplateShareCode.Describe(Template(3));

        var decoded = TemplateCodec.DecodeShareCode(described.Code);

        Assert.True(decoded.Succeeded);
        Assert.Equal("Sized", decoded.Template!.Name);
        Assert.Equal(3, decoded.Template.EntriesByNormalizedName.Count);
    }
}
