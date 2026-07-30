namespace PenumbraOrganizer.Plugin.LibraryWork.Pure;

/// <summary>
/// Everything the background phase needs about one mod, as plain strings copied off the Penumbra
/// adapter on the framework thread. The mod directory is a string rather than the DirectoryInfo the
/// adapter hands out: that severs object identity with adapter-owned state, so a stale adapter can
/// never be reached through a seed even by accident.
///
/// ChangedItemKeys holds references to strings Penumbra already allocated, so materializing them
/// copies 8 bytes each, not character data.
/// </summary>
public sealed record ScanSeed(
    string Identifier,
    string Name,
    string Author,
    string CurrentPath,
    string ModDirectoryPath,
    IReadOnlyList<string> ChangedItemKeys);
