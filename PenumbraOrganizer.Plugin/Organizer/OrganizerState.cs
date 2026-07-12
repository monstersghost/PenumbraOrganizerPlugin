namespace PenumbraOrganizer.Plugin.Organizer;

public sealed class OrganizerState
{
    private readonly Dictionary<string, OrganizerModRow> _mods = new();

    public IReadOnlyList<OrganizerModRow> Mods =>
        _mods.Values.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public void LoadScan(IEnumerable<OrganizerModRow> scanned, IReadOnlySet<string> previouslyProtected)
    {
        _mods.Clear();
        foreach (var row in scanned)
        {
            // Heliosphere-managed mods are always re-protected on scan, even if a user
            // previously unprotected them — Heliosphere owns their location, so manual
            // unprotect only "sticks" for non-Heliosphere mods.
            row.Protected = row.HeliosphereManaged || previouslyProtected.Contains(row.Identifier);
            row.ProposedPath = row.CurrentPath;
            _mods[row.Identifier] = row;
        }
    }

    public void SetProtected(string identifier, bool value)
    {
        if (_mods.TryGetValue(identifier, out var row))
            row.Protected = value;
    }

    public void SetHeliosphereProtection(bool value)
    {
        foreach (var row in _mods.Values.Where(m => m.HeliosphereManaged))
            row.Protected = value;
    }

    public bool AssignManual(string identifier, string proposedPath)
    {
        if (!_mods.TryGetValue(identifier, out var row) || row.Protected)
            return false;

        row.ProposedPath = proposedPath;
        return true;
    }

    public int SortByCreator(Func<string, string> canonicalizeCreator)
    {
        var count = 0;
        foreach (var row in _mods.Values.Where(m => !m.Protected))
        {
            var folder = canonicalizeCreator(row.Author);
            row.ProposedPath = string.IsNullOrEmpty(folder) ? row.Name : $"{folder}/{row.Name}";
            count++;
        }

        return count;
    }

    public ReviewResult Validate()
    {
        var protectedViolations = _mods.Values
            .Where(m => m.Protected && !string.Equals(m.ProposedPath, m.CurrentPath, StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Identifier)
            .ToList();

        var collisions = _mods.Values
            .GroupBy(m => m.ProposedPath, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .ToDictionary(g => g.Key, g => g.Select(m => m.Identifier).ToList());

        return new ReviewResult(protectedViolations, collisions);
    }
}

public sealed record ReviewResult(
    IReadOnlyList<string> ProtectedViolations,
    IReadOnlyDictionary<string, List<string>> PathCollisions)
{
    public bool HasIssues => ProtectedViolations.Count > 0 || PathCollisions.Count > 0;
}
