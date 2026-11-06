using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer.BodyTypes;

namespace PenumbraOrganizer.Plugin.Organizer.Classification;

/// <summary>
/// Which single body the enabled mods of a collection use. Only Body-category Replacer mods count:
/// gear and add-on parts say what they fit, not what the character wears.
/// </summary>
public static class ActiveCollectionBodyDetector
{
    public static string? Detect(
        IEnumerable<OrganizerModRow> rows, IReadOnlySet<string> enabledIdentifiers, BodyPriority priority)
    {
        var bodies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (row.Category != ModCategory.Body || row.BodyPart != BodyPart.Replacer)
                continue;
            if (!enabledIdentifiers.Contains(row.Identifier))
                continue;

            var body = row.BodyCandidates is not null
                ? BodyPriorityResolver.Resolve(row.BodyCandidates, priority, nameFirst: true)?.Label
                : row.BodyType;
            if (body is not null)
                bodies.Add(body);
        }
        return bodies.Count == 1 ? bodies.First() : null;
    }
}
