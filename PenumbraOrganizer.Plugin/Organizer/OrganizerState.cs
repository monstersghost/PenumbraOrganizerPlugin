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
}
