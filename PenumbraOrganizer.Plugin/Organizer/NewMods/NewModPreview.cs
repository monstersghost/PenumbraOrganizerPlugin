namespace PenumbraOrganizer.Plugin.Organizer.NewMods;

/// <summary>One staged proposal: a mod whose proposed path differs from where it is.</summary>
/// <param name="Tool">"Heliosphere" or "Proteus" when a tool manages the mod (NewModPromptText.ToolTag), else null.</param>
public readonly record struct StagedMove(string Name, string CurrentPath, string ProposedPath, string? Tool = null);

/// <summary>One line of the confirm window: the mod, the folder it is in, the folder it would move to.</summary>
/// <param name="Tool">The managing tool's name, shown as a small tag beside the mod, or null.</param>
public sealed record NewModPreviewRow(string Name, string From, string To, string? Tool = null);

/// <summary>
/// What a staged new-mod (or sortroot) sort would do, for the confirm window. Pure data: built from the
/// staged moves after the scoped sort, before anything is applied. Plain ASCII.
/// </summary>
public sealed record NewModPreview(string Title, string ScopeText, IReadOnlyList<NewModPreviewRow> Rows)
{
    public const string RootLabel = "(root)";

    public int Count => Rows.Count;

    public static NewModPreview Build(IEnumerable<StagedMove> moves, string? scopeText, bool newMods)
    {
        var rows = moves
            .Select(m => new NewModPreviewRow(
                m.Name, FolderOf(m.CurrentPath, m.Name), FolderOf(m.ProposedPath, m.Name), m.Tool))
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.To, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var kind = newMods ? "new" : "loose";
        var title = $"Move {rows.Count} {kind} {(rows.Count == 1 ? "mod" : "mods")}?";
        return new NewModPreview(title, scopeText ?? string.Empty, rows);
    }

    /// <summary>
    /// The folder part of a Penumbra sort path. The mod's own name is stripped whole first, so a name
    /// containing a slash is not split; otherwise everything before the last slash. No folder is the root.
    /// </summary>
    public static string FolderOf(string path, string name)
    {
        var trimmed = path.Trim('/');
        string folder;
        if (trimmed.Length == 0 || trimmed == name)
            folder = string.Empty;
        else if (trimmed.EndsWith("/" + name, StringComparison.Ordinal))
            folder = trimmed[..^(name.Length + 1)];
        else
        {
            var slash = trimmed.LastIndexOf('/');
            folder = slash < 0 ? string.Empty : trimmed[..slash];
        }

        return folder.Length == 0 ? RootLabel : folder;
    }
}
