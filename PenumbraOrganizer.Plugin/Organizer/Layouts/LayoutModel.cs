using PenumbraOrganizer.Core.Classification;

namespace PenumbraOrganizer.Plugin.Organizer.Layouts;

/// <summary>Where the creator folder goes for one category. Null on a layout means "follow Group by".</summary>
public enum CreatorLevel
{
    /// <summary>No creator folder: <c>Gear/Top/Mod</c>.</summary>
    None,

    /// <summary>Creator above the type folders: <c>Ann/Gear/Top/Mod</c>.</summary>
    BeforeType,

    /// <summary>Creator below the type folders: <c>Gear/Top/Ann/Mod</c>.</summary>
    AfterType,
}

/// <summary>
/// The splits a category can use, as flags so a category's set is one comparable value. Which ones a category
/// may use, and in which folder order, is <see cref="LayoutCatalog"/>'s job; the names are stored in the config,
/// so renaming a member breaks saved layouts.
/// </summary>
[Flags]
public enum LayoutSplits
{
    None = 0,

    /// <summary>Gear: a folder per body, above the slot (today's "Split gear by body type").</summary>
    BodyType = 1,

    /// <summary>Gear: the equipment slot (today's "Split gear by equipment slot").</summary>
    Slot = 2,

    /// <summary>Gear, not a folder: hands and feet skip the body folder (today's shared hands and feet).</summary>
    SharedHandsFeet = 4,

    /// <summary>NPC: NPCs, Bosses, Enemies.</summary>
    NpcKind = 8,

    /// <summary>The category's own race row: Hair, Face, Skin, or Body's tails, ears and horns.</summary>
    Race = 16,

    /// <summary>Hair: the vanilla hair number, three digits.</summary>
    HairNumber = 32,

    /// <summary>Face: iris-only mods under <c>Face/Eyes/&lt;race&gt;</c> (the Eyes race row).</summary>
    EyesRace = 64,

    /// <summary>Body: the numbered part and body-type folders (today's "Split body mods by body type").</summary>
    PartAndBodyType = 128,

    /// <summary>Body: racial-scaling mods routed to <c>Body/03. Racial Scaling/&lt;race&gt;</c>.</summary>
    RacialScalingRace = 256,

    /// <summary>Animation: emotes split by the emote they replace.</summary>
    Emote = 512,
}

/// <summary>
/// One category's layout: its top folder (a relative path, may be nested), the creator level (null follows
/// Group by) and the splits it uses. A positional record of a string and two enums, so it has value equality.
/// </summary>
public sealed record CategoryLayout(string Folder, CreatorLevel? Creator, LayoutSplits Splits)
{
    public bool Has(LayoutSplits split) => (Splits & split) == split;
}

/// <summary>
/// Today's Sort tab split checkboxes and race rows as one value: what the legacy configuration fields store and
/// what the engine's flag parameters take. <see cref="LayoutProfile.FromLegacy"/> and
/// <see cref="LayoutProfile.ToLegacy"/> convert both ways without loss.
/// </summary>
public readonly record struct LegacyLayout(
    bool SplitGear, bool SplitNpc, bool SplitHair, bool SplitGearBody, bool SplitBodyMods,
    bool SharedHandsFeet, bool SplitEmotes, RaceRows RaceRows)
{
    /// <summary>
    /// The legacy fields as stored, never consulting <c>CategoryLayouts</c>. The race rows are read the way
    /// <c>RaceSplitOptions.FromConfig</c> always has: a config the race migration has not touched yet uses the
    /// legacy hair checkbox.
    /// </summary>
    public static LegacyLayout ReadFields(Configuration config) => new(
        config.SortSplitGear, config.SortSplitNpc, config.SortSplitHair, config.SortSplitGearBody,
        config.SortSplitBodyMods, config.SortSharedHandsFeet, config.SortSplitEmotes,
        config.RaceSplitEnabled is not null
            ? RaceRowNames.Parse(config.RaceSplitRows)
            : config.SortSplitHairByRace ? RaceRows.Hair : RaceRows.All);
}

/// <summary>
/// The effective layout of every category. Value equality over all sixteen layouts. Built from saved overrides
/// with <see cref="Resolve"/> (anything not overridden is Recommended), or from the legacy fields with
/// <see cref="FromLegacy"/>. Immutable: the array is private, never handed out, and <see cref="With"/> copies it,
/// so one instance can be shared between threads and cached.
/// </summary>
public sealed class LayoutProfile : IEquatable<LayoutProfile>
{
    private readonly CategoryLayout[] _layouts;

    private LayoutProfile(CategoryLayout[] layouts) => _layouts = layouts;

    /// <summary>The built-in profile every category follows until the user customises it.</summary>
    public static LayoutProfile Recommended { get; } =
        new([.. LayoutCatalog.Categories.Select(LayoutCatalog.Recommended)]);

    /// <summary>
    /// The category's layout. A value outside the sixteen categories (for example <c>default(ModCategory)</c>)
    /// gets the default Others layout, the place the Sort files a mod without a category, instead of throwing.
    /// </summary>
    public CategoryLayout this[ModCategory category] =>
        LayoutCatalog.IsKnown(category) ? _layouts[Index(category)] : OutsideTheCatalog;

    private static readonly CategoryLayout OutsideTheCatalog = LayoutCatalog.Recommended(ModCategory.Others);

    private static int Index(ModCategory category) => (int)category - 1;

