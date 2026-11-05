using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer.BodyTypes;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Organizer;

/// <summary>
/// The numbered layout of <c>Gear/</c> when both the slot split and the body split are on:
/// <c>00. Sets</c>, <c>01.</c>-<c>09.</c> slot folders, then <c>10.</c> and up for the bodies that
/// have gear, ordered by the user's Body priority, each holding the same numbered slot folders.
/// </summary>
/// <remarks>
/// Pure and built once per sort from the rows that will be placed by the gear rule, so the numbers
/// are contiguous for exactly those rows. Public because the test project has no
/// <c>InternalsVisibleTo</c>.
/// </remarks>
public sealed class GearFolderLayout
{
    public const int FirstBodyNumber = 10;

    // Order and numbers match ModTypeFolders' enumerated gear slots.
    private static readonly string[] SlotOrder =
        ["Head", "Top", "Hands", "Legs", "Feet", "Ears", "Neck", "Wrists", "Rings"];

    private const string SetsFolder = "00. Sets";

    private GearFolderLayout(IReadOnlyDictionary<string, int> bodyNumbers) => BodyNumbers = bodyNumbers;

    /// <summary>Body label to its folder number, for the bodies the layout was built from.</summary>
    public IReadOnlyDictionary<string, int> BodyNumbers { get; }

    /// <param name="resolvedBodies">The body each placed gear mod resolves to; null for none.</param>
    /// <param name="priority">Null orders bodies by label alone.</param>
    public static GearFolderLayout Build(IEnumerable<string?> resolvedBodies, BodyPriority? priority)
    {
        var ordered = resolvedBodies
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .OrderBy(label => priority?.Rank(label) ?? int.MaxValue)
            .ThenBy(label => label, StringComparer.Ordinal)
            .ToList();

        var numbers = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < ordered.Count; i++)
            numbers[ordered[i]] = FirstBodyNumber + i;

        return new GearFolderLayout(numbers);
    }

    /// <summary>The slot's folder name: <c>02. Top</c>, or <c>00. Sets</c> when no single slot resolved.</summary>
    public static string SlotFolder(string? slot)
    {
        if (slot is null)
            return SetsFolder;

        var index = Array.IndexOf(SlotOrder, slot);
        return index >= 0
            ? $"{index + 1:D2}. {slot}"
            : throw new ArgumentOutOfRangeException(nameof(slot), slot, $"Unsupported gear slot '{slot}'.");
    }

    /// <summary>The full <c>Gear/...</c> folder for a gear mod's slot and (optional) body.</summary>
    public string Folder(string? slot, string? body)
    {
        // Reuses GetFolder's validation of the slot and the body label, so a malformed value fails
        // here exactly as it would have without numbering.
        _ = ModTypeFolders.GetFolder(ModCategory.Gear, slot, body);

        var slotFolder = SlotFolder(slot);
        if (body is null)
            return $"Gear/{slotFolder}";

        if (!BodyNumbers.TryGetValue(body, out var number))
            throw new InvalidOperationException(
                $"Body '{body}' was not part of the rows this layout was built from.");

        return $"Gear/{number:D2}. {body}/{slotFolder}";
    }
}
