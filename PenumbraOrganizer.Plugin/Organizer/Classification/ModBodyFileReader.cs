using System.Text.RegularExpressions;

namespace PenumbraOrganizer.Plugin.Organizer.Classification;

/// <summary>
/// Reads whether a mod actually replaces a body, for the Sort tab's "Split body by body type".
/// </summary>
/// <remarks>
/// The third consumer of <see cref="ModConfigFileWalker"/>, after
/// <see cref="ModEquipmentFileReader"/> and <see cref="ModHairFileReader"/>, and it shares their
/// return contract: null means the walk could not be trusted.
/// <para>
/// This reports evidence only. WHICH body a mod is comes from its name
/// (<see cref="BodyTypeMatcher"/>); this says whether the name is worth consulting at all. The
/// division is the same one <see cref="ModHairFileReader"/> draws against
/// <c>EnrichHairSubCategory</c>: the reader does not make the filing decision.
/// </para>
/// </remarks>
public static class ModBodyFileReader
{
    // A body replacer replaces the SMALLCLOTHES equipment models - e0000, the bare-body item - in
    // one of the four halves a body is authored in. This is the same signature Proteus's own
    // BodySizeCatalog.IsBodyModel uses to decide whether an installed mod is a body at all, which
    // is the one part of that plugin's detection that needs no curated list. Confirmed against its
    // source rather than guessed.
    //
    // Anchored at BOTH ends: a path that merely contains this shape is not a body model, and the
    // unanchored form would accept, say, a texture path that embeds a model path.
    //
    // [0-9], NOT \d: .NET's \d matches every Unicode decimal digit, so a path written with
    // fullwidth digits would match here while naming no real game path. ModHairFileReader carries
    // the same note, for the same reason - this repo has been bitten by a non-ASCII codepoint
    // reaching code as something other than what it looked like.
    private static readonly Regex BodyModelPath = new(
        @"^chara/equipment/e0000/model/c[0-9]{4}e0000_(top|dwn|glv|sho)\.mdl$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// True when the mod replaces at least one body model, false when it replaces none, and null
    /// when the walk failed - the same three-way answer the gear and hair readers give, so a caller
    /// treats "could not read" as no evidence rather than as a confident "not a body".
    /// </summary>
    public static bool? ReadIsBodyReplacer(DirectoryInfo modDirectory)
    {
        var found = false;

        var ok = ModConfigFileWalker.TryWalk(
            modDirectory,
            path =>
            {
                if (BodyModelPath.IsMatch(path))
                    found = true;
            },
            // Manipulations are ignored on purpose. A body replacer is identified by the MODELS it
            // redirects; its EQDP/IMC entries, where it has any, say which races and variants the
            // models apply to and are carried by plenty of mods that are not bodies. Reading them
            // would widen this from "replaces a body" to "touches smallclothes metadata", which is
            // true of a great many gear mods.
            _ => { });

        return ok ? found : null;
    }
}
