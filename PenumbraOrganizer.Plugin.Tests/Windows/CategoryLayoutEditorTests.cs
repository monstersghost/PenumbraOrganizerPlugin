using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Layouts;
using PenumbraOrganizer.Plugin.Organizer.Rules;
using PenumbraOrganizer.Plugin.Windows;

namespace PenumbraOrganizer.Plugin.Tests.Windows;

public class CategoryLayoutEditorTests
{
    private readonly List<LayoutProfile> _saved = [];

    private CategoryLayoutEditor Editor(LayoutProfile? start = null) => new(start ?? LayoutProfile.Recommended, _saved.Add);

    private static LayoutProfile Customised(params ModCategory[] categories) =>
        categories.Aggregate(LayoutProfile.Recommended,
            (p, c) => p.With(c, p[c] with { Folder = $"Custom/{c}" }));

    // ---- opening

    [Fact]
    public void OpensOnRecommended_WhenNothingIsCustomised_AndOnAdvancedOtherwise()
    {
        Assert.Equal(SortTabMode.Recommended, CategoryLayoutEditor.InitialMode(LayoutProfile.Recommended));
        Assert.Equal(SortTabMode.Advanced, CategoryLayoutEditor.InitialMode(Customised(ModCategory.Hair)));
    }

    [Fact]
    public void SelectsTheFirstCustomCategory_ElseGear()
    {
        Assert.Equal(ModCategory.Gear, Editor().Selected);
        var editor = Editor(Customised(ModCategory.NPC, ModCategory.Hair));
        Assert.Equal(ModCategory.Hair, editor.Selected); // Hair = 4 comes before NPC = 7
        Assert.Equal("Custom/Hair", editor.FolderText);
    }

    // ---- custom detection and counts

    [Fact]
    public void CustomDetection_FollowsEveryKindOfChange()
    {
        var editor = Editor();
        Assert.Equal(0, editor.CustomCount);
        Assert.Null(editor.RecommendedNote);

        editor.SetSplit(LayoutSplits.Slot, false);
        Assert.True(editor.IsCustom(ModCategory.Gear));
        Assert.Equal("1 category customised", editor.RecommendedNote);

        editor.Select(ModCategory.NPC);
        editor.SetCreator(CreatorLevel.None, SortStrategy.TypeThenCreator);
        editor.Select(ModCategory.Hair);
        editor.SetFolderText("Looks/Hair");
        editor.Commit();

        Assert.Equal(3, editor.CustomCount);
        Assert.Equal("3 categories customised", editor.RecommendedNote);
        Assert.Equal("3 of 16 categories customised", editor.AdvancedSummary);
        Assert.False(editor.IsCustom(ModCategory.Body));
    }

    // ---- the Recommended screen's rules note (Rules is edited in Advanced only)

    private static RuleSet Rules(params (string Text, bool Enabled)[] rules) =>
        RuleSet.Compile(
            [.. rules.Select(r => new RuleEntry(r.Text, r.Enabled))], RaceSplitOptions.Off);

    [Fact]
    public void RecommendedRulesNote_IsNull_WhenNoRuleCanRun()
    {
        Assert.Null(CategoryLayoutEditor.RecommendedRulesNote(null));
        Assert.Null(CategoryLayoutEditor.RecommendedRulesNote(RuleSet.Empty));
        // A switched-off rule and a rule with a mistake never change a path, so they are not "active".
        Assert.Null(CategoryLayoutEditor.RecommendedRulesNote(
            Rules(("name contains 'a' -> A", false), ("nmae = 'x' -> B", true))));
    }

    [Fact]
    public void RecommendedRulesNote_CountsTheRulesThatRun()
    {
        Assert.Equal("1 rule active, edit it in Advanced",
            CategoryLayoutEditor.RecommendedRulesNote(Rules(("name contains 'a' -> A", true))));
        Assert.Equal("2 rules active, edit them in Advanced",
            CategoryLayoutEditor.RecommendedRulesNote(Rules(
                ("name contains 'a' -> A", true),
                ("name contains 'b' -> B", false),
                ("nmae = 'x' -> C", true),
                ("author = 'Ann' -> Ann", true))));
    }

    // ---- persist on change, never per frame

    [Fact]
    public void EachDiscreteChange_SavesOnce_AndReadingSavesNothing()
    {
        var editor = Editor();
        _ = editor.Splits(SortStrategy.TypeThenCreator, false, true);
        _ = editor.Example(SortStrategy.TypeThenCreator, false);
        _ = editor.CustomCount;
        Assert.False(editor.Commit());
        Assert.Empty(_saved);

        editor.SetSplit(LayoutSplits.Slot, false);
        Assert.Single(_saved);
        editor.SetSplit(LayoutSplits.Slot, false); // no change, no save
        Assert.Single(_saved);
        Assert.Equal(editor.Profile, _saved[^1]);
    }

