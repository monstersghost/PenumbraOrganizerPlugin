namespace PenumbraOrganizer.Plugin.Organizer;

/// <summary>
/// What a folder rule says, as resolved for a given path: the deepest rule on the parent chain
/// wins, so a folder can carve an exception out of a protected ancestor.
/// </summary>
public enum FolderProtectionRule
{
    /// <summary>No rule anywhere on the chain. The folder layer does not protect this path.</summary>
    None,

    /// <summary>The nearest rule protects this path.</summary>
    Protected,

    /// <summary>
    /// The nearest rule is an exception: the folder layer does not protect this path, even though
    /// an ancestor is protected. Per-mod ticks and Heliosphere management still do.
    /// </summary>
    Excluded,
}
