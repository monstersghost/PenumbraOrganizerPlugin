using PenumbraOrganizer.Plugin.Organizer.Workbook;

namespace PenumbraOrganizer.Plugin.Organizer.Layouts;

/// <summary>
/// The rule a category folder name obeys: the same structure checks a rule or workbook folder uses (relative, no
/// <c>.</c> or <c>..</c>, no empty part, at most 260 characters, backslashes read as <c>/</c>), and no braces,
/// because a category folder is literal text and a <c>{token}</c> in it would be expanded wherever it is used as a
/// workbook code.
/// </summary>
public static class LayoutFolderName
{
    public const string BracesError = "A category folder cannot contain { or }.";
    public const string EmptyError = "Folder is empty.";

    /// <summary>
    /// Trimmed, backslashes turned into <c>/</c>, outer slashes removed, and every segment trimmed. Penumbra trims
    /// folder names, so an untrimmed segment (<c>Equipment / Gear</c>) would never match the folder Penumbra
    /// keeps and the Sort would propose the same move on every pass.
    /// </summary>
    public static string Normalize(string? raw)
    {
        var path = DestinationResolver.NormalizePath(raw ?? string.Empty);
        return string.Join('/', path.Split('/').Select(segment => segment.Trim()));
    }

    /// <summary>Why <paramref name="raw"/> cannot be a category folder, or null when it can (after <see cref="Normalize"/>).</summary>
    public static string? Error(string? raw)
    {
        var folder = Normalize(raw);
        if (folder.Length == 0)
            return EmptyError;
        if (folder.Contains('{') || folder.Contains('}'))
            return BracesError;
        return DestinationResolver.StructureError(folder);
    }
}
