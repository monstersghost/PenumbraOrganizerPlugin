using PenumbraOrganizer.Plugin.Organizer.BodyTypes;
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer.Classification;
using PenumbraOrganizer.Plugin.Organizer.Templates;

namespace PenumbraOrganizer.Plugin.Organizer;

/// <summary>
/// The folder-selection expressions behind every sort strategy, in one public place so that
/// OrganizerState's sorts and the template planner's fallback compute destinations with the same
/// code rather than two implementations that can drift.
/// </summary>
/// <remarks>
/// The subcategory split flags are applied in sequence rather than switched over, because they are
/// independent decisions about the same subcategory: composing them is what makes every
/// combination reachable without writing a variant of each strategy.
/// <para>
/// The body split is not one of them. A body type is a segment of its own, above the subcategory,
/// so it composes with the others rather than competing for the same slot - which is what lets a
/// gear mod be both a Top and a Bibo+ garment at Gear/Bibo+/Top.
/// </para>
/// </remarks>
public static class SortFolderSelectors
{
    /// <param name="canonicalizeCreator">
    /// Null for callers that cannot produce a creator segment, which then resolves to null and
    /// falls through to the "Review" bucket. The local functions below keep every segment lazy, so
    /// an unused segment is never built.
    /// </param>
    /// <param name="renameFolder">
    /// Applied by the template planner to map a type folder onto the template author's own label
    /// for it. Null for ordinary sorts, which rename nothing.
    /// </param>
    public static (string? Primary, string? Secondary) Select(
        SortStrategy strategy,
        bool splitGear,
        bool splitNpc,
        OrganizerModRow row,
        Func<string, string>? canonicalizeCreator = null,
        Func<string, string>? renameFolder = null,
        bool splitHair = false,
        HairPriority? hairPriority = null,
        bool splitGearBody = false,
        bool splitBodyMods = false,
        BodyPriority? bodyPriority = null,
        GearFolderLayout? gearLayout = null)
    {
        string? Creator() =>
            canonicalizeCreator is null ? null : KnownSegment(canonicalizeCreator(row.Author));

        string? Type()
        {
            // The numbered gear layout owns Gear rows entirely. The template rename still applies,
            // to the whole numbered path, exactly where TypeFolder applies it.
            if (gearLayout is not null && row.Category == ModCategory.Gear)
            {
                var gearFolder = gearLayout.Folder(
                    row.SubCategory, BodyFor(row, splitGearBody, splitBodyMods, bodyPriority));
                return renameFolder is null ? gearFolder : renameFolder(gearFolder);
            }
            var sub = row.SubCategory;
            // A priority only changes a hair row's number, and only while hair is split. With no
            // priority the resolver's step 3 returns the number the scan stored, so the output is
            // unchanged from 0.6.1.0.
            if (splitHair && hairPriority is { } priority && row.Category == ModCategory.Hair)
            {
                var resolved = HairPriorityResolver.Resolve(row.HairFlat, row.HairByRaceCode, priority);
                sub = resolved?.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            if (!splitGear) sub = FlattenGearSubCategory(row.Category, sub);
            if (!splitNpc) sub = FlattenNpcSubCategory(row.Category, sub);
            if (!splitHair) sub = FlattenHairSubCategory(row.Category, sub);
            // Not flattened the way the three above are: a body type is its own segment on the row,
            // never a value in SubCategory, so turning the split off is simply not passing it.
            return TypeFolder(row.Category, sub, renameFolder,
                BodyFor(row, splitGearBody, splitBodyMods, bodyPriority));
        }

        return strategy switch
        {
            SortStrategy.CreatorOnly => (Creator(), null),
            SortStrategy.TypeOnly => (Type(), null),
            SortStrategy.TypeThenCreator => (Type(), Creator()),
            SortStrategy.CreatorThenType => (Creator(), Type()),
            _ => throw new ArgumentOutOfRangeException(nameof(strategy), strategy, "Unknown sort strategy."),
        };
    }

    /// <summary>
    /// Collapses a (primary, secondary) pair into the single folder a template plan carries.
    /// Deliberately mirrors BuildPath's segment order and its "Review" fallback for an
    /// unclassified row, so a planned folder and a sorted folder agree.
    /// </summary>
    public static string FlattenToFolder(string? primary, string? secondary)
    {
        if (primary is not null && secondary is not null)
            return $"{primary}/{secondary}";
        if (primary is not null)
            return primary;
        if (secondary is not null)
            return secondary;
        return "Review";
    }

    // Gear only: always the flat folder, ignoring any resolved slot subcategory. Every other
    // category keeps its normal subfolder behavior.
    public static string? FlattenGearSubCategory(ModCategory? category, string? subCategory) =>
        category == ModCategory.Gear ? null : subCategory;

    // The NPC mirror of the above. Unlike gear, this had no button before the split checkboxes:
    // NPC mods were always split into NPC/NPCs, NPC/Bosses and NPC/Enemies. Turning it off yields
    // plain "NPC".
    public static string? FlattenNpcSubCategory(ModCategory? category, string? subCategory) =>
        category == ModCategory.NPC ? null : subCategory;

    // The hair mirror of the two above. Like gear and unlike NPC, this has a checkbox and defaults
    // off, so flat "Hair" is what an unchanged install produces.
    public static string? FlattenHairSubCategory(ModCategory? category, string? subCategory) =>
        category == ModCategory.Hair ? null : subCategory;

    public static string? TypeFolder(ModCategory? category, string? subCategory,
                                    Func<string, string>? renameFolder, string? bodyType = null)
    {
        if (category is null)
            return null;

        var folder = ModTypeFolders.GetFolder(category.Value, subCategory, bodyType);
        if (string.IsNullOrWhiteSpace(folder))
            return null;

        // Renaming happens strictly after GetFolder, so no template value can reach GetFolder's
        // deliberate throw for a nonsense (category, subcategory) pairing.
        return renameFolder is null ? folder : renameFolder(folder);
    }

    // The live priority beats the stored default only when the row has candidates to rank. A row
    // built without them (an imported workbook, an older scan, most tests) keeps its BodyType, so
    // adding the priority changes no existing result.
    // A row's body segment is used only when ITS category's flag is on. Gear and Body are the only
    // categories that ever carry one (ModTypeClassifier.BodyTypeApplies), so no other category can
    // reach ResolveBody.
    private static string? BodyFor(
        OrganizerModRow row, bool splitGearBody, bool splitBodyMods, BodyPriority? priority) =>
        (row.Category == ModCategory.Gear && splitGearBody) || (row.Category == ModCategory.Body && splitBodyMods)
            ? ResolveBody(row, priority)
            : null;

    /// <summary>
    /// The numbered Gear layout for the rows that are about to be placed by the gear rule, or null
    /// unless BOTH the slot split and the gear body split are on (any other combination keeps
    /// today's layout exactly).
    /// </summary>
    public static GearFolderLayout? BuildGearLayout(
        IEnumerable<OrganizerModRow> rows, bool splitGear, bool splitGearBody, BodyPriority? priority) =>
        splitGear && splitGearBody
            ? GearFolderLayout.Build(
                rows.Where(r => r.Category == ModCategory.Gear).Select(r => ResolveBody(r, priority)), priority)
            : null;

    private static string? ResolveBody(OrganizerModRow row, BodyPriority? priority) =>
        priority is not null && row.BodyCandidates is not null
            ? BodyPriorityResolver.Resolve(row.BodyCandidates, priority, nameFirst: row.Category == ModCategory.Body)?.Label
            : row.BodyType;

    // Single dynamic segments (creator names) mirror Penumbra's FixName so what we propose is
    // what Penumbra will actually store. Multi-level type folders must NOT be FixName'd — that
    // would turn their '/' separator into '\'.
    public static string? KnownSegment(string? segment) =>
        string.IsNullOrWhiteSpace(segment) ? null : PenumbraPathSemantics.FixName(segment);
}
