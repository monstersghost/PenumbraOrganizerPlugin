using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Layouts;
using PenumbraOrganizer.Plugin.Windows.Widgets;

namespace PenumbraOrganizer.Plugin.Windows;

/// <summary>The two screens of the Sort tab.</summary>
public enum SortTabMode
{
    Recommended,
    Advanced,
}

/// <summary>
/// One split row of the Advanced editor: the catalog entry, its label and one-line hint, whether it is on, its
/// position among the enabled folder splits (null when off or not a folder), and whether it can be changed now.
/// </summary>
public sealed record SplitRow(
    SplitInfo Info, string Label, string Hint, bool On, int? Depth, bool Enabled, string? DisabledReason);

/// <summary>
/// The Sort tab's Advanced editor as plain state, in the style of <c>RulesEditor</c>: the working layout profile, the
/// selected category, the folder text being typed and its validation, custom detection, reset one or all, and Select
/// all. Every change goes through the spec 1 model (<see cref="LayoutProfile.With"/>, <see cref="LayoutProfile.Reset"/>)
/// and is handed to the persist callback once, when it happens, never per frame. Folder typing is the one deferred
/// edit: it updates the working profile (so the example follows) and is persisted by <see cref="Commit"/>, which the
/// tab calls when the text box is left, the category changes or the mode changes. No ImGui, so it is tested directly.
/// </summary>
public sealed class CategoryLayoutEditor
{
    public const string ActivityGateReason = "Another operation is in progress or requires recovery.";
    public const string SplitsInapplicableReason = "Grouping by creator alone never uses the mod's type.";
    public const string NeedsSlotReason = "Needs Equipment slot.";
    public const string NeedsBodySplitReason = "This row's folder only exists while Part and body type is on.";
    public const string RaceOffNote = "Takes effect when Split by race is on.";
    public const string NoSplitsText =
        "This category has no splits yet. New splits, such as weapons by job, will appear here.";

    private readonly Action<LayoutProfile> _persist;
    private LayoutProfile _persisted;

    public CategoryLayoutEditor(LayoutProfile saved, Action<LayoutProfile> persist)
    {
        _persist = persist;
        _persisted = saved;
        Profile = saved;
        Selected = LayoutCatalog.Categories.FirstOrDefault(saved.IsCustom, ModCategory.Gear);
        FolderText = saved[Selected].Folder;
    }

    /// <summary>The working profile: what the screens show and the example paths use.</summary>
    public LayoutProfile Profile { get; private set; }

    public ModCategory Selected { get; private set; }

    /// <summary>The folder text box's content for <see cref="Selected"/>, which may be invalid while typing.</summary>
    public string FolderText { get; private set; }

    /// <summary>Why <see cref="FolderText"/> cannot be saved, or null when it can.</summary>
    public string? FolderError => LayoutFolderName.Error(FolderText);

    public CategoryLayout Layout => Profile[Selected];

    public bool IsCustom(ModCategory category) => Profile.IsCustom(category);

    public int CustomCount => LayoutCatalog.Categories.Count(Profile.IsCustom);

    /// <summary>Recommended mode when nothing is customised, Advanced otherwise (spec 2, section 6).</summary>
    public static SortTabMode InitialMode(LayoutProfile profile) =>
        LayoutCatalog.Categories.Any(profile.IsCustom) ? SortTabMode.Advanced : SortTabMode.Recommended;

    /// <summary>The Advanced header line, "3 of 16 categories customised".</summary>
    public string AdvancedSummary => $"{CustomCount} of {LayoutCatalog.Categories.Count} categories customised";

    /// <summary>The Recommended mode note, or null when nothing is customised.</summary>
    public string? RecommendedNote => CustomCount switch
    {
        0 => null,
        1 => "1 category customised",
        var n => $"{n} categories customised",
    };

    /// <summary>Selects a category, saving a pending folder edit of the previous one first.</summary>
    public void Select(ModCategory category)
    {
        Commit();
        Selected = category;
        FolderText = Profile[category].Folder;
    }

    /// <summary>
    /// The folder text changed. A valid name updates the working profile (normalised); an invalid one only shows
    /// <see cref="FolderError"/> and is never saved. Persisted by <see cref="Commit"/>.
    /// </summary>
    public void SetFolderText(string text)
    {
        FolderText = text;
        if (LayoutFolderName.Error(text) is null)
            Profile = Profile.With(Selected, Layout with { Folder = LayoutFolderName.Normalize(text) });
    }

