using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PenumbraOrganizer.Plugin.Organizer.Templates;

/// <summary>
/// The one set of serializer options for template documents, shared by the models' tests and by
/// TemplateCodec, so a document written by one path is byte-identical to one written by the other.
///
/// The relaxed encoder matters for size, not looks: the default encoder escapes '+' as + and
/// every non-ASCII character as \uXXXX -- six bytes where one belongs. Mod names are full of both
/// ("Bibo+", "Café"), and payload size is what decides whether a share code fits in a chat
/// message. "Unsafe" here refers to embedding output directly in HTML, which templates never do:
/// they go to a .json file and to the clipboard.
/// </summary>
public static class TemplateJson
{
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };
}

/// <summary>
/// Names one of OrganizerState's seven existing sort strategies, used for mods a template has no
/// entry for. These names are a wire contract — renaming a member breaks published templates.
/// </summary>
public enum TemplateFallbackStrategy
{
    Creator,
    ModType,
    ModTypeDetailed,
    TypeThenCreator,
    TypeThenCreatorFlat,
    CreatorThenType,
    CreatorThenTypeFlat,
}

public enum TemplateWarningCode
{
    UnknownFolderLabelKey,
    InvalidEntryPath,
    DuplicateEntry,
    ConflictingDuplicateEntry,
    ExportNameCollision,
    UnmatchedTemplateEntry,
    AmbiguousLocalMatch,
}

/// <summary>
/// Structured rather than pre-formatted prose so the UI formats consistently and tests assert on
/// codes instead of comparing strings.
/// </summary>
public sealed record TemplateWarning(TemplateWarningCode Code, string Subject);

/// <summary>
/// One template entry. Short JSON field names because entries dominate the payload size, which
/// decides whether a share code fits in a chat message.
/// </summary>
public sealed record TemplateEntry(
    [property: JsonPropertyName("n")] string N,
    [property: JsonPropertyName("f")] string F);

/// <summary>
/// The raw document as deserialized. Untrusted: FallbackStrategy is a string here because an
/// unknown value must produce a stated error rather than a deserialization exception, and entry
/// keys are re-normalized rather than believed. Use TemplateCodec to obtain a validated template.
/// </summary>
public sealed class OrganizationTemplate
{
    [JsonPropertyName("formatVersion")] public int FormatVersion { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("author")] public string? Author { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }

    // Informational provenance only: never validated beyond being a string, never blocks import.
    [JsonPropertyName("createdWithVersion")] public string? CreatedWithVersion { get; set; }
    [JsonPropertyName("createdAtUtc")] public string? CreatedAtUtc { get; set; }

    [JsonPropertyName("fallbackStrategy")] public string FallbackStrategy { get; set; } = string.Empty;
    [JsonPropertyName("folderLabels")] public Dictionary<string, string> FolderLabels { get; set; } = new();
    [JsonPropertyName("folders")] public List<string> Folders { get; set; } = [];
    [JsonPropertyName("entries")] public List<TemplateEntry> Entries { get; set; } = [];
}

/// <summary>
/// A template that has passed every stage of TemplateCodec's validation. This is the only shape
/// the planner accepts, so unvalidated external input cannot reach OrganizerState.
/// </summary>
public sealed record ValidatedOrganizationTemplate(
    string Name,
    string? Author,
    string? Description,
    TemplateFallbackStrategy FallbackStrategy,
    IReadOnlyDictionary<string, string> FolderLabels,
    IReadOnlyList<string> Folders,
    IReadOnlyDictionary<string, string> EntriesByNormalizedName);
