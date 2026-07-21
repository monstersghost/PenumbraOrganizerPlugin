using System.Text.Json;

namespace PenumbraOrganizer.Plugin.Organizer.NpcNames;

public enum NpcNameListParseStatus { Ok, MalformedJson, UnsupportedVersion }

public sealed record NpcNameListParseResult(NpcNameListDocument? Data, NpcNameListParseStatus Status);

public static class NpcNameListCodec
{
    public const int CurrentVersion = 1;
    private const int MaxNameLength = 128;

    private static readonly JsonSerializerOptions SerializeOptions = new() { WriteIndented = true };

    // Never throws. Data is non-null exactly when Status == Ok. MalformedJson and
    // UnsupportedVersion stay distinct so scan-time and refresh-time callers can report them
    // differently if they choose to (mirrors OrganizationJsonCodec's own convention).
    public static NpcNameListParseResult Parse(string json)
    {
        if (json is null)
            return new NpcNameListParseResult(null, NpcNameListParseStatus.MalformedJson);

        NpcNameListDocument? data;
        try
        {
            data = JsonSerializer.Deserialize<NpcNameListDocument>(json);
        }
        catch (JsonException)
        {
            return new NpcNameListParseResult(null, NpcNameListParseStatus.MalformedJson);
        }

        if (data is null)
            return new NpcNameListParseResult(null, NpcNameListParseStatus.MalformedJson);
        if (data.Version != CurrentVersion)
            return new NpcNameListParseResult(null, NpcNameListParseStatus.UnsupportedVersion);

        return new NpcNameListParseResult(Sanitize(data), NpcNameListParseStatus.Ok);
    }

    public static string Serialize(NpcNameListDocument data) =>
        JsonSerializer.Serialize(Sanitize(data), SerializeOptions);

    // Additive only: everything already in `existing` is kept verbatim; only genuinely new names
    // are unioned in. Excluded is carried through unchanged — refresh never modifies it.
    public static NpcNameListDocument MergeAdditive(
        NpcNameListDocument existing,
        IReadOnlyList<string> newNpcs,
        IReadOnlyList<string> newEnemies,
        IReadOnlyList<string> newBosses)
    {
        var excluded = new HashSet<string>(existing.Excluded, StringComparer.OrdinalIgnoreCase);

        return Sanitize(new NpcNameListDocument
        {
            Version = existing.Version,
            NPCs = [.. existing.NPCs, .. newNpcs.Where(n => !excluded.Contains(n))],
            Enemies = [.. existing.Enemies, .. newEnemies.Where(n => !excluded.Contains(n))],
            Bosses = [.. existing.Bosses, .. newBosses.Where(n => !excluded.Contains(n))],
            Excluded = existing.Excluded,
        });
    }

    // Applied on every parse, serialize, and merge so the document is always normalized before
    // use: trimmed, blank/over-length entries dropped, de-duplicated case-insensitively within
    // (not across) each array, sorted deterministically so repeated writes with no real change
    // produce byte-identical output.
    private static NpcNameListDocument Sanitize(NpcNameListDocument data) => new()
    {
        Version = CurrentVersion,
        NPCs = SanitizeList(data.NPCs),
        Enemies = SanitizeList(data.Enemies),
        Bosses = SanitizeList(data.Bosses),
        Excluded = SanitizeList(data.Excluded),
    };

    private static List<string> SanitizeList(List<string>? names) =>
        (names ?? [])
            .Select(n => n?.Trim() ?? string.Empty)
            .Where(n => n.Length > 0 && n.Length <= MaxNameLength)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