    /// <summary>
    /// Every category's effective layout: its override when there is one, otherwise the Recommended layout
    /// (<paramref name="recommended"/> exists so a test can stand in a later, improved Recommended). Splits a
    /// category cannot use are dropped; a null override and a key outside the sixteen categories are ignored.
    /// </summary>
    public static LayoutProfile Resolve(
        IReadOnlyDictionary<ModCategory, CategoryLayout> overrides, LayoutProfile? recommended = null)
    {
        var baseline = recommended ?? Recommended;
        return new([.. LayoutCatalog.Categories.Select(category =>
            overrides.TryGetValue(category, out var layout) && layout is not null
                ? layout with { Splits = layout.Splits & LayoutCatalog.LegalSplits(category) }
                : baseline[category])]);
    }

    /// <summary>The categories whose layout differs from <paramref name="recommended"/> (default: Recommended).</summary>
    public IReadOnlyDictionary<ModCategory, CategoryLayout> Overrides(LayoutProfile? recommended = null)
    {
        var baseline = recommended ?? Recommended;
        return LayoutCatalog.Categories
            .Where(category => this[category] != baseline[category])
            .ToDictionary(category => category, category => this[category]);
    }

    public bool IsCustom(ModCategory category) => this[category] != Recommended[category];

    /// <summary>A copy with one category's layout replaced (splits it cannot use dropped). This profile is unchanged.</summary>
    public LayoutProfile With(ModCategory category, CategoryLayout layout)
    {
        if (!LayoutCatalog.IsKnown(category))
            throw new ArgumentOutOfRangeException(nameof(category), category, "Not one of the sixteen categories.");
        ArgumentNullException.ThrowIfNull(layout);

        var copy = (CategoryLayout[])_layouts.Clone();
        copy[Index(category)] = layout with { Splits = layout.Splits & LayoutCatalog.LegalSplits(category) };
        return new LayoutProfile(copy);
    }

    public LayoutProfile Reset(ModCategory category) => With(category, Recommended[category]);

    /// <summary>The profile the legacy fields describe: default folders, every creator level follows Group by.</summary>
    public static LayoutProfile FromLegacy(LegacyLayout legacy)
    {
        LayoutSplits If(bool on, LayoutSplits split) => on ? split : LayoutSplits.None;
        bool Row(RaceRows row) => legacy.RaceRows.HasFlag(row);

        var splits = new Dictionary<ModCategory, LayoutSplits>
        {
            [ModCategory.Gear] = If(legacy.SplitGearBody, LayoutSplits.BodyType) | If(legacy.SplitGear, LayoutSplits.Slot)
                                 | If(legacy.SharedHandsFeet, LayoutSplits.SharedHandsFeet),
            [ModCategory.NPC] = If(legacy.SplitNpc, LayoutSplits.NpcKind),
            [ModCategory.Hair] = If(Row(RaceRows.Hair), LayoutSplits.Race) | If(legacy.SplitHair, LayoutSplits.HairNumber),
            [ModCategory.Body] = If(legacy.SplitBodyMods, LayoutSplits.PartAndBodyType)
                                 | If(Row(RaceRows.TailsEarsHorns), LayoutSplits.Race)
                                 | If(Row(RaceRows.RacialScaling), LayoutSplits.RacialScalingRace),
            [ModCategory.Animation] = If(legacy.SplitEmotes, LayoutSplits.Emote),
            [ModCategory.Face] = If(Row(RaceRows.Face), LayoutSplits.Race) | If(Row(RaceRows.Eyes), LayoutSplits.EyesRace),
            [ModCategory.Skin] = If(Row(RaceRows.Skin), LayoutSplits.Race),
        };

        return new([.. LayoutCatalog.Categories.Select(category =>
            new CategoryLayout(LayoutCatalog.DefaultFolder(category), null, splits.GetValueOrDefault(category)))]);
    }

    /// <summary>The split checkboxes and race rows this profile corresponds to. Folders and creator levels are not here.</summary>
    public LegacyLayout ToLegacy()
    {
        var gear = this[ModCategory.Gear];
        var hair = this[ModCategory.Hair];
        var body = this[ModCategory.Body];
        var face = this[ModCategory.Face];
        RaceRows If(bool on, RaceRows row) => on ? row : RaceRows.None;

        var rows = If(hair.Has(LayoutSplits.Race), RaceRows.Hair)
                   | If(face.Has(LayoutSplits.Race), RaceRows.Face)
                   | If(face.Has(LayoutSplits.EyesRace), RaceRows.Eyes)
                   | If(this[ModCategory.Skin].Has(LayoutSplits.Race), RaceRows.Skin)
                   | If(body.Has(LayoutSplits.Race), RaceRows.TailsEarsHorns)
                   | If(body.Has(LayoutSplits.RacialScalingRace), RaceRows.RacialScaling);

        return new LegacyLayout(
            gear.Has(LayoutSplits.Slot), this[ModCategory.NPC].Has(LayoutSplits.NpcKind), hair.Has(LayoutSplits.HairNumber),
            gear.Has(LayoutSplits.BodyType), body.Has(LayoutSplits.PartAndBodyType), gear.Has(LayoutSplits.SharedHandsFeet),
            this[ModCategory.Animation].Has(LayoutSplits.Emote), rows);
    }

    public bool Equals(LayoutProfile? other) =>
        other is not null && (ReferenceEquals(this, other) || _layouts.SequenceEqual(other._layouts));

    public override bool Equals(object? obj) => Equals(obj as LayoutProfile);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var layout in _layouts)
            hash.Add(layout);
        return hash.ToHashCode();
    }

    public static bool operator ==(LayoutProfile? left, LayoutProfile? right) =>
        left is null ? right is null : left.Equals(right);

    public static bool operator !=(LayoutProfile? left, LayoutProfile? right) => !(left == right);
}
