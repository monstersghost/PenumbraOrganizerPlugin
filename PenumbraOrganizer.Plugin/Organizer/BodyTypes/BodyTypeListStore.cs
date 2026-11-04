using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Organizer.BodyTypes;

/// <param name="Warnings">
/// Everything the user should be told about their own list, or empty. A LIST and not one string,
/// deliberately: unlike the NPC list, where exactly one thing can be wrong per load, a body list
/// can simultaneously have entries dropped for a malformed label AND aliases that collide with
/// shipped ones. Reporting only the first would hide the second.
/// </param>
public sealed record BodyTypeListLoadResult(BodyTypeListDocument Document, IReadOnlyList<string> Warnings);

/// <summary>
/// The body-type list's source: the bundled list, plus the user's own file when they have written
/// one. Never throws for an on-disk problem and never writes.
/// </summary>
/// <remarks>
/// Deliberately much smaller than <c>NpcNameListStore</c>. That type carries a legacy-file
/// migration, an opt-in second list, a seeding write and two size ceilings, all of which exist
/// because a wiki scrape produces ~20,000 names and one build shipped a file that crashed the game.
/// This list is 26 bodies: there is no scrape, nothing to migrate, and a matcher built from it
/// costs nothing, so none of that machinery is reproduced here. The one thing kept is the reason it
/// existed - a user file that lets a new body be added without waiting for a plugin release.
/// </remarks>
public static class BodyTypeListStore
{
    /// <summary>The user's own additions, read if present and never created.</summary>
    public const string UserFileName = "body-type-list.json";

    /// <summary>
    /// A ceiling on the user file, so a runaway or pasted-in-wrong file cannot build an enormous
    /// matcher. Far above any plausible real list and far below a size that would cost anything.
    /// </summary>
    public const int MaxUserBodyCount = 500;

    public static string UserListPath(string configDirectory) =>
        Path.Combine(configDirectory, UserFileName);

