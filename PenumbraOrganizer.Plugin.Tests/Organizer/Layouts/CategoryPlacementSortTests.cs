using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.BodyTypes;
using PenumbraOrganizer.Plugin.Organizer.Classification;
using PenumbraOrganizer.Plugin.Organizer.Layouts;
using PenumbraOrganizer.Plugin.Organizer.Rules;
using PenumbraOrganizer.Plugin.Windows;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Layouts;

public class CategoryPlacementSortTests
{
    private static OrganizerModRow Row(string id, ModCategory? category, string? sub = null, string? body = null,
        BodyPart part = BodyPart.Replacer, IReadOnlySet<string>? emotes = null, bool racialScaling = false,
        RspEvidence? rsp = null) =>
        new()
        {
            Identifier = id, Name = $"Mod {id}", Author = "Ann", CurrentPath = $"Loose/Mod {id}",
            ProposedPath = $"Loose/Mod {id}", Category = category, SubCategory = sub, BodyType = body, BodyPart = part,
            EmoteNames = emotes, IsRacialScaling = racialScaling, Rsp = rsp,
        };

    // The same mod at another current path (CurrentPath is init-only), optionally Heliosphere-managed.
    private static OrganizerModRow At(OrganizerModRow row, string path, bool? heliosphere = null) =>
        new()
        {
            Identifier = row.Identifier, Name = row.Name, Author = row.Author, CurrentPath = path, ProposedPath = path,
            Category = row.Category, SubCategory = row.SubCategory, BodyType = row.BodyType, BodyPart = row.BodyPart,
            EmoteNames = row.EmoteNames, IsRacialScaling = row.IsRacialScaling, Rsp = row.Rsp,
            HeliosphereManaged = heliosphere ?? row.HeliosphereManaged,
        };

    // What the next scan sees after Apply: every mod at the path the sort proposed.
    private static OrganizerModRow[] Applied(OrganizerState state) =>
        [.. state.Mods.Select(m => At(m, m.ProposedPath))];

    private static CategoryPlacement Folders(params (ModCategory Category, string Folder)[] folders) =>
        CategoryPlacement.Create(folders.ToDictionary(f => f.Category, f => f.Folder), null)!;

    private static CategoryPlacement Creators(params (ModCategory Category, CreatorLevel Level)[] levels) =>
        CategoryPlacement.Create(null, levels.ToDictionary(l => l.Category, l => l.Level))!;

    private static Dictionary<string, string> Sort(
        SortStrategy strategy, LayoutOptions options, bool splitGear = true, bool splitGearBody = true,
        bool splitBodyMods = true, params OrganizerModRow[] rows)
    {
        var state = new OrganizerState();
        state.LoadScan(rows, new HashSet<string>());
        state.Sort(strategy, splitGear, splitNpc: true, splitHair: false, name => name, splitGearBody: splitGearBody,
            splitBodyMods: splitBodyMods, options: options);
        return state.Mods.ToDictionary(m => m.Identifier, m => m.ProposedPath);
    }

    [Fact]
    public void Create_ReturnsNullWhenNothingDiffers_SoTheDefaultHasOneShape()
    {
        Assert.Null(CategoryPlacement.Create(null, null));
        Assert.Null(CategoryPlacement.Create(new Dictionary<ModCategory, string> { [ModCategory.Gear] = "Gear" }, null));
        Assert.Null(CategoryPlacement.From(LayoutProfile.Recommended));
        Assert.Null(LayoutProfile.Recommended.Placement);
        Assert.Equal(Folders((ModCategory.Gear, "Kit")), Folders((ModCategory.Gear, "Kit")));
        Assert.NotEqual(Folders((ModCategory.Gear, "Kit")), Folders((ModCategory.Gear, "Kit2")));
    }