    /// <summary>Saves the working profile when it differs from what was last saved. True when it saved.</summary>
    public bool Commit()
    {
        if (Profile == _persisted)
            return false;
        _persisted = Profile;
        _persist(Profile);
        return true;
    }

    /// <summary>
    /// A warning for the selected category's folder that does not stop saving: another category files into the same
    /// folder, or the folder sits inside a protected folder or a sort-inside folder. Null when none applies.
    /// </summary>
    public string? FolderWarning(Func<string, bool> isProtected, Func<string, bool> isSortInside)
    {
        var folder = Layout.Folder;
        var same = LayoutCatalog.Categories
            .Where(c => c != Selected && string.Equals(SortFolder(c), SortFolder(Selected), StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (same.Count > 0)
            return $"{string.Join(", ", same)} also uses this folder, so those mods share it.";
        if (isProtected(folder))
            return "This folder is inside a protected folder, so mods filed there will be treated as protected.";
        if (isSortInside(folder))
            return "This folder is inside a sort-inside folder, so mods already there are sorted inside it.";
        return null;
    }

    /// <summary>
    /// True when <paramref name="folder"/> is <paramref name="anchor"/> or below it, on a segment boundary and ignoring
    /// case: the same test the engine uses for renamed category folders (<see cref="CategoryPlacement.IsAtOrInside"/>).
    /// </summary>
    public static bool IsInside(string folder, string anchor) => CategoryPlacement.IsAtOrInside(folder, anchor);

    // The folder the Sort writes for a category: a default folder is the engine's own root (spec 1, A9).
    private string SortFolder(ModCategory category) =>
        string.Equals(Profile[category].Folder, LayoutCatalog.DefaultFolder(category), StringComparison.Ordinal)
            ? LayoutCatalog.SortRoot(category)
            : Profile[category].Folder;

    /// <summary>The creator level the segmented control shows: the category's own, else Group by's.</summary>
    public CreatorLevel? CreatorShown(SortStrategy groupBy) => ExamplePaths.EffectiveCreator(Layout, groupBy);

    /// <summary>True when the selected category follows Group by for its creator folder.</summary>
    public bool FollowsGroupBy => Layout.Creator is null;

    /// <summary>
    /// Picks a creator level. Picking the level Group by already gives means "follow Group by" (null), so the category
    /// does not turn Custom and keeps following a later Group by change. Ignored under Creator only.
    /// </summary>
    public void SetCreator(CreatorLevel level, SortStrategy groupBy)
    {
        if (groupBy == SortStrategy.CreatorOnly)
            return;
        CreatorLevel? stored = level == ExamplePaths.InheritedCreator(groupBy) ? null : level;
        if (stored == Layout.Creator)
            return;
        Profile = Profile.With(Selected, Layout with { Creator = stored });
        Commit();
    }

    /// <summary>The selected category's split rows, in the catalog's fixed folder-depth order.</summary>
    public IReadOnlyList<SplitRow> Splits(SortStrategy groupBy, bool raceEnabled, bool gateOpen)
    {
        var layout = Layout;
        var baseReason = !gateOpen ? ActivityGateReason
            : groupBy == SortStrategy.CreatorOnly ? SplitsInapplicableReason
            : null;
        var rows = new List<SplitRow>();
        var depth = 0;
        foreach (var info in LayoutCatalog.SplitsFor(Selected))
        {
            var on = layout.Has(info.Split);
            int? number = on && info.IsFolder ? ++depth : null;
            var reason = baseReason
                ?? (info.Split == LayoutSplits.SharedHandsFeet && !layout.Has(LayoutSplits.Slot) ? NeedsSlotReason
                    : NeedsBodySplit(Selected, info.Split) && !layout.Has(LayoutSplits.PartAndBodyType) ? NeedsBodySplitReason
                    : null);
            var hint = HintFor(Selected, info.Split);
            if (info.NeedsScanEvidence && !raceEnabled)
                hint = $"{hint} {RaceOffNote}";
            rows.Add(new SplitRow(info, LabelFor(Selected, info), hint, on, number, reason is null, reason));
        }

        return rows;
    }

    /// <summary>Turns one split of the selected category on or off.</summary>
    public void SetSplit(LayoutSplits split, bool on)
    {
        var splits = on ? Layout.Splits | split : Layout.Splits & ~split;
        if (splits == Layout.Splits)
            return;
        Profile = Profile.With(Selected, Layout with { Splits = splits });
        Commit();
    }

    /// <summary>Puts one category back on Recommended (folder, creator level and splits).</summary>
    public void Reset(ModCategory category)
    {
        Profile = Profile.Reset(category);
        if (category == Selected)
            FolderText = Layout.Folder;
        Commit();
    }

    /// <summary>Puts every category back on Recommended.</summary>
    public void ResetAll()
    {
        Profile = LayoutCatalog.Categories.Aggregate(Profile, (profile, category) => profile.Reset(category));
        FolderText = Layout.Folder;
        Commit();
    }

    /// <summary>
    /// Select all splits, with <see cref="SplitToggles.MostDetailed"/>'s meaning carried through the model: every
    /// split of every category on, the shared hands-and-feet box off; folders and creator levels untouched. Returns
    /// the race master switch value the tab must set (on), which is global, not part of a layout.
    /// </summary>
    public bool SelectAll()
    {
        var detailed = SplitToggles.MostDetailed(default);
        var wanted = LayoutProfile.FromLegacy(new LegacyLayout(
            detailed.SplitGear, detailed.SplitNpc, detailed.SplitHair, detailed.SplitGearBody, detailed.SplitBodyMods,
            detailed.SharedHandsFeet, detailed.SplitEmotes, detailed.RaceRows));
        Profile = LayoutCatalog.Categories.Aggregate(Profile,
            (profile, category) => profile.With(category, profile[category] with { Splits = wanted[category].Splits }));
        Commit();
        return detailed.RaceEnabled;
    }

    /// <summary>The selected category's example path.</summary>
    public IReadOnlyList<PathSegment> Example(SortStrategy groupBy, bool raceEnabled) =>
        ExamplePaths.For(Selected, Layout, groupBy, raceEnabled);

    /// <summary>What Recommended does, one line per kind of mod, for the Recommended screen.</summary>
    public static IReadOnlyList<(string Title, string Text)> RecommendedLines(bool raceEnabled) =>
    [
        ("Gear", "By body type, then slot. Accessories stay out of body folders. Hands and feet share a folder."),
        ("Hair", "By hair number, three digits."),
        ("Body", "By body type, then part: Body/10. Bibo+/00. Replacer."),
        ("NPC", "By kind: NPCs, Bosses, Enemies."),
        ("Animation", "Emotes split by the emote they replace."),
        ("Everything else", raceEnabled
            ? "One folder per type. Split by race is on (see Advanced)."
            : "One folder per type. Race split is off."),
    ];

    private static bool NeedsBodySplit(ModCategory category, LayoutSplits split) =>
        category == ModCategory.Body && split is LayoutSplits.Race or LayoutSplits.RacialScalingRace;

    private static string LabelFor(ModCategory category, SplitInfo info) =>
        category == ModCategory.Body && info.Split == LayoutSplits.Race ? "Tails, ears and horns by race" : info.Name;

    private static string HintFor(ModCategory category, LayoutSplits split) => (category, split) switch
    {
        (ModCategory.Gear, LayoutSplits.BodyType) => "Numbered body folders, such as 10. Bibo+.",
        (ModCategory.Gear, LayoutSplits.Slot) => "Head, Top, Hands, Legs, Feet, Ears, Neck, Wrists, Rings.",
        (ModCategory.Gear, LayoutSplits.SharedHandsFeet) => "Gloves and boots fit most bodies. Off in Select all.",
        (ModCategory.NPC, LayoutSplits.NpcKind) => "NPCs, Bosses, Enemies.",
        (ModCategory.Hair, LayoutSplits.Race) => "One race, or Multi-race.",
        (ModCategory.Hair, LayoutSplits.HairNumber) => "Padded to three digits, such as 115.",
        (ModCategory.Body, LayoutSplits.PartAndBodyType) => "Body/10. Bibo+/00. Replacer, with shared parts on top.",
        (ModCategory.Body, LayoutSplits.Race) => "Tails, ears and horns mods by race.",
        (ModCategory.Body, LayoutSplits.RacialScalingRace) => "Racial scaling mods under 03. Racial Scaling/<race>.",
        (ModCategory.Face, LayoutSplits.Race) => "Face mods by race.",
        (ModCategory.Face, LayoutSplits.EyesRace) => "Iris-only mods under Face/Eyes/<race>.",
        (ModCategory.Skin, LayoutSplits.Race) => "Skin mods by race.",
        (ModCategory.Animation, LayoutSplits.Emote) => "The emote the mod replaces.",
        _ => string.Empty,
    };
}
