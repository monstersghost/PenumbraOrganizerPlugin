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
}
