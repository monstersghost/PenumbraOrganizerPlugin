using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PenumbraOrganizer.Core.Classification;

namespace PenumbraOrganizer.Plugin.Organizer.Layouts;

/// <summary>
/// What the Sort engine needs from the category layouts beyond the split flags: the categories filed under a
/// folder other than their default, and the categories with a creator level of their own. Null wherever a
/// <c>CategoryPlacement?</c> is accepted means "every category at its default folder, following Group by", which
/// is the engine's behaviour from before layouts existed. <see cref="Create"/> never returns an empty instance, so
/// "nothing customised" has exactly one representation and a selection's value equality stays exact.
/// </summary>
public sealed class CategoryPlacement : IEquatable<CategoryPlacement>
{
    private readonly SortedDictionary<ModCategory, string> _folders;
    private readonly SortedDictionary<ModCategory, CreatorLevel> _creators;

    // The full, length-prefixed text of every customisation. Equality compares it; the fingerprint carries only
    // its hash (Digest), so 16 long folder names cannot blow up the staged export's fingerprint cell.
    private readonly string _text;

    private CategoryPlacement(SortedDictionary<ModCategory, string> folders, SortedDictionary<ModCategory, CreatorLevel> creators)
    {
        _folders = folders;
        _creators = creators;
        _text = string.Join(';', LayoutCatalog.Categories
            .Where(c => folders.ContainsKey(c) || creators.ContainsKey(c))
            .Select(c =>
            {
                var folder = folders.GetValueOrDefault(c);
                var creator = creators.TryGetValue(c, out var level)
                    ? ((int)level).ToString(CultureInfo.InvariantCulture)
                    : "-";
                // Length-prefixed, so no character inside a folder name can make two placements read alike.
                return string.Create(CultureInfo.InvariantCulture, $"{(int)c}:{creator}:{folder?.Length ?? -1}:{folder}");
            }));
        Digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_text)));
        RenamedFolders = [.. folders.Values];
    }

    /// <summary>A short stable text (SHA-256 hex) of every customisation, for <c>SortSelection.Fingerprint</c>.</summary>
    public string Digest { get; }

    /// <summary>Every configured folder that differs from its category's default, in category order.</summary>
    public IReadOnlyList<string> RenamedFolders { get; }

    /// <summary>
    /// The placement for these folders and creator levels, or null when there is nothing to place differently. A
    /// folder equal to its category's default is dropped. Folders are taken as given (validated where they are
    /// read from the config).
    /// </summary>
    public static CategoryPlacement? Create(
        IReadOnlyDictionary<ModCategory, string>? folders, IReadOnlyDictionary<ModCategory, CreatorLevel>? creators)
    {
        var keptFolders = new SortedDictionary<ModCategory, string>();
        foreach (var (category, folder) in folders ?? new Dictionary<ModCategory, string>())
        {
            if (!LayoutCatalog.IsKnown(category) || string.IsNullOrEmpty(folder))
                continue;
            if (!string.Equals(folder, LayoutCatalog.DefaultFolder(category), StringComparison.Ordinal))
                keptFolders[category] = folder;
        }

        var keptCreators = new SortedDictionary<ModCategory, CreatorLevel>();
        foreach (var (category, level) in creators ?? new Dictionary<ModCategory, CreatorLevel>())
        {
            if (LayoutCatalog.IsKnown(category))
                keptCreators[category] = level;
        }

        return keptFolders.Count == 0 && keptCreators.Count == 0 ? null : new CategoryPlacement(keptFolders, keptCreators);
    }

    /// <summary>The placement a profile describes; null when every folder is the default and every level follows Group by.</summary>
    public static CategoryPlacement? From(LayoutProfile profile) => Create(
        LayoutCatalog.Categories.ToDictionary(c => c, c => profile[c].Folder),
        LayoutCatalog.Categories.Where(c => profile[c].Creator is not null).ToDictionary(c => c, c => profile[c].Creator!.Value));

    /// <summary>The configured folder, or null when the category keeps its default (or there is no category).</summary>
    public string? FolderFor(ModCategory? category) =>
        category is { } c && _folders.TryGetValue(c, out var folder) ? folder : null;

    /// <summary>The category's own creator level, or null when it follows Group by.</summary>
    public CreatorLevel? CreatorFor(ModCategory? category) =>
        category is { } c && _creators.TryGetValue(c, out var level) ? level : null;

    /// <summary>
    /// Whether <paramref name="path"/> is one of the renamed category folders or lies inside one, on a segment
    /// boundary and ignoring case (as the sorter's own folder names are matched). A renamed root is a sorter
    /// folder: never a sort-inside or tool anchor.
    /// </summary>
    public bool IsAtOrInsideRenamedFolder(string path)
    {
        foreach (var folder in RenamedFolders)
        {
            if (IsAtOrInside(path, folder))
                return true;
        }

        return false;
    }

    /// <summary>Whether <paramref name="path"/> equals <paramref name="folder"/> or lies inside it (segment boundary, any case).</summary>
    public static bool IsAtOrInside(string path, string folder) =>
        path.Length >= folder.Length
        && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
        && (path.Length == folder.Length || path[folder.Length] == '/');

    public bool Equals(CategoryPlacement? other) =>
        other is not null && (ReferenceEquals(this, other) || string.Equals(_text, other._text, StringComparison.Ordinal));

    public override bool Equals(object? obj) => Equals(obj as CategoryPlacement);

    public override int GetHashCode() => Digest.GetHashCode(StringComparison.Ordinal);
}
