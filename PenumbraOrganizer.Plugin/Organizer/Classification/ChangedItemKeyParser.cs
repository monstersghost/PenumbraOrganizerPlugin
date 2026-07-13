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

    // e.g. "Smallclothes (NPC, 9903-1, Body)" — id and slot are not parsed further.
    private static readonly Regex NpcSuffix = new(@"\(NPC, [^)]*\)$", RegexOptions.Compiled);

    public static ChangedItemKey Parse(string key)
    {
        if (key.StartsWith(CustomizationPrefix, StringComparison.Ordinal))
            return new(ChangedItemKeyShape.Customization, key);

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
}
