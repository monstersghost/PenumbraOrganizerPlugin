namespace PenumbraOrganizer.Plugin.LibraryWork.Pure;

/// <summary>
/// One mod's worth of index input, as plain strings copied off the Penumbra adapter on the framework
/// thread. Same rationale as ScanSeed: the mod directory is a string, not the adapter's DirectoryInfo.
/// </summary>
public sealed record IndexSeed(
    string Identifier,
    string Name,
    string Author,
    string ModDirectoryPath,
    IReadOnlyList<string> ChangedItemKeys);
