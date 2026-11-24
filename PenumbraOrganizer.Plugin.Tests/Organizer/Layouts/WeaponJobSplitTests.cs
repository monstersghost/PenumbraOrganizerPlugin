using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Classification;
using PenumbraOrganizer.Plugin.Organizer.Layouts;
using PenumbraOrganizer.Plugin.Windows;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Layouts;

// The Weapon category's Job split: opt-in, stored only in the category layouts, on in Select all, routed to the engine
// through LayoutOptions.SplitWeaponByJob, and in the fingerprint only while on.
public class WeaponJobSplitTests
{
    private static readonly string[] Labels = ["Bibo+", "Rue+"];

    private static LayoutProfile WithJob(LayoutProfile? from = null, string? folder = null, CreatorLevel? creator = null)
    {
        var profile = from ?? LayoutProfile.Recommended;
        return profile.With(ModCategory.Weapon,
            new CategoryLayout(folder ?? LayoutCatalog.DefaultFolder(ModCategory.Weapon), creator, LayoutSplits.Job));
    }

    private static Configuration Config(LayoutProfile profile, SortStrategy strategy = SortStrategy.TypeThenCreator)
    {
        var config = Configuration.CreateFresh();
        config.SaveLayoutProfile(profile);
        config.SortGrouping = strategy.ToString();
        return config;
    }

    private static OrganizerModRow Weapon(string id, string? job, string author = "Ann") => new()
    {
        Identifier = id, Name = $"Mod {id}", Author = author, CurrentPath = $"Loose/Mod {id}", ProposedPath = $"Loose/Mod {id}",
        Category = ModCategory.Weapon, WeaponJob = job,
    };

    private static Dictionary<string, string> Sort(Configuration config, params OrganizerModRow[] rows)
    {
        var state = new OrganizerState();
        state.LoadScan(rows, new HashSet<string>(), bodyLabels: Labels);
        SortRunner.Run(state, SortSelectionFactory.FromConfig(config, Labels), name => name, scope: null);
        return state.Mods.ToDictionary(m => m.Identifier, m => m.ProposedPath);
    }

    [Fact]
    public void Catalog_WeaponHasTheJobSplit_OffInRecommended()
    {
        var job = Assert.Single(LayoutCatalog.SplitsFor(ModCategory.Weapon));
        Assert.Equal(new SplitInfo(LayoutSplits.Job, "Job", true, true), job);
        Assert.Equal(LayoutSplits.None, LayoutProfile.Recommended[ModCategory.Weapon].Splits);
        Assert.False(SortSelectionFactory.WeaponJobSplitFor(Configuration.CreateFresh()));
    }

    [Fact]
    public void IsStoredInTheLayoutsOnly_AndReadsBack()
    {
        var config = Config(WithJob());

        var entry = Assert.Single(config.CategoryLayouts!, e => e.Category == "Weapon");
        Assert.Equal(["Job"], entry.Splits);
        Assert.True(SortSelectionFactory.WeaponJobSplitFor(config));
        Assert.True(LayoutProfile.Resolve(LayoutOverrides.Read(config.CategoryLayouts))[ModCategory.Weapon].Has(LayoutSplits.Job));

        // No legacy field carries it: the rollback mirror is exactly Recommended's.
        Assert.Equal(LegacyLayout.ReadFields(Config(LayoutProfile.Recommended)), LegacyLayout.ReadFields(config));
        Assert.Equal(LayoutProfile.Recommended.ToLegacy(), WithJob().ToLegacy());
    }

    [Fact]
    public void ASavedJobOnAnotherCategory_IsDropped()
    {
        var read = LayoutOverrides.Read([new CategoryLayoutEntry { Category = "Gear", Folder = "Gear", Splits = ["Job"] }]);
        Assert.Equal(LayoutSplits.None, read[ModCategory.Gear].Splits);
    }

    [Fact]
    public void SelectAll_TurnsItOn()
    {
        var editor = new CategoryLayoutEditor(LayoutProfile.Recommended, _ => { });
        editor.SelectAll();
        Assert.True(editor.Profile[ModCategory.Weapon].Has(LayoutSplits.Job));
    }

    [Fact]
    public void On_AWeaponWithAJob_GoesUnderItsJob_AndOneWithoutStaysInWeapon()
    {
        var paths = Sort(Config(WithJob()),
            Weapon("sam", "SAM"), Weapon("multi", WeaponJobs.Multiclass), Weapon("none", null));

        Assert.Equal("Weapon/SAM/Ann/Mod sam", paths["sam"]);
        Assert.Equal("Weapon/Multiclass/Ann/Mod multi", paths["multi"]);
        Assert.Equal("Weapon/Ann/Mod none", paths["none"]);
    }

    [Fact]
    public void Off_AWeaponWithAJob_StaysDirectlyInWeapon()
    {
        var paths = Sort(Config(LayoutProfile.Recommended), Weapon("sam", "SAM"));
        Assert.Equal("Weapon/Ann/Mod sam", paths["sam"]);
    }

    [Theory]
    [InlineData(SortStrategy.TypeOnly, "Weapon/SAM/Mod sam")]
    [InlineData(SortStrategy.CreatorThenType, "Ann/Weapon/SAM/Mod sam")]
    [InlineData(SortStrategy.CreatorOnly, "Ann/Mod sam")]
    public void On_TheJobFollowsGroupBy(SortStrategy strategy, string expected) =>
        Assert.Equal(expected, Sort(Config(WithJob(), strategy), Weapon("sam", "SAM"))["sam"]);

