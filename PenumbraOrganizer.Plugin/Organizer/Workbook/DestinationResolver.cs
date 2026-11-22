using System.Globalization;
using System.Text.RegularExpressions;

namespace PenumbraOrganizer.Plugin.Organizer.Workbook;

/// <summary>Token values for one mod. An empty string means "no value", so its folder level collapses.</summary>
public sealed record DestinationTokens(string Creator, string Type, string Race)
{
    public static readonly DestinationTokens None = new(string.Empty, string.Empty, string.Empty);
}

/// <summary>
/// How a destination cell is read. <paramref name="CodeFolder"/> maps a positive code to its folder path
/// (null when no usable row defines it); <paramref name="NameFolder"/> maps a built-in category name, any
/// case, to its folder path (null when the name is not a built-in). <paramref name="TokensEnabled"/> is
/// true for version 3 files only.
/// </summary>
public sealed record DestinationRules(
    bool TokensEnabled,
    Func<int, string?> CodeFolder,
    Func<string, string?> NameFolder);

/// <summary><see cref="CodeUsed"/> is the code actually looked up, or null when none was.</summary>
public sealed record DestinationResult(string? Destination, string? Error, int? CodeUsed = null)
{
    /// <summary>A blank destination: leave the mod where it is.</summary>
    public static readonly DestinationResult Leave = new(null, null);

    public static DestinationResult Ok(string destination, int? codeUsed = null) => new(destination, null, codeUsed);

    public static DestinationResult Fail(string error) => new(null, error);
}

/// <summary>
/// The one place a destination cell becomes a folder path (workbook spec, section 4): code or built-in
/// name or literal first segment, then tokens expanded one segment at a time, then collapse, then
/// validation. Pure: no file access.
/// </summary>
public static class DestinationResolver
{
    public const int MaxPathLength = 260;

    // Any {name}: unknown names are errors, so there is no length cutoff.
    private static readonly Regex TokenPattern = new(
        @"\{([A-Za-z]+)\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string NormalizePath(string path) => path.Trim().Replace('\\', '/').Trim('/');

    /// <summary>The names of the tokens in <paramref name="text"/> that are not one of the three.</summary>
    public static IReadOnlyList<string> UnknownTokens(string text) =>
        TokenPattern.Matches(text)
            .Select(m => m.Groups[1].Value)
            .Where(name => ValueOf(name, DestinationTokens.None) is null)
            .ToList();

    /// <summary>The rule every folder path obeys, whether it came from a cell or from the mapping sheet.</summary>
    public static string? StructureError(string path)
    {
        if (Path.IsPathRooted(path) || path.Contains(':') || path.Any(char.IsControl))
            return "Use a relative Penumbra folder path.";

        var parts = path.Split('/');
        if (parts.Any(part => string.IsNullOrWhiteSpace(part) || part is "." or ".."))
            return "Destination cannot contain empty segments, . or ..";

        if (path.Length > MaxPathLength)
            return $"Destination is longer than {MaxPathLength} characters.";

        return null;
    }

    /// <summary>The code a destination's first segment names, or null. Only a positive whole number is a code.</summary>
    public static int? FirstSegmentCode(string raw)
    {
        var first = NormalizePath(raw ?? string.Empty).Split('/')[0];
        return TryParseCode(first, out var code) ? code : null;
    }

    private static bool TryParseCode(string text, out int code) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out code) && code > 0;

    /// <summary>
    /// A destination taken exactly as written, with no code, name or token reading and no separator
    /// normalisation (a creator "A/B" is stored as the single segment "A\B"). Used when the cell is
    /// unchanged from what the export wrote.
    /// </summary>
    public static DestinationResult Literal(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return DestinationResult.Leave;

        return StructureError(raw) is { } error ? DestinationResult.Fail(error) : DestinationResult.Ok(raw);
    }

    public static DestinationResult Resolve(string raw, DestinationRules rules, DestinationTokens tokens)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return DestinationResult.Leave;

        var normalized = NormalizePath(raw);
        if (normalized.Length == 0)
            return DestinationResult.Fail("Destination is empty.");
        if (StructureError(normalized) is { } structure)
            return DestinationResult.Fail(structure);

        var parts = normalized.Split('/');
        string[] head;
        int? codeUsed = null;
        if (TryParseCode(parts[0], out var code))
        {
            var folder = rules.CodeFolder(code);
            if (folder is null)
                return DestinationResult.Fail($"code {code} is not in the Category Mapping sheet");
            head = folder.Split('/');
            codeUsed = code;
        }
        else if (rules.NameFolder(parts[0]) is { } named)
        {
            head = named.Split('/');
        }
        else
        {
            head = [parts[0]];
        }

        var segments = head.Concat(parts.Skip(1)).ToList();

        if (rules.TokensEnabled)
        {
            var expanded = new List<string>();
            foreach (var segment in segments)
            {
                string? unknown = null;
                var text = TokenPattern.Replace(segment, match =>
                {
                    var value = ValueOf(match.Groups[1].Value, tokens);
                    if (value is null)
                    {
                        unknown ??= match.Groups[1].Value;
                        return match.Value;
                    }

                    return value;
                });

                if (unknown is not null)
                    return DestinationResult.Fail($"unknown token {{{unknown}}}");

                // The whole segment expanded to nothing: its level collapses. A partly empty one keeps
                // its literal text.
                if (text.Trim().Length == 0)
                    continue;

                expanded.Add(text);
            }

            segments = expanded;
        }

        if (segments.Count == 0)
            return DestinationResult.Leave;

        var final = string.Join('/', segments);
        return StructureError(final) is { } finalError
            ? DestinationResult.Fail(finalError)
            : DestinationResult.Ok(final, codeUsed);
    }

    /// <summary>
    /// A sort rule's folder: tokens always on, never read as a category code or built-in name (a rule's
    /// "17/x" is the literal folder 17), each segment trimmed, a whole-token segment that expands empty
    /// dropped, and a stray brace an error. Pure.
    /// </summary>
    public static DestinationResult ResolveRuleFolder(string raw, DestinationTokens tokens)
    {
        var normalized = NormalizePath(raw);
        if (normalized.Length == 0)
            return DestinationResult.Fail("Destination is empty.");

        var expanded = new List<string>();
        foreach (var part in normalized.Split('/'))
        {
            var segment = part.Trim();

            // Checked before expansion so a token value that itself contains a brace is not an error.
            if (TokenPattern.Replace(segment, string.Empty).Any(c => c is '{' or '}'))
                return DestinationResult.Fail("Folder has an unmatched { or }.");

            string? unknown = null;
            var text = TokenPattern.Replace(segment, match =>
            {
                var value = ValueOf(match.Groups[1].Value, tokens);
                if (value is null)
                {
                    unknown ??= match.Groups[1].Value;
                    return match.Value;
                }

                return value;
            });

            if (unknown is not null)
                return DestinationResult.Fail($"unknown token {{{unknown}}}");

            text = text.Trim();
            if (text.Length == 0)
                continue;
            expanded.Add(text);
        }

        if (expanded.Count == 0)
            return DestinationResult.Fail("Folder expands to nothing for this mod.");

        var final = string.Join('/', expanded);
        return StructureError(final) is { } error ? DestinationResult.Fail(error) : DestinationResult.Ok(final);
    }

    private static string? ValueOf(string name, DestinationTokens tokens) => name.ToLowerInvariant() switch
    {
        "creator" => tokens.Creator,
        "type" => tokens.Type,
        "race" => tokens.Race,
        _ => null,
    };
}
