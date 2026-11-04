namespace PenumbraOrganizer.Plugin.Organizer.BodyTypes;

/// <summary>One body replacer: the folder name it earns, and the words a mod for it is named with.</summary>
/// <remarks>
/// <see cref="Sex"/> is metadata only. The Sort tab files a body as one segment
/// (<c>Gear/Bibo+/Top</c>), never <c>Gear/Female/Bibo+/Top</c>: a gear library is browsed by the
/// body it was cut for, and the body's sex is already implied by the body. It is kept because it is
/// real domain information the list would otherwise lose, and because a future grouping would need
/// it back.
/// </remarks>
public sealed class BodyTypeEntry
{
    /// <summary>The subfolder under <c>Body</c>, spelled as the community spells it.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>"Female", "Male", or anything else; never consulted when building a folder.</summary>
    public string Sex { get; set; } = string.Empty;

    /// <summary>
    /// "verified" for a body cross-checked against Proteus's own source, "reported" for one carried
    /// on the plugin author's word alone. Provenance for whoever maintains this next; no behaviour
    /// depends on it.
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// The words to look for in a mod's name, lowercase. Order does not matter: the matcher sorts
    /// every alias globally by length so the longest wins regardless of where it was listed.
    /// </summary>
    public List<string> Aliases { get; set; } = [];
}

public sealed class BodyTypeListDocument
{
    public int Version { get; set; } = BodyTypeListCodec.CurrentVersion;
    public List<BodyTypeEntry> Bodies { get; set; } = [];
}
