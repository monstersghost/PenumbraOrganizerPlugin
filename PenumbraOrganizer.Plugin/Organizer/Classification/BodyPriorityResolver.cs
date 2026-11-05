using PenumbraOrganizer.Plugin.Organizer.BodyTypes;

namespace PenumbraOrganizer.Plugin.Organizer.Classification;

public enum BodyEvidenceTier { Content, Name, LibraryPath }

/// <summary>The body a mod files under, and which tier and source put it there.</summary>
public sealed record BodyResolution(string Label, BodyEvidenceTier Tier, BodyEvidenceSource Source);

/// <summary>
/// Picks one body from a mod's candidates. The mirror of <see cref="HairPriorityResolver"/>: pure,
/// no disk, applied at sort time against evidence the scan stored on the row.
/// </summary>
public static class BodyPriorityResolver
{
    public static BodyResolution? Resolve(
        BodyCandidates? candidates, BodyPriority priority, bool nameFirst = false)
    {
        if (candidates is null)
            return null;

        // A later tier is consulted only when every earlier one found nothing. User settings win
        // INSIDE a tier by rank; they do not let a noisy library path override clear content.
        //
        // nameFirst is for a Body-category mod, whose own title says which body it IS. Its option
        // groups name the bodies it is merely COMPATIBLE with ("Yet Another Body+" ships a Bibo+
        // group), so there the name outranks the content.
        if (nameFirst)
        {
            return Pick(candidates.Name, BodyEvidenceTier.Name, priority)
                ?? Pick(candidates.Content, BodyEvidenceTier.Content, priority)
                ?? Pick(candidates.LibraryPath, BodyEvidenceTier.LibraryPath, priority);
        }

        return Pick(candidates.Content, BodyEvidenceTier.Content, priority)
            ?? Pick(candidates.Name, BodyEvidenceTier.Name, priority)
            ?? Pick(candidates.LibraryPath, BodyEvidenceTier.LibraryPath, priority);
    }

    private static BodyResolution? Pick(
        IReadOnlyList<BodyCandidate> tier, BodyEvidenceTier which, BodyPriority priority)
    {
        if (tier.Count == 0)
            return null;

        // Rank first; ordinal label second so two labels the list does not know (both
        // int.MaxValue) never resolve by the order the scan happened to find them in.
        var best = tier
            .OrderBy(c => priority.Rank(c.Label))
            .ThenBy(c => c.Label, StringComparer.Ordinal)
            .First();
        return new BodyResolution(best.Label, which, best.Source);
    }
}
