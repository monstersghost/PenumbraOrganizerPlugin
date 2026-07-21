using System.Text.RegularExpressions;

namespace PenumbraOrganizer.Plugin.Organizer.Classification;

public static class ChangedItemKeyParser
{
    private const string CustomizationPrefix = "Customization: ";
    private const string EmotePrefix = "Emote: ";
    private const string ActionPrefix = "Action: ";
    private const string IconPrefix = "Icon: ";
    private const string MountSuffix = " (Mount)";

    private static readonly string[] MinionSuffixes = [" (Battle NPC)", " (Companion)", " (Event NPC)"];
    private static readonly string[] CategoryLiterals = ["Animation", "Vfx", "Sound", "Housing"];
    private static readonly string[] Genders = ["Female", "Male"];

    // e.g. "Smallclothes (NPC, 9903-1, Body)" — id and slot are not parsed further.
    private static readonly Regex NpcSuffix = new(@"\(NPC, [^)]*\)$", RegexOptions.Compiled);

    public static ChangedItemKey Parse(string key)
    {
        if (key.StartsWith(CustomizationPrefix, StringComparison.Ordinal))
            return ParseCustomization(key);

        if (key.StartsWith(EmotePrefix, StringComparison.Ordinal))
            return new(ChangedItemKeyShape.Emote, key, ItemName: key[EmotePrefix.Length..]);

        if (key.StartsWith(ActionPrefix, StringComparison.Ordinal))
            return new(ChangedItemKeyShape.Action, key, ItemName: key[ActionPrefix.Length..]);

        if (key.StartsWith(IconPrefix, StringComparison.Ordinal))
            return new(ChangedItemKeyShape.Icon, key);

        if (CategoryLiterals.Contains(key, StringComparer.Ordinal))
            return new(ChangedItemKeyShape.CategoryLiteral, key, CategoryLiteral: key);

        if (key.EndsWith(MountSuffix, StringComparison.Ordinal))
            return new(ChangedItemKeyShape.Mount, key, ItemName: key[..^MountSuffix.Length]);

        foreach (var suffix in MinionSuffixes)
        {
            if (key.EndsWith(suffix, StringComparison.Ordinal))
                return new(ChangedItemKeyShape.Minion, key, ItemName: key[..^suffix.Length]);
        }

        if (NpcSuffix.IsMatch(key))
            return new(ChangedItemKeyShape.Npc, key);

        return new(ChangedItemKeyShape.Gear, key, ItemName: key);
    }

    // Payload grammar per spec: "{Race} {Gender} {BodyPart}[ (Subtype)][ Number]",
    // or "Player {BodyPart}" (no race/gender), or the literal "Unknown".
    // Race may be multi-word ("Au Ra"), so the gender token is the anchor.
    private static ChangedItemKey ParseCustomization(string key)
    {
        var payload = key[CustomizationPrefix.Length..];
        var tokens = payload.Split(' ');

        string? race = null, gender = null;
        string[] bodyTokens;

        var genderIndex = Array.FindIndex(tokens, t => Genders.Contains(t, StringComparer.Ordinal));
        if (genderIndex > 0)
        {
            race = string.Join(' ', tokens[..genderIndex]);
            gender = tokens[genderIndex];
            bodyTokens = tokens[(genderIndex + 1)..];
        }
        else if (tokens.Length > 1 && tokens[0] == "Player")
        {
            bodyTokens = tokens[1..];
        }
        else
        {
            bodyTokens = tokens;
        }

        int? number = null;
        if (bodyTokens.Length > 0 && int.TryParse(bodyTokens[^1], out var parsedNumber))
        {
            number = parsedNumber;
            bodyTokens = bodyTokens[..^1];
        }

        // Subtype is usually trailing ("Face (Iris)"), but the "(Child)" race-variant marker
        // appears leading, right after gender ("Female (Child) Face") — check leading first.
        string? subtype = null;
        if (bodyTokens.Length > 0 && bodyTokens[0].StartsWith('(') && bodyTokens[0].EndsWith(')'))
        {
            subtype = bodyTokens[0][1..^1];
            bodyTokens = bodyTokens[1..];
        }
        else if (bodyTokens.Length > 0 && bodyTokens[^1].StartsWith('(') && bodyTokens[^1].EndsWith(')'))
        {
            subtype = bodyTokens[^1][1..^1];
            bodyTokens = bodyTokens[..^1];
        }

        var bodyPart = bodyTokens.Length > 0 ? string.Join(' ', bodyTokens) : null;

        return new(ChangedItemKeyShape.Customization, key,
            Race: race, Gender: gender, BodyPart: bodyPart, Subtype: subtype, Number: number);
    }
}
