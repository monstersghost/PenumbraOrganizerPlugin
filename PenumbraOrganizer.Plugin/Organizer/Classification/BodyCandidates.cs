namespace PenumbraOrganizer.Plugin.Organizer.Classification;

/// <summary>Where a body candidate came from. Recorded so a dump can explain a placement.</summary>
public enum BodyEvidenceSource { Group, Option, FilePath, Name, LibraryPath }

public readonly record struct BodyCandidate(string Label, BodyEvidenceSource Source);

/// <summary>
/// Every body a mod could be filed under, by tier. Stored on the row at scan time and resolved
/// against the user's priority at SORT time, exactly as hair evidence is - so reordering the
/// priority never needs a rescan.
/// </summary>
public sealed class BodyCandidates
{
    /// <summary>Tier 1: inside the mod - group names, option names, first folder of file values.</summary>
    public IReadOnlyList<BodyCandidate> Content { get; init; } = [];

    /// <summary>Tier 2: the mod's own name.</summary>
    public IReadOnlyList<BodyCandidate> Name { get; init; } = [];

    /// <summary>Tier 2.5: the folders above the mod in the library. Least reliable.</summary>
    public IReadOnlyList<BodyCandidate> LibraryPath { get; init; } = [];

    /// <summary>The mod's config could not be read. Distinct from "read fine, found no body".</summary>
    public bool ContentReadFailed { get; init; }

    public string Describe() =>
        $"content=[{Join(Content)}] name=[{Join(Name)}] library=[{Join(LibraryPath)}] "
        + $"contentRead={(ContentReadFailed ? "FAILED" : "ok")}";

    private static string Join(IReadOnlyList<BodyCandidate> list) =>
        string.Join(", ", list.Select(c => $"{c.Label} ({c.Source})"));
}

public static class BodyCandidateBuilder
{
    public static BodyCandidates Build(
        BodyTypeMatcher matcher, string modName, string libraryPath,
        ModContent? content, bool contentReadFailed)
    {
        var tier1 = new List<BodyCandidate>();
        if (content is not null)
        {
            foreach (var name in content.GroupNames)
                AddAll(tier1, matcher.MatchAll(name), BodyEvidenceSource.Group);
            foreach (var name in content.OptionNames)
                AddAll(tier1, matcher.MatchAll(name), BodyEvidenceSource.Option);
            foreach (var value in content.FileValues)
            {
                if (FirstFolder(value) is { } folder)
                    AddAll(tier1, matcher.MatchAll(folder), BodyEvidenceSource.FilePath);
            }
        }

        var tier2 = new List<BodyCandidate>();
        AddAll(tier2, matcher.MatchAll(modName), BodyEvidenceSource.Name);

        var tier25 = new List<BodyCandidate>();
        var segments = libraryPath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        // Every folder ABOVE the mod; the last segment is the mod's own name, which Tier 2 covers.
        for (var i = 0; i < segments.Length - 1; i++)
            AddAll(tier25, matcher.MatchAll(segments[i]), BodyEvidenceSource.LibraryPath);

        return new BodyCandidates
        {
            Content = tier1,
            Name = tier2,
            LibraryPath = tier25,
            ContentReadFailed = contentReadFailed && content is null,
        };
    }

    /// <summary>
    /// The first folder of a mod-relative path, or null when there is none. Authors who ship
    /// per-body variants name the TOP folder ("Prestige YAB+/top.mdl"); a body name deeper down
    /// ("shared/Bibo/file.tex") or in a root file name is too noisy to count.
    /// </summary>
    public static string? FirstFolder(string modRelativePath)
    {
        var normalized = modRelativePath.Replace('\\', '/');
        var slash = normalized.IndexOf('/');
        return slash <= 0 ? null : normalized[..slash];
    }

    // One candidate per label within a tier; the first source to name a body keeps the credit.
    private static void AddAll(List<BodyCandidate> into, IReadOnlyList<string> labels, BodyEvidenceSource source)
    {
        foreach (var label in labels)
        {
            if (!into.Exists(c => c.Label == label))
                into.Add(new BodyCandidate(label, source));
        }
    }
}