    [Fact]
    public void FolderTyping_IsSavedOnCommit_NotPerKeystroke()
    {
        var editor = Editor();
        foreach (var text in new[] { "E", "Eq", "Equipment", "Equipment/", "Equipment/Gear" })
            editor.SetFolderText(text);

        Assert.Empty(_saved);
        Assert.Equal("Equipment/Gear", editor.Layout.Folder); // the example already follows
        Assert.True(editor.Commit());
        Assert.Equal("Equipment/Gear", Assert.Single(_saved)[ModCategory.Gear].Folder);
        Assert.False(editor.Commit());
    }

    [Fact]
    public void ChangingCategory_SavesAPendingFolderFirst()
    {
        var editor = Editor();
        editor.SetFolderText("Kit");
        editor.Select(ModCategory.Hair);
        Assert.Equal("Kit", Assert.Single(_saved)[ModCategory.Gear].Folder);
        Assert.Equal("Hair", editor.FolderText);
    }

    // ---- folder validation

    [Theory]
    [InlineData("", LayoutFolderName.EmptyError)]
    [InlineData("Equipment//Gear", "Destination cannot contain empty segments, . or ..")]
    [InlineData("../Gear", "Destination cannot contain empty segments, . or ..")]
    [InlineData("C:/Gear", "Use a relative Penumbra folder path.")]
    [InlineData("Gear/{creator}", LayoutFolderName.BracesError)]
    public void AnInvalidFolder_ShowsWhy_AndIsNeverSaved(string text, string error)
    {
        var editor = Editor();
        editor.SetFolderText(text);

        Assert.Equal(error, editor.FolderError);
        Assert.Equal(text, editor.FolderText);
        Assert.Equal("Gear", editor.Layout.Folder);
        Assert.False(editor.Commit());
        Assert.Empty(_saved);
    }

    [Fact]
    public void AFolderLongerThan260_IsRejected()
    {
        var editor = Editor();
        editor.SetFolderText(new string('a', 261));
        Assert.Equal("Destination is longer than 260 characters.", editor.FolderError);
        Assert.Equal("Gear", editor.Layout.Folder);
    }

    [Fact]
    public void AValidFolder_IsNormalised_AndTheErrorClears()
    {
        var editor = Editor();
        editor.SetFolderText("../x");
        editor.SetFolderText(@" Equipment\Gear ");
        Assert.Null(editor.FolderError);
        Assert.Equal("Equipment/Gear", editor.Layout.Folder);
    }

    // ---- folder warnings

    [Fact]
    public void FolderWarning_SameFolderAsAnotherCategory()
    {
        var editor = Editor();
        editor.SetFolderText("Hair");
        Assert.Equal("Hair also uses this folder, so those mods share it.",
            editor.FolderWarning(_ => false, _ => false));

        editor.SetFolderText("hair"); // Penumbra folders ignore case
        Assert.NotNull(editor.FolderWarning(_ => false, _ => false));
    }

    [Fact]
    public void FolderWarning_InsideAProtectedOrSortInsideFolder()
    {
        var editor = Editor();
        editor.SetFolderText("Mine/Gear");
        Assert.Equal("This folder is inside a protected folder, so mods filed there will be treated as protected.",
            editor.FolderWarning(f => f.StartsWith("Mine", StringComparison.Ordinal), _ => false));
        Assert.Equal("This folder is inside a sort-inside folder, so mods already there are sorted inside it.",
            editor.FolderWarning(_ => false, f => CategoryLayoutEditor.IsInside(f, "mine")));
        Assert.Null(editor.FolderWarning(_ => false, _ => false));
    }

    [Theory]
    [InlineData("Mine", "Mine", true)]
    [InlineData("Mine/Gear", "mine", true)]
    [InlineData("Mines/Gear", "Mine", false)]
    [InlineData("Gear", "Mine", false)]
    public void IsInside_WorksOnASegmentBoundary(string folder, string anchor, bool expected) =>
        Assert.Equal(expected, CategoryLayoutEditor.IsInside(folder, anchor));

    // ---- creator level

