using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.BodyTypes;
using PenumbraOrganizer.Plugin.Organizer.Classification;
using PenumbraOrganizer.Plugin.Organizer.Layouts;
using PenumbraOrganizer.Plugin.Windows;
using PenumbraOrganizer.Plugin.Windows.Widgets;

namespace PenumbraOrganizer.Plugin.Tests.Windows;

public class ExamplePathsTests
{
    private static readonly string[] Labels = ["Bibo+", "Rue+"];

    private static string Text(ModCategory category, LayoutProfile profile, SortStrategy groupBy, bool race = false) =>
        ExamplePaths.Text(ExamplePaths.For(category, profile[category], groupBy, race));

    [Fact]
    public void Recommended_UnderTheDefaultGroupBy()
    {
        var r = LayoutProfile.Recommended;
        Assert.Equal("Gear/10. Bibo+/02. Top/Ann/Medieval Dress", Text(ModCategory.Gear, r, SortStrategy.TypeThenCreator));
        Assert.Equal("Hair/115/Bizu/Imeris - Solar", Text(ModCategory.Hair, r, SortStrategy.TypeThenCreator));
        Assert.Equal("Body/10. Bibo+/00. Replacer/Ai/Bibo+ Base Install", Text(ModCategory.Body, r, SortStrategy.TypeThenCreator));
        Assert.Equal("NPC/Bosses/Thor/Proto Carbuncle", Text(ModCategory.NPC, r, SortStrategy.TypeThenCreator));
        Assert.Equal("Animation and VFX/Emotes/Dance/Ann/Harvest Dance", Text(ModCategory.Animation, r, SortStrategy.TypeThenCreator));
        Assert.Equal("Weapon/Ann/Gunbreaker Blade", Text(ModCategory.Weapon, r, SortStrategy.TypeThenCreator));
    }

    [Theory]
    [InlineData(SortStrategy.TypeOnly, "Gear/10. Bibo+/02. Top/Medieval Dress")]
    [InlineData(SortStrategy.TypeThenCreator, "Gear/10. Bibo+/02. Top/Ann/Medieval Dress")]
    [InlineData(SortStrategy.CreatorThenType, "Ann/Gear/10. Bibo+/02. Top/Medieval Dress")]
    [InlineData(SortStrategy.CreatorOnly, "Ann/Medieval Dress")]
    public void EachGroupBy_PutsTheCreatorWhereTheSortDoes(SortStrategy groupBy, string expected) =>
        Assert.Equal(expected, Text(ModCategory.Gear, LayoutProfile.Recommended, groupBy));

    [Theory]
    [InlineData(CreatorLevel.None, "Equipment/Gear/10. Bibo+/02. Top/Medieval Dress")]
    [InlineData(CreatorLevel.BeforeType, "Ann/Equipment/Gear/10. Bibo+/02. Top/Medieval Dress")]
    [InlineData(CreatorLevel.AfterType, "Equipment/Gear/10. Bibo+/02. Top/Ann/Medieval Dress")]
    public void ACustomisedCategory_ShowsItsFolderAndCreatorLevel(CreatorLevel level, string expected)
    {
        var profile = LayoutProfile.Recommended.With(ModCategory.Gear,
            LayoutProfile.Recommended[ModCategory.Gear] with { Folder = "Equipment/Gear", Creator = level });
        Assert.Equal(expected, Text(ModCategory.Gear, profile, SortStrategy.TypeOnly));
    }

    [Fact]
    public void RaceShowsOnlyWhileTheMasterSwitchIsOn()
    {
        var r = LayoutProfile.Recommended;
        Assert.Equal("Hair/115/Imeris - Solar", Text(ModCategory.Hair, r, SortStrategy.TypeOnly, race: false));
        Assert.Equal("Hair/Viera/115/Imeris - Solar", Text(ModCategory.Hair, r, SortStrategy.TypeOnly, race: true));
        Assert.Equal("Face/Viera/Soft Glow Face", Text(ModCategory.Face, r, SortStrategy.TypeOnly, race: true));
        Assert.Equal("Skin/Akako Skin", Text(ModCategory.Skin, r, SortStrategy.TypeOnly, race: false));
    }

    [Fact]
    public void TheRolesColourTheCreatorFolderSplitsAndMod()
    {
        var segments = ExamplePaths.For(ModCategory.NPC, LayoutProfile.Recommended[ModCategory.NPC],
            SortStrategy.CreatorThenType, raceEnabled: false);
        Assert.Equal([PathRole.Creator, PathRole.Folder, PathRole.Split, PathRole.Mod], segments.Select(s => s.Role));
    }

    [Theory]
    [InlineData(SortStrategy.TypeOnly, null, CreatorLevel.None)]
    [InlineData(SortStrategy.TypeThenCreator, null, CreatorLevel.AfterType)]
    [InlineData(SortStrategy.CreatorThenType, null, CreatorLevel.BeforeType)]
    [InlineData(SortStrategy.CreatorOnly, null, null)]
    [InlineData(SortStrategy.TypeThenCreator, CreatorLevel.None, CreatorLevel.None)]
    [InlineData(SortStrategy.CreatorOnly, CreatorLevel.None, null)]
    public void EffectiveCreator_IsTheCategorysOwn_ElseGroupBys(SortStrategy groupBy, CreatorLevel? own, CreatorLevel? expected) =>
        Assert.Equal(expected, ExamplePaths.EffectiveCreator(
            LayoutProfile.Recommended[ModCategory.Gear] with { Creator = own }, groupBy));

