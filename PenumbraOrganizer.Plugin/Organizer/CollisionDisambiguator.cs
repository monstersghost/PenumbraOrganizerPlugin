namespace PenumbraOrganizer.Plugin.Organizer;

/// <summary>
/// Renumbers ProposedPath collisions produced by the automatic sort strategies
/// (SortByCreator, SortByModType) when Penumbra duplicate installs share a display
/// Name. Never touches AssignManual's collisions - those are real user mistakes and
/// stay visible through OrganizerState.Validate() unchanged.
/// </summary>
public static class CollisionDisambiguator
{
    /// <param name="rows">The rows whose proposals may be renumbered.</param>
    /// <param name="reservedPaths">
    /// Paths already occupied by rows NOT passed in (a scoped sort's untouched rows). They are
    /// never renamed: any passed row proposed onto one is suffixed, and no suffix lands on one.
    /// Compared exactly (OrdinalIgnoreCase), the same comparison <c>OrganizerState.Validate</c>
    /// uses for collisions. Null means none, which is the behaviour before scoped sorts.
    /// </param>
    public static void Disambiguate(IEnumerable<OrganizerModRow> rows, IEnumerable<string>? reservedPaths = null)
    {
        var materialized = rows.ToList();

        var occupied = new HashSet<string>(reservedPaths ?? [], StringComparer.OrdinalIgnoreCase);

        var reserved = new HashSet<string>(
            materialized.Select(r => r.ProposedPath).Concat(occupied),
            StringComparer.OrdinalIgnoreCase);

        var groups = materialized
            .GroupBy(r => r.ProposedPath, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1 || occupied.Contains(g.Key));

        foreach (var group in groups)
        {
            var ordered = group.OrderBy(r => r.Identifier, StringComparer.Ordinal).ToList();

            // Minimal churn first: a row whose ProposedPath already equals its CurrentPath
            // needs no SetModPath call at all, so it keeps the bare path and everything
            // arriving gets suffixed. At most one row can qualify (CurrentPaths are unique).
            var inPlace = ordered
                .Where(r => string.Equals(r.ProposedPath, r.CurrentPath, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var exactMatches = ordered
                .Where(r => string.Equals(r.Identifier, r.Name, StringComparison.Ordinal))
                .ToList();
            // A path held by an untouched row keeps its occupant, so no passed row is canonical.
            var canonical = occupied.Contains(group.Key) ? null
                : inPlace.Count == 1 ? inPlace[0]
                : exactMatches.Count == 1 ? exactMatches[0]
                : ordered[0];
            var basePath = (canonical ?? ordered[0]).ProposedPath;
            var suffix = 2;

            foreach (var row in ordered.Where(r => !ReferenceEquals(r, canonical)))
            {
                string candidate;
                do { candidate = $"{basePath} ({suffix++})"; }
                while (!reserved.Add(candidate));
                row.ProposedPath = candidate;
            }
        }
    }
}