    [Fact]
    public void On_ARenamedWeaponFolder_KeepsTheJobBelowIt()
    {
        var paths = Sort(Config(WithJob(folder: "Equipment/Weapons", creator: CreatorLevel.None)), Weapon("sam", "SAM"));
        Assert.Equal("Equipment/Weapons/SAM/Mod sam", paths["sam"]);
    }

    [Fact]
    public void TheSortTabSelection_AndTheHeadlessSelection_Agree()
    {
        var config = Config(WithJob());
        var headless = SortSelectionFactory.FromConfig(config, Labels);
        var legacy = LayoutProfileCache.For(config).ToLegacy();
        var panel = SortPanel.BuildSelection(config, headless.Strategy, legacy.SplitGear, legacy.SplitNpc, legacy.SplitHair,
            headless.HairRaceCode, headless.MidlanderFallback, legacy.SplitGearBody, legacy.SplitBodyMods, headless.BodyPriority,
            legacy.SharedHandsFeet, legacy.SplitEmotes, RaceSplitOptions.FromConfig(config), rootOnly: false);

        Assert.True(headless.Layout.SplitWeaponByJob);
        Assert.Equal(headless, panel);
    }

    [Fact]
    public void Fingerprint_DiffersOnlyByTheSplit_AndIsUnchangedWithoutIt()
    {
        var without = SortSelectionFactory.FromConfig(Config(LayoutProfile.Recommended), Labels);
        var with = SortSelectionFactory.FromConfig(Config(WithJob()), Labels);

        Assert.Equal(without with { Layout = without.Layout with { SplitWeaponByJob = true } }, with);
        Assert.NotEqual(without.Fingerprint(), with.Fingerprint());
        Assert.DoesNotContain("wj=", without.Fingerprint(), StringComparison.Ordinal);
        Assert.Equal(without.Fingerprint() + "|wj=1", with.Fingerprint());
    }

    [Fact]
    public void Fingerprint_TheSplitGoesBeforeTheFolderDigest()
    {
        var renamed = LayoutProfile.Recommended.With(ModCategory.Gear,
            LayoutProfile.Recommended[ModCategory.Gear] with { Folder = "Kit" });
        var without = SortSelectionFactory.FromConfig(Config(renamed), Labels).Fingerprint();
        var with = SortSelectionFactory.FromConfig(Config(WithJob(renamed)), Labels).Fingerprint();

        var cats = without.IndexOf("|cats=", StringComparison.Ordinal);
        Assert.True(cats > 0);
        Assert.Equal(without.Insert(cats, "|wj=1"), with);
    }

    [Fact]
    public void ExamplePath_ShowsTheJob_AndMatchesTheSort()
    {
        foreach (var (strategy, _) in SortPanel.Groupings)
        {
            var profile = WithJob();
            var example = ExamplePaths.Text(ExamplePaths.For(ModCategory.Weapon, profile[ModCategory.Weapon], strategy, false));
            var row = new OrganizerModRow
            {
                Identifier = "w", Name = "Gunbreaker Blade", Author = "Ann", CurrentPath = "Loose/Gunbreaker Blade",
                ProposedPath = "Loose/Gunbreaker Blade", Category = ModCategory.Weapon, WeaponJob = "GNB",
            };
            Assert.Equal(example, Sort(Config(profile, strategy), row)["w"]);
        }

        Assert.Equal("Weapon/GNB/Ann/Gunbreaker Blade", ExamplePaths.Text(
            ExamplePaths.For(ModCategory.Weapon, WithJob()[ModCategory.Weapon], SortStrategy.TypeThenCreator, false)));
    }

    [Fact]
    public void Editor_ShowsTheJobRow_WithItsHint_AndNoRaceNote()
    {
        var editor = new CategoryLayoutEditor(LayoutProfile.Recommended, _ => { });
        editor.Select(ModCategory.Weapon);
        var row = Assert.Single(editor.Splits(SortStrategy.TypeOnly, raceEnabled: false, gateOpen: true));

        Assert.Equal("Job", row.Label);
        Assert.False(row.On);
        Assert.True(row.Enabled);
        Assert.Contains("Scan again", row.Hint, StringComparison.Ordinal);
        Assert.DoesNotContain(CategoryLayoutEditor.RaceOffNote, row.Hint, StringComparison.Ordinal);
        Assert.False(CategoryLayoutEditor.HasRaceSplit(ModCategory.Weapon));
        Assert.True(CategoryLayoutEditor.HasRaceSplit(ModCategory.Face));
    }

    [Theory]
    [InlineData(true, true, null, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    [InlineData(false, true, true, true)]
    [InlineData(false, true, false, false)]
    [InlineData(false, true, null, false)]
    [InlineData(true, false, null, false)]
    public void RescanNote_ShowsWhenTheRowsWereScannedTheOtherWay(bool on, bool hasRows, bool? scannedWith, bool shown) =>
        Assert.Equal(shown ? CategoryLayoutEditor.WeaponRescanHint : null,
            CategoryLayoutEditor.WeaponRescanNote(on, hasRows, scannedWith));

    [Fact]
    public void TheStateRemembersHowTheScanClassifiedWeapons()
    {
        var state = new OrganizerState();
        Assert.Null(state.WeaponsClassifiedByScan);
        state.ReplaceScanAtomically([], new HashSet<string>(), weaponsClassified: true);
        Assert.True(state.WeaponsClassifiedByScan);
        state.LoadScan([], new HashSet<string>());
        Assert.Null(state.WeaponsClassifiedByScan);
    }

    [Fact]
    public void Help_TheSplitHasItsOwnTopic() =>
        Assert.Equal("sort.split-weapon-job", SplitHelp.TopicFor(ModCategory.Weapon, LayoutSplits.Job).Id);
}
