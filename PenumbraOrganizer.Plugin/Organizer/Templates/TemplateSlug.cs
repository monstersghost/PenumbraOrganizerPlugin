using System.Text;

namespace PenumbraOrganizer.Plugin.Organizer.Templates;

/// <summary>
/// Turns a template's display name into a filename. This is the ONLY sanitizer that may be used
/// for a path on disk: TemplatePathValidator.IsValidFolder deliberately accepts "..", drive
/// letters, backslashes and Windows device names, because those are harmless as Penumbra virtual
/// folders -- and every one of them is dangerous here. The document's own `name` stays
/// authoritative for display; this value is storage only.
/// </summary>
public static class TemplateSlug
{
    public const string Fallback = "template";
    private const int MaxLength = 64;

    // Dangerous only when they are the entire stem, so they are suffixed rather than stripped --
    // otherwise a legitimate "Console Tweaks" would be mangled.
    private static readonly HashSet<string> ReservedStems = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string From(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var character in name.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character) || character == '_')
                builder.Append(character);
            else if (character == ' ')
                builder.Append('-');
            // else: skip the character (punctuation, slashes, etc.)
        }

        var slug = CollapseDashes(builder.ToString());

        if (slug.Length > MaxLength)
            slug = CollapseDashes(slug[..MaxLength]);

        // Nothing usable survived (the name was punctuation, separators, or empty).
        if (slug.Length == 0)
            return Fallback;

        if (ReservedStems.Contains(slug))
            slug += "-" + Fallback;

        return slug;
    }

    public static string MakeUnique(string slug, IReadOnlySet<string> taken)
    {
        if (!taken.Contains(slug))
            return slug;

        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{slug}-{suffix}";
            if (!taken.Contains(candidate))
                return candidate;
        }
    }

    // Also removes leading/trailing dashes, which is what makes a trailing dot or space
    // impossible -- Windows silently strips those from a filename, so a slug must never end in
    // one.
    private static string CollapseDashes(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (character == '-' && (builder.Length == 0 || builder[^1] == '-'))
                continue;

            builder.Append(character);
        }

        while (builder.Length > 0 && builder[^1] == '-')
            builder.Length--;

        return builder.ToString();
    }
}