    [Fact]
    public void ARenamedFolder_ReplacesOnlyTheRoot_UnderEveryNumberedLayout()
    {
        var paths = Sort(SortStrategy.TypeOnly,
            new LayoutOptions(SharedHandsAndFeet: true, Placement: Folders((ModCategory.Gear, "Equipment/Gear"), (ModCategory.Body, "Bodies"))),
            rows:
            [
                Row("top", ModCategory.Gear, "Top", body: "Bibo+"),
                Row("hands", ModCategory.Gear, "Hands", body: "Bibo+"),
                Row("repl", ModCategory.Body, body: "Bibo+"),
                Row("skel", ModCategory.Body, part: BodyPart.Skeleton),
                Row("face", ModCategory.Face),
            ]);

        Assert.Equal("Equipment/Gear/10. Bibo+/02. Top/Mod top", paths["top"]);
        Assert.Equal("Equipment/Gear/03. Hands/Mod hands", paths["hands"]);
        Assert.Equal("Bodies/10. Bibo+/00. Replacer/Mod repl", paths["repl"]);
        Assert.Equal("Bodies/00. Skeleton/Mod skel", paths["skel"]);
        Assert.Equal("Face/Mod face", paths["face"]);
    }

    [Fact]
    public void Animation_AndVfx_RenameTheirOwnRoots()
    {
        var paths = Sort(SortStrategy.TypeOnly,
            new LayoutOptions(SplitEmotes: true, Placement: Folders((ModCategory.Animation, "Motion"), (ModCategory.VFX, "Effects"))),
            rows:
            [
                Row("dance", ModCategory.Animation, "Emotes", emotes: new HashSet<string> { "Dance" }),
                Row("battle", ModCategory.Animation, "Battle Animation"),
                Row("vfx", ModCategory.VFX, "VFX"),
            ]);

        Assert.Equal("Motion/Emotes/Dance/Mod dance", paths["dance"]);
        Assert.Equal("Motion/Battle Animation/Mod battle", paths["battle"]);
        Assert.Equal("Effects/Mod vfx", paths["vfx"]);
    }

    [Fact]
    public void ARoutedRacialScalingMod_UsesBodysFolder()
    {
        var race = new RaceSplitOptions(true, RaceRows.RacialScaling, true, RacePriority.Empty);
        var paths = Sort(SortStrategy.TypeOnly,
            new LayoutOptions(Race: race, Placement: Folders((ModCategory.Body, "Bodies"), (ModCategory.Gear, "Kit"))),
            rows: [Row("rsp", ModCategory.Gear, racialScaling: true, rsp: new RspEvidence(new HashSet<string> { "Viera" }, true, true))]);

        Assert.Equal("Bodies/03. Racial Scaling/Viera/Mod rsp", paths["rsp"]);
    }

    [Theory]
    [InlineData(CreatorLevel.None, "Gear/02. Top/Mod top")]
    [InlineData(CreatorLevel.BeforeType, "Ann/Gear/02. Top/Mod top")]
    [InlineData(CreatorLevel.AfterType, "Gear/02. Top/Ann/Mod top")]
    public void ACategorysCreatorLevel_OverridesGroupBy_ForThatCategoryOnly(CreatorLevel level, string expected)
    {
        var paths = Sort(SortStrategy.TypeThenCreator, new LayoutOptions(Placement: Creators((ModCategory.Gear, level))),
            rows: [Row("top", ModCategory.Gear, "Top"), Row("npc", ModCategory.NPC, "Bosses")]);

        Assert.Equal(expected, paths["top"]);
        Assert.Equal("NPC/Bosses/Ann/Mod npc", paths["npc"]);
    }

    [Fact]
    public void CreatorOnlyGrouping_IgnoresCreatorLevelsAndFolders()
    {
        var placement = CategoryPlacement.Create(
            new Dictionary<ModCategory, string> { [ModCategory.Gear] = "Kit" },
            new Dictionary<ModCategory, CreatorLevel> { [ModCategory.Gear] = CreatorLevel.None });
        var paths = Sort(SortStrategy.CreatorOnly, new LayoutOptions(Placement: placement), rows: [Row("top", ModCategory.Gear, "Top")]);
        Assert.Equal("Ann/Mod top", paths["top"]);
    }

    [Fact]
    public void AnUnclassifiedMod_FollowsGroupBy()
    {
        var paths = Sort(SortStrategy.TypeThenCreator,
            new LayoutOptions(Placement: Creators((ModCategory.Others, CreatorLevel.None))),
            rows: [Row("u", null)]);
        Assert.Equal("Ann/Mod u", paths["u"]);
    }

