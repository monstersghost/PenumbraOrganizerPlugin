using System.Text.Json;

namespace PenumbraOrganizer.Plugin.Organizer.Classification;

/// <summary>
/// Walks a mod's Penumbra config JSON, handing each declared game path and each meta manipulation
/// to a callback.
/// </summary>
/// <remarks>
/// Extracted from <see cref="ModEquipmentFileReader"/> once <see cref="ModHairFileReader"/> became
/// a second consumer. Everything here is about Penumbra's on-disk LAYOUT; what a given path or
/// manipulation MEANS belongs to the readers.
/// <para>
/// Callbacks must consume their <see cref="JsonElement"/> synchronously: it is a view into the
/// <see cref="JsonDocument"/> that owns it, and that document is disposed when its file's scope
/// ends here. Reading values inside the callback is fine; storing the element for later is a
/// use-after-dispose.
/// </para>
/// </remarks>
public static class ModConfigFileWalker
{
    /// <summary>
    /// Returns false if any config file could not be enumerated, read, or parsed - an
    /// untrustworthy partial walk, which every caller must treat as "no evidence" rather than as a
    /// confident empty answer. A non-existent directory returns true with no callbacks: that is
    /// not a read failure, just nothing to find.
    /// </summary>
    /// <remarks>
    /// On false, the caller must DISCARD everything collected so far. The callbacks write into the
    /// caller's own collection as the walk proceeds, so a failure part-way through leaves a
    /// partial set that must never be returned as an answer.
    /// </remarks>
    public static bool TryWalk(
        DirectoryInfo modDirectory,
        Action<string> onFileKey,
        Action<JsonElement> onManipulation)
    {
        if (!modDirectory.Exists)
            return true;

        IReadOnlyList<FileInfo> configFiles;
        try
        {
            configFiles = DiscoverConfigFiles(modDirectory);
        }
        catch (Exception ex) when (IsExpectedFileSystemException(ex))
        {
            return false;
        }

        foreach (var file in configFiles)
        {
            try
            {
                using var stream = File.OpenRead(file.FullName);
                using var document = JsonDocument.Parse(stream);
                WalkElement(document.RootElement, onFileKey, onManipulation);
            }
            catch (Exception ex) when (IsExpectedFileSystemException(ex) || ex is JsonException)
            {
                return false;
            }
        }

        return true;
    }

    // Counts the config file(s) TryWalk would read, without reading or parsing them. A caller that
    // gets an empty walk cannot otherwise tell "no config files were found here at all" from
    // "config files were read and had nothing recognized" - very different root causes.
    public static int CountConfigFiles(DirectoryInfo modDirectory)
    {
        if (!modDirectory.Exists)
            return 0;

        try
        {
            return DiscoverConfigFiles(modDirectory).Count;
        }
        catch (Exception ex) when (IsExpectedFileSystemException(ex))
        {
            return 0;
        }
    }

    // Penumbra 1.7.0 (2026-07-20) replaced the older default_mod.json + group_*.json layout with
    // a single meta.json per mod ("This version gets rid of the group- and default option JSON
    // files and instead stores all this information in the meta.json again" - Penumbra's own
    // changelog). The two layouts are mutually exclusive after Penumbra's own migration, but both
    // are checked here so a plugin build spanning the update doesn't regress for a tester still on
    // the older Penumbra version.
    private static IReadOnlyList<FileInfo> DiscoverConfigFiles(DirectoryInfo modDirectory)
    {
        // Materialized eagerly: DirectoryInfo.EnumerateFiles is lazy, so a permission error during
        // enumeration would otherwise surface outside the caller's catch.
        var metaJson = new FileInfo(Path.Combine(modDirectory.FullName, "meta.json"));
        if (metaJson.Exists)
            return [metaJson];

        var files = new List<FileInfo>();
        var defaultMod = new FileInfo(Path.Combine(modDirectory.FullName, "default_mod.json"));
        if (defaultMod.Exists)
            files.Add(defaultMod);
        files.AddRange(modDirectory.EnumerateFiles("group_*.json"));
        return files;
    }

    private static bool IsExpectedFileSystemException(Exception ex) =>
        ex is IOException or UnauthorizedAccessException; // UnauthorizedAccessException does NOT
                                                          // derive from IOException in .NET

    private static void WalkElement(
        JsonElement element,
        Action<string> onFileKey,
        Action<JsonElement> onManipulation)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return; // guards every TryGetProperty call below against a non-object element

        if (element.TryGetProperty("Files", out var files) && files.ValueKind == JsonValueKind.Object)
        {
            // Normalised here rather than in each reader: every consumer wants '/' separators, and
            // Penumbra writes some keys with '\'.
            foreach (var entry in files.EnumerateObject())
                onFileKey(entry.Name.Replace('\\', '/'));
        }

        if (element.TryGetProperty("Manipulations", out var manipulations)
            && manipulations.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in manipulations.EnumerateArray())
                onManipulation(item);
        }

        WalkChildArray(element, "Options", onFileKey, onManipulation);
        WalkChildArray(element, "Containers", onFileKey, onManipulation);

        // Penumbra 1.7.0's meta.json shape: the mod's default option's Files/Manipulations live
        // nested one level under "DefaultData" (an object, not an array like Options/Containers),
        // and every group that used to be its own group_*.json file is now one entry in a
        // top-level "Groups" array - each group entry has the same Options/Containers shape
        // already handled above, so recursing into it needs no further special-casing.
        if (element.TryGetProperty("DefaultData", out var defaultData)
            && defaultData.ValueKind == JsonValueKind.Object)
            WalkElement(defaultData, onFileKey, onManipulation);
        WalkChildArray(element, "Groups", onFileKey, onManipulation);
    }

    private static void WalkChildArray(
        JsonElement element,
        string propertyName,
        Action<string> onFileKey,
        Action<JsonElement> onManipulation)
    {
        if (!element.TryGetProperty(propertyName, out var children) || children.ValueKind != JsonValueKind.Array)
            return;

        // Genuinely recursive (calls WalkElement, not just the Files/Manipulations step) - real
        // mods checked across ~2,280 combined mods never nest beyond one level, but this doesn't
        // silently stop early if a future/unusual mod does.
        foreach (var child in children.EnumerateArray())
            WalkElement(child, onFileKey, onManipulation);
    }
}
