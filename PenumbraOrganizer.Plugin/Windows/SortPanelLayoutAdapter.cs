using System.Runtime.CompilerServices;
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Layouts;

namespace PenumbraOrganizer.Plugin.Windows;

/// <summary>
/// The current Sort tab's checkboxes as a view over the category layouts (spec 1, section 6): a tick writes the
/// matching split of its category. A category customised in a way the checkboxes cannot show (a renamed folder or
/// its own creator level) is read-only here, so this tab can never undo what it cannot display. Pure, so the
/// tests pin it without ImGui.
/// </summary>
public static class SortPanelLayoutAdapter
{
    public const string ReadOnlyReason =
        "This category's folder or creator level was customised, which this tab cannot show, so it is read-only here.";

    /// <summary>The checkbox values a profile shows. The race master is global, so the caller passes it.</summary>
    public static SplitToggles TogglesFor(LayoutProfile profile, bool raceEnabled)
    {
        var legacy = profile.ToLegacy();
        return new SplitToggles(
            legacy.SplitGear, legacy.SplitNpc, legacy.SplitHair, legacy.SplitGearBody, legacy.SplitBodyMods,
            legacy.SharedHandsFeet, legacy.SplitEmotes, raceEnabled, legacy.RaceRows);
    }

    /// <summary>True when the checkboxes show exactly this profile's splits.</summary>
    public static bool Matches(LayoutProfile profile, SplitToggles toggles) =>
        TogglesFor(profile, toggles.RaceEnabled) == toggles;

    /// <summary>Categories with a folder other than the default or a creator level of their own.</summary>
    public static IReadOnlySet<ModCategory> ReadOnlyCategories(LayoutProfile profile) =>
        LayoutCatalog.Categories
            .Where(c => profile[c].Creator is not null
                        || !string.Equals(profile[c].Folder, LayoutCatalog.DefaultFolder(c), StringComparison.Ordinal))
            .ToHashSet();

    /// <summary>
    /// The profile with the checkboxes' splits written into each category, folders and creator levels kept. A
    /// read-only category keeps its saved splits whatever the boxes say.
    /// </summary>
    public static LayoutProfile WithToggles(LayoutProfile current, SplitToggles toggles)
    {
        var wanted = LayoutProfile.FromLegacy(new LegacyLayout(
            toggles.SplitGear, toggles.SplitNpc, toggles.SplitHair, toggles.SplitGearBody, toggles.SplitBodyMods,
            toggles.SharedHandsFeet, toggles.SplitEmotes, toggles.RaceRows));
        var readOnly = ReadOnlyCategories(current);

        var next = current;
        foreach (var category in LayoutCatalog.Categories)
        {
            if (!readOnly.Contains(category) && wanted[category].Splits != current[category].Splits)
                next = next.With(category, current[category] with { Splits = wanted[category].Splits });
        }

        return next;
    }

    /// <summary>
    /// The Sort tab's save decision, run every frame: writes the Group by, the global race settings and the
    /// checkboxes' splits into <paramref name="config"/> when any of them differs from what is saved, and says
    /// whether the caller must save the file. <c>Shown</c> is what the checkboxes must display afterwards: the
    /// boxes of a read-only category are put back to its saved splits (Select all can tick them).
    /// </summary>
    /// <remarks>
    /// Once the layouts exist, every write ends with <see cref="Configuration.SyncLegacyLayoutFields"/> (never the
    /// bare race sync), so the legacy fields stay a true rollback mirror even after a hand edit. A config the
    /// layout migration has not run on (only a headless caller that skipped migration) gets the legacy fields
    /// written as before. A frame with nothing changed reads the cached profile and allocates nothing.
    /// <paramref name="toggles"/>' own race master is ignored in favour of <paramref name="raceEnabled"/>.
    /// </remarks>
    public static (bool Save, SplitToggles Shown) Persist(
        Configuration config, SplitToggles toggles, string groupingName, bool raceEnabled, bool multiRace,
        RacePriority priority)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(priority);

        var wanted = toggles with { RaceEnabled = raceEnabled };
        var current = LayoutProfileCache.For(config);
        var next = Matches(current, wanted) ? current : WithToggles(current, wanted);
        var shown = TogglesFor(next, raceEnabled);

        var layoutChanged = next != current;
        var raceChanged = config.RaceSplitEnabled != raceEnabled
                          || config.SendMultiRaceToMultiRace != multiRace
                          || NormalizedPriority(config.RacePriority) != priority;
        if (!layoutChanged && !raceChanged && config.SortGrouping == groupingName)
            return (false, shown);

        config.SortGrouping = groupingName;
        config.RaceSplitEnabled = raceEnabled;
        config.SendMultiRaceToMultiRace = multiRace;
        config.RacePriority = [.. priority.Ranked];

        if (config.CategoryLayouts is null)
            WriteLegacyFields(config, next.ToLegacy());
        else if (layoutChanged)
            config.SaveLayoutProfile(next); // ends with SyncLegacyLayoutFields
        else
            config.SyncLegacyLayoutFields();

        return (true, shown);
    }

    // The pre-layout write, for a config that has not been migrated: the legacy fields ARE its layout.
    private static void WriteLegacyFields(Configuration config, LegacyLayout legacy)
    {
        config.SortSplitGear = legacy.SplitGear;
        config.SortSplitNpc = legacy.SplitNpc;
        config.SortSplitHair = legacy.SplitHair;
        config.SortSplitGearBody = legacy.SplitGearBody;
        config.SortSplitBodyMods = legacy.SplitBodyMods;
        config.SortSharedHandsFeet = legacy.SharedHandsFeet;
        config.SortSplitEmotes = legacy.SplitEmotes;
        config.RaceSplitRows = RaceRowNames.ToNames(legacy.RaceRows);
        config.SyncLegacyRaceFields();
    }

    // The saved race priority, normalised once per list instance: the tab compares it every frame, and every
    // write assigns a new list, so keying on the instance never serves a stale value.
    private static readonly ConditionalWeakTable<List<string>, RacePriority> Priorities = new();

    private static RacePriority NormalizedPriority(List<string>? saved) =>
        saved is null ? RacePriority.Empty : Priorities.GetValue(saved, list => RacePriority.Normalize(list));

    /// <summary>The category a Race-section row belongs to.</summary>
    public static ModCategory CategoryOf(RaceRows row) => row switch
    {
        RaceRows.Hair => ModCategory.Hair,
        RaceRows.Face or RaceRows.Eyes => ModCategory.Face,
        RaceRows.Skin => ModCategory.Skin,
        RaceRows.TailsEarsHorns or RaceRows.RacialScaling => ModCategory.Body,
        _ => throw new ArgumentOutOfRangeException(nameof(row), row, "Not a single race row."),
    };

    /// <summary>The line the tab shows when any category is read-only here, or null when none is.</summary>
    public static string? CustomNote(IReadOnlySet<ModCategory> readOnly) =>
        readOnly.Count == 0
            ? null
            : $"Custom (read-only here): {string.Join(", ", readOnly.OrderBy(c => c))}.";
}