    [Fact]
    public void Others_RenamesItsSortRoot()
    {
        var paths = Sort(SortStrategy.TypeOnly, new LayoutOptions(Placement: Folders((ModCategory.Others, "Misc"))),
            rows: [Row("o", ModCategory.Others)]);
        Assert.Equal("Misc/Mod o", paths["o"]);
    }

    [Fact]
    public void ASortInsideAnchor_StaysAboveTheRenamedFolder()
    {
        var state = new OrganizerState();
        state.SetToolPolicy(ToolPolicy.From((int)ToolMode.Protected, (int)ToolMode.Protected, false, ["Mine"]));
        state.LoadScan(
            [new OrganizerModRow { Identifier = "a", Name = "Mod a", Author = "Ann", CurrentPath = "Mine/Mod a",
                ProposedPath = "Mine/Mod a", Category = ModCategory.Gear, SubCategory = "Top" }],
            new HashSet<string>());

        state.Sort(SortStrategy.TypeOnly, true, true, false, name => name,
            options: new LayoutOptions(Placement: Folders((ModCategory.Gear, "Kit"))));

        Assert.Equal("Mine/Kit/Top/Mod a", state.Mods.Single().ProposedPath);
    }

    [Fact]
    public void ATemplateRename_AndAPlacement_CannotBeCombined() =>
        Assert.Throws<ArgumentException>(() => SortFolderSelectors.Select(
            SortStrategy.TypeOnly, true, true, Row("t", ModCategory.Gear, "Top"), renameFolder: f => f,
            options: new LayoutOptions(Placement: Folders((ModCategory.Gear, "Kit")))));

    [Fact]
    public void NoPlacement_IsTheDefaultLayout()
    {
        var rows = () => new[] { Row("top", ModCategory.Gear, "Top", body: "Bibo+"), Row("npc", ModCategory.NPC, "Bosses") };
        Assert.Equal(
            Sort(SortStrategy.TypeThenCreator, new LayoutOptions(), rows: rows()),
            Sort(SortStrategy.TypeThenCreator, new LayoutOptions(Placement: null), rows: rows()));
    }

    [Fact]
    public void TheFingerprint_IsUnchangedWithoutAPlacement_AndSeesEveryFolderAndLevel()
    {
        var plain = new SortSelection(SortStrategy.TypeThenCreator, true, true, false);
        Assert.DoesNotContain("cats=", plain.Fingerprint());

        string Fp(CategoryPlacement? p) => (plain with { Layout = plain.Layout with { Placement = p } }).Fingerprint();
        var kit = Fp(Folders((ModCategory.Gear, "Kit")));
        Assert.StartsWith(plain.Fingerprint() + "|cats=", kit);
        Assert.Equal(kit, Fp(Folders((ModCategory.Gear, "Kit"))));
        Assert.NotEqual(kit, Fp(Folders((ModCategory.Gear, "Kit2"))));
        Assert.NotEqual(kit, Fp(Folders((ModCategory.Hair, "Kit"))));
        Assert.NotEqual(Fp(Creators((ModCategory.Gear, CreatorLevel.None))), Fp(Creators((ModCategory.Gear, CreatorLevel.AfterType))));
        // A separator inside a folder name cannot make two placements read alike.
        Assert.NotEqual(
            Fp(Folders((ModCategory.Gear, "A;2:-:1:B"))),
            Fp(Folders((ModCategory.Gear, "A"), (ModCategory.Weapon, "B"))));
    }

