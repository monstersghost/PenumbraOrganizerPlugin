using System.Text.Json;
using System.Text.Json.Serialization;

namespace PenumbraOrganizer.Plugin.Organizer;

// Mirrors Penumbra's organization.json schema (Luna FileSystemSaver.Organization, Version 1).
// ExtensionData on every type: this plugin rewrites a config file it doesn't own, and a future
// Penumbra field added without a Version bump must survive the prune round-trip.
public sealed class FolderData
{
    public uint? ExpandedColor { get; set; }
    public uint? CollapsedColor { get; set; }
    public string? SortMode { get; set; }
    public bool? IsSeparator { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class SeparatorData
{
    public uint? Color { get; set; }
    public bool Folder { get; set; }
    public long CreationDate { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class OrganizationJson
{
    public int Version { get; set; }
    public Dictionary<string, FolderData> Folders { get; set; } = new();
    public Dictionary<string, SeparatorData> Separators { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
