using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PenumbraOrganizer.Plugin.Organizer.Templates;

/// <summary>
/// The single normalization used on both sides of a template: the author's mod names at export
/// and the importer's mod names at import. This is the feature's compatibility surface â€” changing
/// it changes which entries match in templates that are already published, so any change to it is
/// a formatVersion question, not a free bug fix. See the spec's "Name normalization" section.
/// </summary>
public static class ModNameNormalizer
{
    // Bracketed tag groups: "[WIP] Foo" -> " Foo".
    private static readonly Regex BracketGroups = new(@"\[[^\]]*\]|\{[^}]*\}", RegexOptions.Compiled);

    // Only the two suffix forms that actually occur: Penumbra's own dealt "_1_1_0", and an
    // author's "v2.1". Deliberately NOT a general trailing-digit rule, which would destroy
    // legitimate names like "Gear 2000".
    private static readonly Regex PenumbraVersionSuffix = new(@"(?:_\d+)+$", RegexOptions.Compiled);
    private static readonly Regex AuthorVersionSuffix = new(@"[ _\-.]v\d+(?:[._]\d+)*$", RegexOptions.Compiled);

    public static string Normalize(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var text = name.Trim().ToLowerInvariant();

        // Apostrophes are deleted rather than spaced, so "Emperor's" -> "emperors".
        text = text.Replace("'", string.Empty).Replace("\u2019", string.Empty);

        text = BracketGroups.Replace(text, " ");
        text = text.Replace("(penumbra)", " ");

        text = AuthorVersionSuffix.Replace(text, string.Empty);
        text = PenumbraVersionSuffix.Replace(text, string.Empty);

        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            // '+' is preserved explicitly: it is load-bearing in this ecosystem ("Bibo+", "YAB+").
            // Unicode letters and digits are preserved, so accented and non-Latin names survive.
            if (character == '+' || char.IsLetter(character) || char.IsDigit(character))
                builder.Append(character);
            else
                builder.Append(' ');
        }

        return CollapseWhitespace(builder.ToString());
    }

    private static string CollapseWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var character in text)
        {
            if (character == ' ')
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }
}
