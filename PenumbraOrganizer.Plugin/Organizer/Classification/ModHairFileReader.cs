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
    // a path that merely CONTAINS this shape. The c#### race code is matched so the path shape is
    // actually verified, then discarded - one hair shipped for eight clans is one number, not eight.
    //
    // [0-9], NOT \d: .NET's \d matches every Unicode decimal digit, so a path with fullwidth or
    // Devanagari digits would MATCH here and then throw FormatException inside the parse - an
    // exception the walker does not catch, so it would escape into the scan. This repo has been
    // bitten three times by a non-ASCII codepoint reaching code as something other than what it
    // looked like; this is that same hazard in regex form.
    private static readonly Regex HairPath = new(
        @"^chara/human/c[0-9]{4}/obj/hair/h([0-9]{4})/",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Returns every hair number found across the mod's files.
    /// <list type="bullet">
    /// <item>null - some file could not be read, parsed, or enumerated. An untrustworthy partial
    /// result, treated as "no subcategory", never as a confident (possibly wrong) answer.</item>
    /// <item>non-null, possibly empty - every file read successfully; empty means none carried
    /// recognized hair path or Est manipulation data.</item>
    /// </list>
    /// </summary>
    public static IReadOnlySet<int>? ReadHairNumbers(DirectoryInfo modDirectory)
    {
        var numbers = new HashSet<int>();

        var ok = ModConfigFileWalker.TryWalk(
            modDirectory,
            path => CollectFromFileKey(path, numbers),
            item => CollectFromManipulation(item, numbers));

        return ok ? numbers : null;
    }

    private static void CollectFromFileKey(string path, HashSet<int> numbers)
    {
        // Already '/'-normalised by the walker.
        var match = HairPath.Match(path);
        if (!match.Success)
            return;

        // TryParse, not Parse: four ASCII digits cannot overflow an int, but a defensive parse
        // costs nothing and survives someone widening the pattern later.
        if (int.TryParse(match.Groups[1].ValueSpan, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var number))
            AddIfSupported(number, numbers);
    }

    // The ONE place the supported range is enforced, for both evidence sources. h0000 parses
    // cleanly to 0 and an Est SetId can be any int; either would format into a subcategory that
    // ModTypeFolders.IsHairNumber rejects, and GetFolder throws on rejection - a crash inside the
    // sort rather than a mod quietly staying flat. Out-of-range evidence is DISCARDED, not
    // promoted to a read failure: a stray h0000 beside a real h0115 should still file the mod
    // under 115, and treating it as untrustworthy would throw away a good answer.
    private static void AddIfSupported(int number, HashSet<int> numbers)
    {
        if (number is >= MinHairNumber and <= MaxHairNumber)
            numbers.Add(number);
    }

    private static void CollectFromManipulation(JsonElement item, HashSet<int> numbers)
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
            AddIfSupported(number, numbers);
    }
}
