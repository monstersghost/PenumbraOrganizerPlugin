using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer.BodyTypes;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Organizer;

/// <summary>
/// The numbered layout of <c>Body/</c> when body mods are split:
/// <c>00. Skeleton</c>, <c>01. Physics</c>, <c>02. Tails, Ears and Horns</c> (shared, whatever the
/// body), then <c>10.</c> and up for the bodies that have body mods, ordered by the user's Body
/// priority, each holding <c>00. Replacer</c>, <c>01. SFW</c> and <c>02. Hands and Feet</c>.
/// </summary>
/// <remarks>
/// Pure and built once per sort from the rows the Body rule will place. Body numbering reuses
/// <see cref="GearFolderLayout"/>'s so the two layouts can never number bodies differently. Public
/// because the test project has no <c>InternalsVisibleTo</c>.
/// </remarks>
public sealed class BodyFolderLayout
{
    private BodyFolderLayout(IReadOnlyDictionary<string, int> bodyNumbers) => BodyNumbers = bodyNumbers;

    public IReadOnlyDictionary<string, int> BodyNumbers { get; }

    /// <param name="resolvedBodies">The body each body-dependent Body mod resolves to; null for none.</param>
    public static BodyFolderLayout Build(IEnumerable<string?> resolvedBodies, BodyPriority? priority) =>
        new(GearFolderLayout.Build(resolvedBodies, priority).BodyNumbers);

    public static string PartFolder(BodyPart part) => part switch
    {
        BodyPart.Skeleton => "00. Skeleton",
        BodyPart.Physics => "01. Physics",
        BodyPart.TailsEarsHorns => "02. Tails, Ears and Horns",
        BodyPart.Replacer => "00. Replacer",
        BodyPart.Sfw => "01. SFW",
        BodyPart.HandsAndFeet => "02. Hands and Feet",
        _ => throw new ArgumentOutOfRangeException(nameof(part), part, "Unknown body part."),
    };

    /// <summary>True for the parts that sit above the bodies and belong to no body.</summary>
    public static bool IsShared(BodyPart part) =>
        part is BodyPart.Skeleton or BodyPart.Physics or BodyPart.TailsEarsHorns;

    /// <summary>
    /// The folder below <c>Body/</c> for a mod's part and (optional) body, or null when the mod has
    /// no body and is not a shared part, which leaves it directly in <c>Body</c>.
    /// </summary>
    public string? Tail(BodyPart part, string? body)
    {
        if (IsShared(part))
            return PartFolder(part);
        if (body is null)
            return null;

        // Same validation of the label as the un-numbered path.
        _ = ModTypeFolders.GetFolder(ModCategory.Body, null, body);
        if (!BodyNumbers.TryGetValue(body, out var number))
            throw new InvalidOperationException(
                $"Body '{body}' was not part of the rows this layout was built from.");
        return $"{number:D2}. {body}/{PartFolder(part)}";
    }
}
