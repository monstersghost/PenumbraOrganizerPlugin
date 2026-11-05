using System.Text.Json;
using System.Text.RegularExpressions;

namespace PenumbraOrganizer.Plugin.Organizer.Classification;

/// <summary>
/// Reads the vanilla hair numbers a mod targets, for the Sort tab's "Split hair by hair number".
/// </summary>
/// <remarks>
/// The hair mirror of <see cref="ModEquipmentFileReader"/>, sharing its walker and its return
/// contract. What "one number means a subfolder, two means none" is lives in
/// <c>ModTypeClassifier.EnrichHairSubCategory</c>, not here: this reports evidence, it does not
/// make the filing decision.
/// </remarks>
public static class ModHairFileReader
{
    /// <summary>The supported hair numbers, inclusive. Also what ModTypeFolders.IsHairNumber accepts.</summary>
    private const int MinHairNumber = 1;
    private const int MaxHairNumber = 9999;

    // Anchored at the start: Files keys are full game paths, and an unanchored match would accept
    // a path that merely CONTAINS this shape. Group 1 is the race folder code, kept for the
    // per-race evidence; group 2 is the hair number. The flat set still collapses across races -
    // one hair shipped for eight clans is one number, not eight.
    //
    // [0-9], NOT \d: .NET's \d matches every Unicode decimal digit, so a path with fullwidth or
    // Devanagari digits would MATCH here and then throw FormatException inside the parse - an
    // exception the walker does not catch, so it would escape into the scan. This repo has been
    // bitten three times by a non-ASCII codepoint reaching code as something other than what it
    // looked like; this is that same hazard in regex form.
    private static readonly Regex HairPath = new(
        @"^chara/human/c([0-9]{4})/obj/hair/h([0-9]{4})/",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>The flat set and the per-race sets, read in one walk of the mod.</summary>
    /// <remarks>
    /// <c>Flat</c> is every supported hair number from file paths and EST entries. <c>ByRaceCode</c>
    /// holds only file paths, keyed by the four-digit race folder code. An EST entry names a race in the
    /// metadata, but this reader does not use that race, so an EST SetId never appears under any code.
    /// </remarks>
    public sealed record HairEvidence(
        IReadOnlySet<int> Flat,
        IReadOnlyDictionary<string, IReadOnlySet<int>> ByRaceCode);

    /// <summary>
    /// Reads the mod's hair evidence in one walk. Null means the walk could not be trusted, the same
    /// meaning <see cref="ReadHairNumbers"/> has always had.
    /// </summary>
    public static HairEvidence? ReadHairEvidence(DirectoryInfo modDirectory)
    {
        var flat = new HashSet<int>();
        var byRace = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);

        var ok = ModConfigFileWalker.TryWalk(
            modDirectory,
            path => CollectFromFileKey(path, flat, byRace),
            item => CollectFromManipulation(item, flat));

        if (!ok)
            return null;

        return Freeze(flat, byRace);
    }

    internal static HairEvidence Freeze(HashSet<int> flat, Dictionary<string, HashSet<int>> byRace)
    {
        var frozen = byRace.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlySet<int>)pair.Value,
            StringComparer.Ordinal);
        return new HairEvidence(flat, frozen);
    }

    /// <summary>The flat set only, for callers that do not need the race split.</summary>
    public static IReadOnlySet<int>? ReadHairNumbers(DirectoryInfo modDirectory) =>
        ReadHairEvidence(modDirectory)?.Flat;

    internal static void CollectFromFileKey(
        string path, HashSet<int> flat, Dictionary<string, HashSet<int>> byRace)
    {
        // Already '/'-normalised by the walker.
        var match = HairPath.Match(path);
        if (!match.Success)
            return;

        // TryParse, not Parse: four ASCII digits cannot overflow an int, but a defensive parse
        // costs nothing and survives someone widening the pattern later.
        if (!int.TryParse(match.Groups[2].ValueSpan, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var number))
            return;
        if (number is < MinHairNumber or > MaxHairNumber)
            return;

        flat.Add(number);
        var raceCode = match.Groups[1].Value;
        if (!byRace.TryGetValue(raceCode, out var set))
            byRace[raceCode] = set = [];
        set.Add(number);
    }

    // The range guard for EST evidence. File-path evidence is bounded in CollectFromFileKey by the
    // same two constants, so the range is defined once. h0000 parses cleanly to 0 and an Est SetId
    // can be any int; either would format into a subcategory that ModTypeFolders.IsHairNumber
    // rejects, and GetFolder throws on rejection. Out-of-range evidence is DISCARDED, not promoted
    // to a read failure: a stray h0000 beside a real h0115 should still file the mod under 115.
    private static void AddIfSupported(int number, HashSet<int> flat)
    {
        if (number is >= MinHairNumber and <= MaxHairNumber)
            flat.Add(number);
    }

    internal static void CollectFromManipulation(JsonElement item, HashSet<int> flat)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return;
        if (!item.TryGetProperty("Type", out var typeProp) || typeProp.ValueKind != JsonValueKind.String)
            return;
        if (typeProp.GetString() != "Est")
            return;
        if (!item.TryGetProperty("Manipulation", out var manipulation) || manipulation.ValueKind != JsonValueKind.Object)
            return;

        // Est covers customization slots generally - Hair and Face. Only Hair is ours.
        if (!manipulation.TryGetProperty("Slot", out var slotProp)
            || slotProp.ValueKind != JsonValueKind.String
            || slotProp.GetString() != "Hair")
            return;

        // SetId is the hair number. "Entry" on the same object is the EST skeleton row the hair
        // maps to - a different, equally plausible-looking integer, so reading it would be a
        // silent wrong answer rather than a visible failure.
        // TryGetInt32 also guards a SetId that is fractional or wider than an int.
        if (manipulation.TryGetProperty("SetId", out var setId)
            && setId.ValueKind == JsonValueKind.Number
            && setId.TryGetInt32(out var number))
            AddIfSupported(number, flat);
    }
}