    [Fact]
    public void PickingTheGroupByLevel_FollowsGroupBy_AndAnyOtherIsCustom()
    {
        var editor = Editor();
        Assert.True(editor.FollowsGroupBy);
        Assert.Equal(CreatorLevel.AfterType, editor.CreatorShown(SortStrategy.TypeThenCreator));

        editor.SetCreator(CreatorLevel.AfterType, SortStrategy.TypeThenCreator);
        Assert.True(editor.FollowsGroupBy);
        Assert.Empty(_saved);

        editor.SetCreator(CreatorLevel.None, SortStrategy.TypeThenCreator);
        Assert.Equal(CreatorLevel.None, editor.Layout.Creator);
        Assert.True(editor.IsCustom(ModCategory.Gear));
        Assert.Equal(CreatorLevel.None, editor.CreatorShown(SortStrategy.CreatorThenType));

        editor.SetCreator(CreatorLevel.AfterType, SortStrategy.TypeThenCreator);
        Assert.Null(editor.Layout.Creator);
        Assert.False(editor.IsCustom(ModCategory.Gear));
    }

    [Fact]
    public void CreatorOnly_IgnoresCreatorPicks_AndShowsNoLevel()
    {
        var editor = Editor();
        editor.SetCreator(CreatorLevel.None, SortStrategy.CreatorOnly);
        Assert.Null(editor.Layout.Creator);
        Assert.Null(editor.CreatorShown(SortStrategy.CreatorOnly));
        Assert.Empty(_saved);
    }

    // ---- splits: order, depth numbering, states

    [Fact]
    public void Splits_AreInTheCatalogOrder_NumberedAmongTheEnabledFolders()
    {
        var editor = Editor();
        var gear = editor.Splits(SortStrategy.TypeThenCreator, false, true);
        Assert.Equal([LayoutSplits.BodyType, LayoutSplits.Slot, LayoutSplits.SharedHandsFeet], gear.Select(r => r.Info.Split));
        Assert.Equal([1, 2, (int?)null], gear.Select(r => r.Depth));

        editor.SetSplit(LayoutSplits.BodyType, false);
        Assert.Equal([null, 1, (int?)null], editor.Splits(SortStrategy.TypeThenCreator, false, true).Select(r => r.Depth));

        editor.Select(ModCategory.Hair);
        Assert.Equal(["Race", "Hair number"], editor.Splits(SortStrategy.TypeOnly, true, true).Select(r => r.Label));
    }

    [Fact]
    public void BodyAndFace_ShowEveryRaceSplit_IncludingRacialScalingAndEyes()
    {
        var editor = Editor();
        editor.Select(ModCategory.Body);
        Assert.Equal(["Part and body type", "Tails, ears and horns by race", "Racial scaling by race"],
            editor.Splits(SortStrategy.TypeOnly, true, true).Select(r => r.Label));
        editor.Select(ModCategory.Face);
        Assert.Equal(["Race", "Eyes by race"], editor.Splits(SortStrategy.TypeOnly, true, true).Select(r => r.Label));
    }

    [Fact]
    public void ACategoryWithoutSplits_HasNoRows()
    {
        var editor = Editor();
        editor.Select(ModCategory.Weapon);
        Assert.Empty(editor.Splits(SortStrategy.TypeOnly, false, true));
    }

    [Fact]
    public void CreatorOnly_AndAClosedGate_GreyEverySplitWithTheReason()
    {
        var editor = Editor();
        Assert.All(editor.Splits(SortStrategy.CreatorOnly, false, true), r =>
        {
            Assert.False(r.Enabled);
            Assert.Equal(CategoryLayoutEditor.SplitsInapplicableReason, r.DisabledReason);
        });
        Assert.All(editor.Splits(SortStrategy.TypeOnly, false, gateOpen: false), r =>
            Assert.Equal(CategoryLayoutEditor.ActivityGateReason, r.DisabledReason));
    }

    [Fact]
    public void DependentSplits_GreyWhileTheirParentIsOff_AndKeepTheirTick()
    {
        var editor = Editor();
        editor.SetSplit(LayoutSplits.Slot, false);
        var shared = editor.Splits(SortStrategy.TypeOnly, false, true).Single(r => r.Info.Split == LayoutSplits.SharedHandsFeet);
        Assert.False(shared.Enabled);
        Assert.True(shared.On);
        Assert.Equal(CategoryLayoutEditor.NeedsSlotReason, shared.DisabledReason);

        editor.Select(ModCategory.Body);
        editor.SetSplit(LayoutSplits.PartAndBodyType, false);
        var rows = editor.Splits(SortStrategy.TypeOnly, true, true);
        Assert.True(rows[0].Enabled);
        Assert.All(rows.Skip(1), r => Assert.Equal(CategoryLayoutEditor.NeedsBodySplitReason, r.DisabledReason));
    }

