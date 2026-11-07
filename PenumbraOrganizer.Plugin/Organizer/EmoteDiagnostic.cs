using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Organizer;

/// <summary>Temporary measuring aid: how emote mods would split, written to dalamud.log after a scan.</summary>
public static class EmoteDiagnostic
{
    public static IReadOnlyList<string> Summarize(IEnumerable<OrganizerModRow> rows)
    {
        var emoteRows = rows.Where(r => r.Category == ModCategory.Animation && r.SubCategory == "Emotes").ToList();
        if (emoteRows.Count == 0)
            return [];

        int single = 0, multiple = 0, expressions = 0, undetermined = 0;
        var perName = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in emoteRows)
        {
            switch (EmoteFolders.Tail(row.EmoteNames))
            {
                case EmoteFolders.Multiple: multiple++; break;
                case EmoteFolders.ExpressionsFolder: expressions++; break;
                case EmoteFolders.Undetermined: undetermined++; break;
                default: single++; break;
            }
            foreach (var name in row.EmoteNames ?? (IReadOnlySet<string>)new HashSet<string>())
                perName[name] = perName.GetValueOrDefault(name) + 1;
        }

        var lines = new List<string>
        {
            $"[emote-diag] {emoteRows.Count} emote mods: Single={single} Multiple={multiple} " +
            $"Expressions={expressions} Undetermined={undetermined}",
        };
        lines.AddRange(perName.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => $"[emote-diag] {p.Key} x{p.Value}"));
        return lines;
    }
}