    // ---- the example agrees with a real Sort through the headless pipeline

    private static RowRaceData Keys(string[]? face = null, string[]? skin = null) =>
        new(new HashSet<string>(face ?? []), new HashSet<string>(), new HashSet<string>(skin ?? []), new HashSet<string>(), false);

    private static OrganizerModRow Row(string id, ModCategory category, string name, string author, string? sub = null,
        string? body = null, IReadOnlySet<int>? hairFlat = null, Dictionary<string, IReadOnlySet<int>>? hairByRace = null,
        RowRaceData? keys = null, IReadOnlySet<string>? emotes = null) => new()
    {
        Identifier = id, Name = name, Author = author, CurrentPath = $"Loose/{name}", ProposedPath = $"Loose/{name}",
        Category = category, SubCategory = sub, BodyType = body, BodyPart = BodyPart.Replacer, HairFlat = hairFlat,
        HairByRaceCode = hairByRace, KeyRaces = keys, EmoteNames = emotes,
    };

    private static List<OrganizerModRow> Rows() =>
    [
        Row("gear", ModCategory.Gear, "Medieval Dress", "Ann", "Top", body: "Bibo+"),
        Row("hair", ModCategory.Hair, "Imeris - Solar", "Bizu", hairFlat: new HashSet<int> { 115 },
            hairByRace: new() { ["1801"] = new HashSet<int> { 115 } }),
        Row("body", ModCategory.Body, "Bibo+ Base Install", "Ai", body: "Bibo+"),
        Row("npc", ModCategory.NPC, "Proto Carbuncle", "Thor", "Bosses"),
        Row("face", ModCategory.Face, "Soft Glow Face", "Ann", keys: Keys(face: ["Viera"])),
        Row("skin", ModCategory.Skin, "Akako Skin", "Ann", keys: Keys(skin: ["Miqo'te"])),
        Row("anim", ModCategory.Animation, "Harvest Dance", "Ann", "Emotes", emotes: new HashSet<string> { "Dance" }),
        Row("weapon", ModCategory.Weapon, "Gunbreaker Blade", "Ann"),
    ];

    private static readonly (string Id, ModCategory Category)[] Checked =
    [
        ("gear", ModCategory.Gear), ("hair", ModCategory.Hair), ("body", ModCategory.Body), ("npc", ModCategory.NPC),
        ("face", ModCategory.Face), ("skin", ModCategory.Skin), ("anim", ModCategory.Animation), ("weapon", ModCategory.Weapon),
    ];

    private static LayoutProfile ProfileNamed(string name) => name switch
    {
        "recommended" => LayoutProfile.Recommended,
        "flat" => LayoutCatalog.Categories.Aggregate(LayoutProfile.Recommended,
            (p, c) => p.With(c, p[c] with { Splits = LayoutSplits.None })),
        "slot-only" => LayoutProfile.Recommended.With(ModCategory.Gear,
            LayoutProfile.Recommended[ModCategory.Gear] with { Splits = LayoutSplits.Slot }),
        "body-only" => LayoutProfile.Recommended.With(ModCategory.Gear,
            LayoutProfile.Recommended[ModCategory.Gear] with { Splits = LayoutSplits.BodyType }),
        "renamed" => LayoutProfile.Recommended
            .With(ModCategory.Gear, new CategoryLayout("Equipment/Gear", CreatorLevel.BeforeType, LayoutSplits.BodyType | LayoutSplits.Slot))
            .With(ModCategory.NPC, new CategoryLayout("People", CreatorLevel.None, LayoutSplits.NpcKind)),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [InlineData("recommended")]
    [InlineData("flat")]
    [InlineData("slot-only")]
    [InlineData("body-only")]
    [InlineData("renamed")]
    public void TheExample_IsWhereTheSortPutsTheSameMod(string profileName)
    {
        var profile = ProfileNamed(profileName);
        foreach (var (strategy, _) in SortPanel.Groupings)
        foreach (var race in new[] { false, true })
        {
            var config = Configuration.CreateFresh();
            config.SaveLayoutProfile(profile);
            config.SortGrouping = strategy.ToString();
            config.RaceSplitEnabled = race;
            config.SyncLegacyRaceFields();

            var state = new OrganizerState();
            state.LoadScan(Rows(), new HashSet<string>(), bodyLabels: Labels);
            SortRunner.Run(state, SortSelectionFactory.FromConfig(config, Labels), name => name, scope: null);

            foreach (var (id, category) in Checked)
            {
                var sorted = state.Mods.Single(m => m.Identifier == id).ProposedPath;
                Assert.True(sorted == Text(category, profile, strategy, race),
                    $"{profileName}/{strategy}/race={race}/{category}: example {Text(category, profile, strategy, race)}, sort {sorted}");
            }
        }
    }
}