    [Fact]
    public void RaceSplits_SayTheyWaitForTheMasterSwitch()
    {
        var editor = Editor();
        editor.Select(ModCategory.Skin);
        Assert.EndsWith(CategoryLayoutEditor.RaceOffNote, editor.Splits(SortStrategy.TypeOnly, false, true)[0].Hint);
        Assert.DoesNotContain(CategoryLayoutEditor.RaceOffNote, editor.Splits(SortStrategy.TypeOnly, true, true)[0].Hint);
    }

    [Fact]
    public void EverySplitHasAHint() =>
        Assert.All(LayoutCatalog.Categories, c =>
        {
            var editor = Editor();
            editor.Select(c);
            Assert.All(editor.Splits(SortStrategy.TypeOnly, true, true), r => Assert.False(string.IsNullOrEmpty(r.Hint)));
        });

    // ---- reset

    [Fact]
    public void ResetOne_RestoresThatCategoryOnly_AndTheFolderText()
    {
        var editor = Editor(Customised(ModCategory.Gear, ModCategory.Hair));
        editor.Select(ModCategory.Gear);
        editor.Reset(ModCategory.Gear);

        Assert.False(editor.IsCustom(ModCategory.Gear));
        Assert.True(editor.IsCustom(ModCategory.Hair));
        Assert.Equal("Gear", editor.FolderText);
        Assert.Equal(editor.Profile, _saved[^1]);
    }

    [Fact]
    public void ResetAll_GivesRecommended_AndSavesOnce()
    {
        var editor = Editor(Customised(ModCategory.Gear, ModCategory.Hair, ModCategory.Others));
        editor.ResetAll();
        Assert.Equal(LayoutProfile.Recommended, editor.Profile);
        Assert.Equal(LayoutProfile.Recommended, Assert.Single(_saved));
    }

    // ---- select all

    [Fact]
    public void SelectAll_TurnsEverySplitOn_SharedOff_AndKeepsFoldersAndCreatorLevels()
    {
        var start = LayoutProfile.Recommended
            .With(ModCategory.Gear, new CategoryLayout("Equipment/Gear", CreatorLevel.None, LayoutSplits.SharedHandsFeet))
            .With(ModCategory.Hair, new CategoryLayout("Hair", CreatorLevel.BeforeType, LayoutSplits.None));
        var editor = Editor(start);

        Assert.True(editor.SelectAll()); // the race master switch goes on

        foreach (var category in LayoutCatalog.Categories)
        {
            var expected = LayoutCatalog.LegalSplits(category) & ~LayoutSplits.SharedHandsFeet;
            Assert.Equal(expected, editor.Profile[category].Splits);
            Assert.Equal(start[category].Folder, editor.Profile[category].Folder);
            Assert.Equal(start[category].Creator, editor.Profile[category].Creator);
        }

        Assert.Single(_saved);
    }

    [Fact]
    public void SelectAll_MeansWhatSplitTogglesMostDetailedMeans()
    {
        // The old tab's Select all wrote these legacy fields; read back as a profile they must be the same layout.
        var detailed = SplitToggles.MostDetailed(default);
        var config = new Configuration
        {
            SortSplitGear = detailed.SplitGear, SortSplitNpc = detailed.SplitNpc, SortSplitHair = detailed.SplitHair,
            SortSplitGearBody = detailed.SplitGearBody, SortSplitBodyMods = detailed.SplitBodyMods,
            SortSharedHandsFeet = detailed.SharedHandsFeet, SortSplitEmotes = detailed.SplitEmotes,
            RaceSplitEnabled = detailed.RaceEnabled, RaceSplitRows = RaceRowNames.ToNames(detailed.RaceRows),
        };

        var editor = Editor();
        Assert.Equal(detailed.RaceEnabled, editor.SelectAll());
        Assert.Equal(LayoutProfile.FromLegacy(LegacyLayout.ReadFields(config)), editor.Profile);
    }

    // ---- example and Recommended copy

    [Fact]
    public void Example_IsTheSelectedCategorysPath()
    {
        var editor = Editor();
        editor.Select(ModCategory.NPC);
        Assert.Equal("NPC/Bosses/Thor/Proto Carbuncle",
            ExamplePaths.Text(editor.Example(SortStrategy.TypeThenCreator, false)));
    }

    [Fact]
    public void RecommendedLines_TellTheTruthAboutRace()
    {
        Assert.Equal("One folder per type. Race split is off.", CategoryLayoutEditor.RecommendedLines(false)[^1].Text);
        Assert.Contains("Split by race is on", CategoryLayoutEditor.RecommendedLines(true)[^1].Text);
        Assert.Equal(["Gear", "Hair", "Body", "NPC", "Animation", "Everything else"],
            CategoryLayoutEditor.RecommendedLines(false).Select(l => l.Title));
    }
}