    [Fact]
    public void FromConfig_AndThePanel_CarryTheSamePlacement()
    {
        var config = Configuration.CreateFresh();
        config.SaveLayoutProfile(LayoutProfile.Recommended.With(
            ModCategory.Gear, LayoutProfile.Recommended[ModCategory.Gear] with { Folder = "Equipment/Gear", Creator = CreatorLevel.None }));
        string[] labels = ["Bibo+"];

        var fromConfig = SortSelectionFactory.FromConfig(config, labels);
        var race = RaceSplitOptions.FromConfig(config);
        var panel = SortPanel.BuildSelection(config, fromConfig.Strategy, fromConfig.SplitGear, fromConfig.SplitNpc,
            fromConfig.SplitHair, fromConfig.HairRaceCode, fromConfig.MidlanderFallback, fromConfig.SplitGearBody,
            fromConfig.SplitBodyMods, fromConfig.BodyPriority, fromConfig.Layout.SharedHandsAndFeet,
            fromConfig.Layout.SplitEmotes, race, rootOnly: false);

        Assert.Equal("Equipment/Gear", fromConfig.Layout.Placement!.FolderFor(ModCategory.Gear));
        Assert.Equal(fromConfig.Fingerprint(), panel.Fingerprint());
        Assert.Same(fromConfig.Layout.Placement, SortSelectionFactory.FromConfig(config, labels).Layout.Placement);
    }

    // R8: the staged workbook export stores the fingerprint in one cell, so it must stay small however many
    // rules and however long the folder names are.
    [Fact]
    public void MaximumRulesAndMaximumFolders_GiveAFingerprintUnder2000Characters()
    {
        var entries = Enumerable.Range(0, RuleSet.MaxRules)
            .Select(i => new RuleEntry($"name contains \"{i}{new string('x', 280)}\" -> Rules/{i}", true))
            .ToList();
        var rules = RuleSet.Compile(entries, RaceSplitOptions.Off);
        var folders = LayoutCatalog.Categories.ToDictionary(c => c, c => $"{(int)c}{new string('f', 257)}");
        var placement = CategoryPlacement.Create(folders, LayoutCatalog.Categories.ToDictionary(c => c, _ => CreatorLevel.AfterType));

        var selection = new SortSelection(SortStrategy.TypeThenCreator, true, true, true,
            Layout: new LayoutOptions(Rules: rules, Placement: placement));
        var fingerprint = selection.Fingerprint();

        Assert.True(entries.Sum(e => e.Text.Length) > 25_000);
        Assert.Contains("|cats=", fingerprint);
        Assert.Contains("|rules=", fingerprint);
        Assert.True(fingerprint.Length < 2000, $"Fingerprint is {fingerprint.Length} characters.");
    }

    [Fact]
    public void TheRulesDigest_StillSeesEveryRuleEdit()
    {
        string Fp(params RuleEntry[] entries) => new SortSelection(SortStrategy.TypeOnly, true, true, false,
            Layout: new LayoutOptions(Rules: RuleSet.Compile(entries, RaceSplitOptions.Off))).Fingerprint();

        var one = Fp(new RuleEntry("name contains \"a\" -> A", true));
        Assert.Equal(one, Fp(new RuleEntry("name contains \"a\" -> A", true)));
        Assert.NotEqual(one, Fp(new RuleEntry("name contains \"a\" -> B", true)));
        Assert.NotEqual(one, Fp(new RuleEntry("name contains \"a\" -> A", false)));
    }

    // R3: a renamed category root is one of the sorter's own folders, so a second Sort changes nothing.
    [Fact]
    public void SortingTwice_WithRenamedFolders_ChangesNothing()
    {
        var options = new LayoutOptions(Placement: Folders((ModCategory.Gear, "Kit"), (ModCategory.Body, "Bodies/All")));
        OrganizerState Sorted(OrganizerModRow[] rows)
        {
            var state = new OrganizerState();
            state.SetToolPolicy(new ToolPolicy(ToolMode.SortInside, ToolMode.Protected, false));
            state.LoadScan(rows, new HashSet<string>());
            state.Sort(SortStrategy.TypeOnly, true, true, false, name => name,
                splitGearBody: true, splitBodyMods: true, options: options);
            return state;
        }

        var first = Sorted(
        [
            Row("top", ModCategory.Gear, "Top", body: "Bibo+"),
            At(Row("hs", ModCategory.Gear, "Top", body: "TBSE"), "Mod hs", heliosphere: true),
            Row("repl", ModCategory.Body, body: "Bibo+"),
            Row("face", ModCategory.Face),
        ]);
        Assert.Equal("Kit/11. TBSE/02. Top/Mod hs", first.Mods.Single(m => m.Identifier == "hs").ProposedPath);
        Assert.Equal("Bodies/All/10. Bibo+/00. Replacer/Mod repl", first.Mods.Single(m => m.Identifier == "repl").ProposedPath);

        var second = Sorted(Applied(first));

        Assert.All(second.Mods, m => Assert.Equal(m.CurrentPath, m.ProposedPath));
    }

