namespace PenumbraOrganizer.Plugin.Organizer.Classification;

/// <summary>
/// The races a mod's customization changed-item keys name, grouped by the part they belong to. A key is
/// filed by (body part, subtype), so a face mod's incidental skin keys never add a race to Face.
/// </summary>
public sealed record RowRaceData(
    IReadOnlySet<string> Face,
    IReadOnlySet<string> Iris,
    IReadOnlySet<string> Skin,
    IReadOnlySet<string> TailEars,
    bool IrisOnly);

public static class CustomizationRaceReader
{
    // Real key parts: "Face", "Skin Textures", "Tail", "Ears". Horns: confirmed in the step 0 checklist.
    private static readonly string[] TailEarParts = ["Tail", "Ears"];

    public static RowRaceData From(IEnumerable<ChangedItemKey> keys)
    {
        var face = new HashSet<string>(StringComparer.Ordinal);
        var iris = new HashSet<string>(StringComparer.Ordinal);
        var skin = new HashSet<string>(StringComparer.Ordinal);
        var tailEars = new HashSet<string>(StringComparer.Ordinal);
        var hasIrisKey = false;
        var hasOtherFaceKey = false;

        foreach (var key in keys)
        {
            if (key.Shape != ChangedItemKeyShape.Customization)
                continue;

            var part = key.BodyPart ?? string.Empty;
            var race = RaceNames.IsKnown(key.Race) ? key.Race : null;

            if (part == "Face")
            {
                if (key.Subtype == "Iris")
                {
                    hasIrisKey = true;
                    if (race is not null)
                        iris.Add(race);
                }
                else
                {
                    hasOtherFaceKey = true;
                    if (race is not null)
                        face.Add(race);
                }
            }
            else if (part.Contains("Skin", StringComparison.Ordinal))
            {
                if (race is not null)
                    skin.Add(race);
            }
            else if (TailEarParts.Contains(part, StringComparer.Ordinal))
            {
                if (race is not null)
                    tailEars.Add(race);
            }
        }

        return new RowRaceData(face, iris, skin, tailEars, IrisOnly: hasIrisKey && !hasOtherFaceKey);
    }
}