    /// <summary>
    /// The bundled curated list, from this assembly's embedded resources.
    /// </summary>
    /// <remarks>
    /// Resolved from <c>typeof(BodyTypeListStore)</c> for the same reason
    /// <c>NpcNameListStore.ReadEmbeddedStaticList</c> does: <c>ScanProcessor.Prepare</c> calls this
    /// off the framework thread, and reaching the type that holds every Dalamud static from there
    /// would be a purity violation <c>LibraryWorkPurityTests</c> cannot see, since it checks
    /// signatures only.
    /// <para>
    /// The file must be saved WITHOUT a UTF-8 BOM. <c>BodyTypeListCodec.Parse</c> never throws, so a
    /// BOM would silently leave every scan with no body types at all rather than failing loudly.
    /// </para>
    /// </remarks>
    public static string ReadEmbeddedStaticList()
    {
        const string resourceName =
            "PenumbraOrganizer.Plugin.Organizer.BodyTypes.body-type-list-static.json";
        using var stream = typeof(BodyTypeListStore).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>The bundled list unioned with the user's file, if they have one.</summary>
    public static BodyTypeListLoadResult LoadForMatching(string configDirectory)
    {
        var staticParse = BodyTypeListCodec.Parse(ReadEmbeddedStaticList());
        if (staticParse.Status != BodyTypeListParseStatus.Ok)
            throw new InvalidOperationException(
                "Bundled static body-type list is not valid JSON - this is a packaging bug, not a runtime condition.");
        if (staticParse.DroppedLabels.Count > 0)
            throw new InvalidOperationException(
                "Bundled static body-type list has entries the codec rejects ("
                + string.Join(", ", staticParse.DroppedLabels)
                + ") - a packaging bug, not a runtime condition.");
        var bundled = staticParse.Data!;

        var userPath = UserListPath(configDirectory);

        // Not created when absent, unlike NpcNameListStore.Load's seeding branch: a user who has
        // never added a body should not find a file they have to maintain.
        if (!File.Exists(userPath))
            return new BodyTypeListLoadResult(bundled, []);

        string contents;
        try
        {
            contents = File.ReadAllText(userPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new BodyTypeListLoadResult(bundled,
                [$"{UserFileName} could not be read ({ex.GetType().Name}); "
                 + "using the bundled body-type list for this session."]);
        }

        var parse = BodyTypeListCodec.Parse(contents);
        switch (parse.Status)
        {
            case BodyTypeListParseStatus.MalformedJson:
                return new BodyTypeListLoadResult(bundled,
                    [$"{userPath} is not valid JSON; using the bundled body-type list for this session."]);

            case BodyTypeListParseStatus.UnsupportedVersion:
                return new BodyTypeListLoadResult(bundled,
                    [$"{userPath} has an unsupported Version; using the bundled body-type list for this session."]);
        }

        var user = parse.Data!;
        if (user.Bodies.Count > MaxUserBodyCount)
        {
            // Warn and fall back in memory only; the file is left exactly as it is. Nothing here
            // backs up or overwrites the user's file the way NpcNameListStore.Load does for an
            // oversized scrape - that behaviour exists to disarm a crash, and this list cannot
            // cause one.
            return new BodyTypeListLoadResult(bundled,
                [$"{UserFileName} holds {user.Bodies.Count:N0} bodies, more than the "
                 + $"{MaxUserBodyCount:N0} this version will build a matcher from. Using the bundled "
                 + "list for this session; the file has not been changed."]);
        }

        // Both kinds of problem are reported, not just the first: an entry can be dropped for a
        // malformed label while a different entry's alias collides with a shipped one.
        var warnings = new List<string>();
        if (parse.DroppedLabels.Count > 0)
            warnings.Add($"{UserFileName}: {parse.DroppedLabels.Count} entr"
                + (parse.DroppedLabels.Count == 1 ? "y was" : "ies were")
                + " ignored because the label is unusable as a folder name ("
                + string.Join(", ", parse.DroppedLabels)
                + "). A label needs at least one letter or digit and may only use spaces, "
                + "+ - & ' . [ ] besides letters and digits.");

        var merged = Union(bundled, user);
        var shadowed = CollidingAliases(bundled, user);
        if (shadowed.Count > 0)
            warnings.Add($"{UserFileName}: {shadowed.Count} alias"
                + (shadowed.Count == 1 ? " is" : "es are")
                + " already used by a different body in the bundled list ("
                + string.Join(", ", shadowed)
                + "). Which body wins is decided by name order, so one of the two will never "
                + "match. Rename the alias, or give your entry the bundled body's exact Label to "
                + "replace it instead of competing with it.");

        return new BodyTypeListLoadResult(merged, warnings);
    }

    /// <summary>
    /// The bundled list with the user's entries added. A user entry whose label matches a bundled
    /// one REPLACES it, so a user can correct a shipped body's aliases; any other entry is appended.
    /// </summary>
    /// <remarks>
    /// Replace-by-label rather than union-of-aliases: a user correcting an alias that turned out to
    /// be wrong - the whole reason a loose word like "eve" is worth being able to withdraw - must be
    /// able to REMOVE it, which unioning could never do.
    /// </remarks>
    private static BodyTypeListDocument Union(BodyTypeListDocument bundled, BodyTypeListDocument user)
    {
        var replaced = new HashSet<string>(user.Bodies.Select(b => b.Label), StringComparer.OrdinalIgnoreCase);

        return new BodyTypeListDocument
        {
            Version = BodyTypeListCodec.CurrentVersion,
            Bodies = [.. bundled.Bodies.Where(b => !replaced.Contains(b.Label)), .. user.Bodies],
        };
    }

    /// <summary>
    /// Aliases a user entry claims that a DIFFERENT bundled body already claims.
    /// </summary>
    /// <remarks>
    /// Not an error, and not repaired: the matcher resolves a collision deterministically by its
    /// ordinal label tie-break. But the outcome is arbitrary from the user's point of view - their
    /// new body silently never matches, or it silently shadows a shipped one - so it is worth
    /// saying out loud. An entry that REPLACES a bundled body by sharing its Label is excluded,
    /// since that is the supported way to correct a shipped body's aliases.
    /// </remarks>
    private static IReadOnlyList<string> CollidingAliases(
        BodyTypeListDocument bundled, BodyTypeListDocument user)
    {
        var replacedLabels = new HashSet<string>(user.Bodies.Select(b => b.Label), StringComparer.OrdinalIgnoreCase);
        var bundledAliases = bundled.Bodies
            .Where(b => !replacedLabels.Contains(b.Label))
            .SelectMany(b => b.Aliases)
            .ToHashSet(StringComparer.Ordinal);

        return user.Bodies
            .SelectMany(b => b.Aliases)
            .Where(bundledAliases.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public static BodyTypeMatcher BuildMatcher(BodyTypeListDocument document) => new(document.Bodies);
}
