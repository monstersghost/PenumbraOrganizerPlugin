using System.Text.Json;

namespace PenumbraOrganizer.Plugin.Organizer.BodyTypes;

public enum BodyTypeListParseStatus { Ok, MalformedJson, UnsupportedVersion }

public sealed record BodyTypeListParseResult(BodyTypeListDocument? Data, BodyTypeListParseStatus Status);

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
            return new BodyTypeListParseResult(null, BodyTypeListParseStatus.MalformedJson);

        BodyTypeListDocument? data;
        try
        {
            data = JsonSerializer.Deserialize<BodyTypeListDocument>(json);
        }
        catch (JsonException)
        {
            return new BodyTypeListParseResult(null, BodyTypeListParseStatus.MalformedJson);
        }

        if (data is null)
            return new BodyTypeListParseResult(null, BodyTypeListParseStatus.MalformedJson);
        if (data.Version != CurrentVersion)
            return new BodyTypeListParseResult(null, BodyTypeListParseStatus.UnsupportedVersion);

        return new BodyTypeListParseResult(Sanitize(data), BodyTypeListParseStatus.Ok);
    }

    public static string Serialize(BodyTypeListDocument data) =>
        JsonSerializer.Serialize(Sanitize(data), SerializeOptions);

    /// <summary>
    /// Normalizes every parse and serialize: labels trimmed, aliases lowercased and trimmed, blank
    /// and over-length entries dropped, aliases de-duplicated within an entry, and any entry left
    /// with no usable label or no usable alias dropped entirely.
    /// </summary>
    /// <remarks>
    /// A label carrying a path separator is dropped rather than repaired. It would otherwise reach
    /// <c>ModTypeFolders.GetFolder</c> and silently add a level below the body folder - a
    /// "Gear/Bibo+/Extra/Top" nobody asked for; <c>IsBodyTypeLabel</c> is the second guard, and this
    /// is the first.
    /// <para>
    /// Aliases are NOT de-duplicated across entries. Two bodies claiming the same alias is a real
    /// authoring mistake, and silently dropping one would hide it; the matcher resolves such a
    /// collision deterministically instead (see <c>BodyTypeMatcher</c>), and
    /// <c>BodyTypeListStaticContentTests</c> fails on it so the bundled list can never ship one.
    /// </para>
    /// </remarks>
    private static BodyTypeListDocument Sanitize(BodyTypeListDocument data) => new()
    {
        Version = CurrentVersion,
        Bodies = (data.Bodies ?? [])
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
            .Where(b => b.Label.Length > 0
                        && b.Label.Length <= MaxLabelLength
                        && !b.Label.Contains('/')
                        && !b.Label.Contains('\\')
                        && b.Aliases.Count > 0)
            .ToList(),
    };
}