    [Fact]
    public void AHeliosphereSortInsideRow_UnderTheRenamedGearFolder_IsNotAnchoredToIt()
    {
        var state = new OrganizerState();
        state.SetToolPolicy(new ToolPolicy(ToolMode.SortInside, ToolMode.Protected, false));
        state.LoadScan(
        [
            At(Row("hs", ModCategory.Gear, "Top", body: "TBSE"), "Kit/Top/Mod hs", heliosphere: true),
            Row("plain", ModCategory.Gear, "Top", body: "Bibo+"),
        ], new HashSet<string>());

        state.Sort(SortStrategy.TypeOnly, true, true, false, name => name, splitGearBody: true,
            options: new LayoutOptions(Placement: Folders((ModCategory.Gear, "Kit"))));

        // Kit is the Gear folder now, not the Heliosphere mod's own folder: it is numbered with the other gear.
        var paths = state.Mods.ToDictionary(m => m.Identifier, m => m.ProposedPath);
        Assert.Equal("Kit/11. TBSE/02. Top/Mod hs", paths["hs"]);
        Assert.Equal("Kit/10. Bibo+/02. Top/Mod plain", paths["plain"]);
    }

    [Fact]
    public void AUserSortInsideFolder_AboveTheRenamedGearFolder_SharesOneNumberedLayout()
    {
        var options = new LayoutOptions(Placement: Folders((ModCategory.Gear, "Equipment/Gear")));
        OrganizerState Sorted(OrganizerModRow[] rows)
        {
            var state = new OrganizerState();
            state.SetToolPolicy(ToolPolicy.From((int)ToolMode.Protected, (int)ToolMode.Protected, false, ["Equipment"]));
            state.LoadScan(rows, new HashSet<string>());
            state.Sort(SortStrategy.TypeOnly, true, true, false, name => name, splitGearBody: true, options: options);
            return state;
        }

        var first = Sorted(
        [
            Row("loose", ModCategory.Gear, "Top", body: "Bibo+"),
            At(Row("inside", ModCategory.Gear, "Top", body: "TBSE"), "Equipment/Old/Mod inside"),
            At(Row("hair", ModCategory.Hair), "Equipment/Old/Mod hair"),
        ]);
        var paths = first.Mods.ToDictionary(m => m.Identifier, m => m.ProposedPath);
        // Both sides of the anchor file into the same Equipment/Gear tree, so they take one set of numbers.
        Assert.Equal("Equipment/Gear/10. Bibo+/02. Top/Mod loose", paths["loose"]);
        Assert.Equal("Equipment/Gear/11. TBSE/02. Top/Mod inside", paths["inside"]);
        // A category that is not renamed under the anchor still keeps it.
        Assert.Equal("Equipment/Hair/Mod hair", paths["hair"]);

        var second = Sorted(Applied(first));

        Assert.All(second.Mods, m => Assert.Equal(m.CurrentPath, m.ProposedPath));
    }

    [Fact]
    public void TheProtectTabsSortInsideCandidates_LeaveOutRenamedRoots()
    {
        OrganizerModRow Gear(string id, string path) => At(Row(id, ModCategory.Gear, "Top"), path);

        var rows = new[]
        {
            Gear("a", "Kit/Top/Mod a"), Gear("b", "Equipment/Gear/10. Bibo+/Mod b"), Gear("c", "Equipment/Old/Mod c"),
            Gear("d", "Mine/Mod d"),
        };
        var placement = Folders((ModCategory.Gear, "Equipment/Gear"), (ModCategory.Hair, "kit"));

        var withRenames = SortInsideFolderChoices.Candidates(rows, [], placement);

        Assert.Equal(["Equipment", "Equipment/Old", "Mine"], withRenames);
        Assert.Contains("Kit", SortInsideFolderChoices.Candidates(rows, []));
    }
}
