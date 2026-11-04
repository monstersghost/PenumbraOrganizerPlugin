using System.Text.Json;

namespace PenumbraOrganizer.Plugin.Organizer.BodyTypes;

public enum BodyTypeListParseStatus { Ok, MalformedJson, UnsupportedVersion }

/// <param name="DroppedLabels">
/// Entries Sanitize threw away, by label, or by a placeholder where the label itself was the
/// problem. Carried so a caller can TELL the user their file was partly ignored: silently dropping
/// an entry is how an extension mechanism fails without anyone noticing.
/// </param>
public sealed record BodyTypeListParseResult(
    BodyTypeListDocument? Data,
    BodyTypeListParseStatus Status,
    IReadOnlyList<string> DroppedLabels);

/// <summary>
/// The body-type list's JSON. Mirrors <c>NpcNameListCodec</c>'s contract exactly - never throws,
/// Data non-null exactly when Status is Ok, the two failure kinds kept distinct - so the two lists
/// behave the same way for a caller handling a corrupt file.
/// </summary>
public static class BodyTypeListCodec
{
    public const int CurrentVersion = 1;

    // A label becomes a folder name, so it is bounded well below any path limit. An alias is
    // matched against a mod title and has no reason to be longer than a label.
    private const int MaxLabelLength = 40;
    private const int MaxAliasLength = 64;

    private static readonly JsonSerializerOptions SerializeOptions = new() { WriteIndented = true };

    public static BodyTypeListParseResult Parse(string json)
    {
        if (json is null)
            return new BodyTypeListParseResult(null, BodyTypeListParseStatus.MalformedJson, []);

        BodyTypeListDocument? data;
        try
        {
            data = JsonSerializer.Deserialize<BodyTypeListDocument>(json);
        }
        catch (JsonException)
        {
            return new BodyTypeListParseResult(null, BodyTypeListParseStatus.MalformedJson, []);
        }

        if (data is null)
            return new BodyTypeListParseResult(null, BodyTypeListParseStatus.MalformedJson, []);
        if (data.Version != CurrentVersion)
            return new BodyTypeListParseResult(null, BodyTypeListParseStatus.UnsupportedVersion, []);

        var sanitized = Sanitize(data, out var dropped);
        return new BodyTypeListParseResult(sanitized, BodyTypeListParseStatus.Ok, dropped);
    }

    public static string Serialize(BodyTypeListDocument data) =>
        JsonSerializer.Serialize(Sanitize(data, out _), SerializeOptions);

    /// <summary>
    /// The one definition of a usable body label, shared with
    /// <c>ModTypeFolders.IsBodyTypeLabel</c> so the two can never disagree.
    /// </summary>
    /// <remarks>
    /// They DID disagree, and it was a real bug: this filter once checked only length and path
    /// separators while the folder builder also enforced a character whitelist, so a user file with
    /// a label containing <c>*</c>, <c>:</c>, an emoji or a pasted non-breaking space parsed
    /// cleanly and then threw out of <c>GetFolder</c> halfway through a sort - leaving some rows
    /// re-pathed, some not, and collision disambiguation skipped. One owner for the rule is the fix.
    /// <para>
    /// A label must carry at least one letter or digit, which is what rejects <c>"."</c> and
    /// <c>".."</c>. Punctuation alone is never a body name, and a dot-only label would otherwise
    /// become a path segment of its own.
    /// </para>
    /// <para>
    /// A character outside the Basic Multilingual Plane - an emoji - fails
    /// <see cref="char.IsLetterOrDigit(char)"/> on both halves of its surrogate pair and is
    /// rejected. That is deliberate: a folder name is not the place for one, and rejecting it in
    /// both validators is what matters.
    /// </para>
    /// </remarks>
    public static bool IsValidLabel(string? label) =>
        label is { Length: >= 1 and <= MaxLabelLength }
        && label.Trim() == label
        && label.Any(char.IsLetterOrDigit)
        && label.All(c => char.IsLetterOrDigit(c)
                          || c is ' ' or '+' or '-' or '&' or '\'' or '.' or '[' or ']');

    /// <summary>
    /// Normalizes every parse and serialize: labels trimmed, aliases lowercased and trimmed, blank
    /// and over-length entries dropped, aliases de-duplicated within an entry, and any entry left
    /// without a usable label or a usable alias dropped entirely.
    /// </summary>
    /// <remarks>
    /// Aliases are NOT de-duplicated across entries. Two bodies claiming the same alias is a real
    /// authoring mistake, and silently dropping one would hide it; the matcher resolves such a
    /// collision deterministically instead (see <c>BodyTypeMatcher</c>),
    /// <c>BodyTypeListStaticContentTests</c> fails on it so the bundled list can never ship one,
    /// and <c>BodyTypeListStore</c> warns when a USER entry collides with a shipped alias.
    /// </remarks>
    private static BodyTypeListDocument Sanitize(BodyTypeListDocument data, out IReadOnlyList<string> dropped)
    {
        var lost = new List<string>();
        var kept = (data.Bodies ?? [])
            .Select(b => new BodyTypeEntry
            {
                Label = b?.Label?.Trim() ?? string.Empty,
                Sex = b?.Sex?.Trim() ?? string.Empty,
                Race = b?.Race?.Trim() ?? string.Empty,
                Note = b?.Note?.Trim() ?? string.Empty,
                Source = b?.Source?.Trim() ?? string.Empty,
                Aliases = (b?.Aliases ?? [])
                    .Select(a => a?.Trim().ToLowerInvariant() ?? string.Empty)
                    .Where(a => a.Length > 0 && a.Length <= MaxAliasLength)
                    .Distinct(StringComparer.Ordinal)
                    .ToList(),
            })
            .Where(b =>
            {
                if (IsValidLabel(b.Label) && b.Aliases.Count > 0)
                    return true;
                lost.Add(b.Label.Length > 0 ? b.Label : "(entry with no label)");
                return false;
            })
            .ToList();

        dropped = lost;
        return new BodyTypeListDocument { Version = CurrentVersion, Bodies = kept };
    }
}
