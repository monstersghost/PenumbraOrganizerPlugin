namespace PenumbraOrganizer.Plugin.Organizer;

/// <summary>
/// Decides whether a mod is managed by Heliosphere, which is what drives its automatic protection.
/// </summary>
/// <remarks>
/// Three independent signals, ORed, because each one alone has a hole and a false negative here is
/// the expensive direction: it silently unprotects a mod Heliosphere owns, and the next Apply moves
/// it out from under Heliosphere.
/// <list type="number">
/// <item>The <c>hs-</c> directory prefix. Survives an update, because Heliosphere's update writes a
/// new <c>hs-...</c> directory and deletes the old one.</item>
/// <item>A <c>heliosphere.json</c> in the mod directory. The only signal for mods whose directory was
/// renamed or that were installed from an exported pack - but it is a disk read, so it reports false
/// while the directory is mid-rewrite during exactly that update.</item>
/// <item>Heliosphere's <c>[HS] </c> display-name prefix, and a remembered set of identifiers seen as
/// managed by any earlier scan. These cover the window where signal 2 blinks out.</item>
/// </list>
/// Measured against a real 9777-mod library: 812 mods were managed, 68 of them lacked the <c>hs-</c>
/// prefix, 60 of those carried the <c>[HS] </c> name, and <b>8 had neither</b> and depended entirely
/// on the disk read. Those 8 are why the remembered set exists rather than just a name check.
/// <para>
/// Both added signals fail conservatively: the worst case is a mod protected that need not be, which
/// the user can untick. The previous behaviour's worst case was a mod moved that must not move.
/// </para>
/// </remarks>
public static class HeliosphereDetector
{
    private const string DirectoryPrefix = "hs-";
    private const string MetaFileName = "heliosphere.json";

    /// <summary>Heliosphere's own display-name prefix, e.g. "[HS] Band Tee - by Solona".</summary>
    private const string DisplayNamePrefix = "[HS] ";

    /// <param name="displayName">
    /// The mod's Penumbra display name. Pass null where it is genuinely unavailable; the other
    /// signals still apply.
    /// </param>
    /// <param name="previouslyKnownIdentifiers">
    /// Identifiers any earlier scan already resolved as managed. Null is treated as empty, so
    /// existing callers and tests keep the pre-remembering behaviour.
    /// </param>
    public static bool IsHeliosphereManaged(
        string directoryName,
        DirectoryInfo modPath,
        string? displayName = null,
        IReadOnlySet<string>? previouslyKnownIdentifiers = null)
    {
        if (!string.IsNullOrWhiteSpace(directoryName)
            && directoryName.StartsWith(DirectoryPrefix, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrWhiteSpace(displayName)
            && displayName.StartsWith(DisplayNamePrefix, StringComparison.Ordinal))
            return true;

        if (previouslyKnownIdentifiers is not null
            && !string.IsNullOrWhiteSpace(directoryName)
            && previouslyKnownIdentifiers.Contains(directoryName))
            return true;

        return HasMetaFile(modPath);
    }

    // File.Exists already returns false rather than throwing for an unreadable path, but the
    // DirectoryInfo/Path.Combine pair can still throw on a malformed path, and a scan must not fail
    // over one bad mod directory.
    private static bool HasMetaFile(DirectoryInfo modPath)
    {
        try
        {
            return modPath.Exists && File.Exists(Path.Combine(modPath.FullName, MetaFileName));
        }
        catch (Exception)
        {
            return false;
        }
    }
}
