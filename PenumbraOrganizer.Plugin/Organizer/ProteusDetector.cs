namespace PenumbraOrganizer.Plugin.Organizer;

/// <summary>
/// Recognises mods Proteus made, from facts the scan already has (no disk read).
/// </summary>
/// <remarks>
/// Proteus's own <c>IsMadeByProteus</c> checks that the manifest's Author is exactly "Proteus", so
/// this matches ordinally: "proteus" is somebody else's mod. The managed output mod is the one in a
/// directory named "Proteus", and Proteus writes the same author into it, so BOTH must hold; a
/// mod that merely lives in a directory called "Proteus" is not flagged. Mods Proteus imports
/// (Onion, Eye, Luminis, emissive) keep the imported author and are deliberately not detected.
/// </remarks>
public static class ProteusDetector
{
    public const string AuthorName = "Proteus";
    public const string ManagedDirectoryName = "Proteus";

    public static ProteusRole Classify(string directoryName, string? author)
    {
        if (!string.Equals(author, AuthorName, StringComparison.Ordinal))
            return ProteusRole.None;

        return string.Equals(directoryName, ManagedDirectoryName, StringComparison.OrdinalIgnoreCase)
            ? ProteusRole.Managed
            : ProteusRole.Made;
    }
}
