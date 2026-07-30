using System.Text.Json;

namespace PenumbraOrganizer.Plugin.Organizer.Templates;

public enum TemplateDecodeError
{
    MissingPrefix,
    InvalidBase64,
    InvalidDeflate,
    PayloadTooLarge,
    MalformedJson,
    UnsupportedFormatVersion,
    MissingName,
    UnknownFallbackStrategy,
    LimitExceeded,
    InvalidFolderLabelValue,
}

public sealed record TemplateDecodeResult(
    ValidatedOrganizationTemplate? Template,
    TemplateDecodeError? Error,
    string? ErrorDetail,
    IReadOnlyList<TemplateWarning> Warnings)
{
    public bool Succeeded => Template is not null;

    public static TemplateDecodeResult Fail(TemplateDecodeError error, string detail) =>
        new(null, error, detail, []);
}

/// <summary>
/// Decodes in distinct stages — transport, JSON, schema, semantic — so that nothing unvalidated
/// can reach OrganizerState: only a ValidatedOrganizationTemplate leaves this class.
///
/// Fatal errors refuse the whole document rather than applying part of it. Non-fatal problems
/// (a bad folder in the folders list, an unknown label key, a duplicate entry) warn and continue.
/// </summary>
public static class TemplateCodec
{
    public const int SupportedFormatVersion = 1;

    // Task 2's shared options, not a second private copy: a document written here must be
    // byte-identical to one written anywhere else, and the relaxed encoder keeps '+' and
    // non-ASCII mod names from inflating to six bytes per character.
    private static JsonSerializerOptions SerializerOptions => TemplateJson.SerializerOptions;

    // Error details reach UI and logs, so every echoed fragment of an untrusted document is
    // bounded. Without this a single hostile field can inflate whatever surface displays it.
    private static string Preview(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "(empty)";

        return value.Length <= PreviewLength ? value : value[..PreviewLength] + "...";
    }

    private const int PreviewLength = 64;
    private const string NullSubject = "(null)";

    public static string EncodeJson(OrganizationTemplate template) =>
        JsonSerializer.Serialize(template, SerializerOptions);

    public static TemplateDecodeResult DecodeJson(string json)
    {
        // Stage 2: well-formedness only.
        OrganizationTemplate? document;
        try
        {
            document = JsonSerializer.Deserialize<OrganizationTemplate>(json, SerializerOptions);
        }
        catch (JsonException exception)
        {
            return TemplateDecodeResult.Fail(TemplateDecodeError.MalformedJson, exception.Message);
        }

        if (document is null)
            return TemplateDecodeResult.Fail(TemplateDecodeError.MalformedJson, "Document was null.");

        return Validate(document);
    }

    // Stages 3-5: schema validation, semantic normalization, validated construction.
    private static TemplateDecodeResult Validate(OrganizationTemplate document)
    {
        // System.Text.Json overwrites a property's initializer default whenever the JSON supplies
        // an explicit null, so every collection here can arrive null no matter what the model
        // declares. This is the untrusted-input boundary: a hostile document must produce a value
        // describing what is wrong, never an exception.
        var rawFolders = document.Folders ?? [];
        var rawLabels = document.FolderLabels ?? new Dictionary<string, string>();
        var rawEntries = document.Entries ?? [];

        if (document.FormatVersion != SupportedFormatVersion)
        {
            return TemplateDecodeResult.Fail(
                TemplateDecodeError.UnsupportedFormatVersion,
                $"Template format version {document.FormatVersion}; this plugin supports {SupportedFormatVersion}.");
        }

        if (string.IsNullOrWhiteSpace(document.Name) || document.Name.Length > TemplateLimits.MaxStringLength)
            return TemplateDecodeResult.Fail(TemplateDecodeError.MissingName, "Template name is missing or too long.");

        if (!Enum.TryParse<TemplateFallbackStrategy>(document.FallbackStrategy, ignoreCase: false, out var strategy))
        {
            return TemplateDecodeResult.Fail(
                TemplateDecodeError.UnknownFallbackStrategy,
                $"Unknown fallback strategy '{Preview(document.FallbackStrategy)}'.");
        }

        if (rawEntries.Count > TemplateLimits.MaxEntries)
            return TemplateDecodeResult.Fail(TemplateDecodeError.LimitExceeded, $"Entries: {rawEntries.Count}.");
        if (rawFolders.Count > TemplateLimits.MaxFolders)
            return TemplateDecodeResult.Fail(TemplateDecodeError.LimitExceeded, $"Folders: {rawFolders.Count}.");
        if (rawLabels.Count > TemplateLimits.MaxFolderLabels)
            return TemplateDecodeResult.Fail(TemplateDecodeError.LimitExceeded, $"Folder labels: {rawLabels.Count}.");

        var warnings = new List<TemplateWarning>();

        var folders = new List<string>();
        foreach (var folder in rawFolders)
        {
            if (folder is null)
            {
                warnings.Add(new TemplateWarning(TemplateWarningCode.InvalidEntryPath, NullSubject));
                continue;
            }

            if (TemplatePathValidator.IsValidFolder(folder))
                folders.Add(folder);
            else
                warnings.Add(new TemplateWarning(TemplateWarningCode.InvalidEntryPath, folder));
        }

        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, replacement) in rawLabels)
        {
            if (key is null)
            {
                warnings.Add(new TemplateWarning(TemplateWarningCode.UnknownFolderLabelKey, NullSubject));
                continue;
            }

            if (replacement is null)
            {
                return TemplateDecodeResult.Fail(
                    TemplateDecodeError.InvalidFolderLabelValue,
                    $"Folder label '{Preview(key)}' has a null replacement.");
            }

            // A malformed replacement value would inject a broken path into every fallback
            // proposal, so it is fatal; a malformed key only fails to match anything.
            if (!TemplatePathValidator.IsValidFolder(replacement) || replacement.Length == 0)
            {
                return TemplateDecodeResult.Fail(
                    TemplateDecodeError.InvalidFolderLabelValue,
                    $"Folder label '{Preview(key)}' has invalid replacement '{Preview(replacement)}'.");
            }

            if (!TemplatePathValidator.IsValidFolder(key) || key.Length == 0)
            {
                warnings.Add(new TemplateWarning(TemplateWarningCode.UnknownFolderLabelKey, key));
                continue;
            }

            labels[key] = replacement;
        }

        // Dropped here rather than inside TemplateDuplicateResolver: the resolver's contract is
        // non-null entries, and the boundary is the right place to enforce that.
        var safeEntries = new List<TemplateEntry>(rawEntries.Count);
        foreach (var entry in rawEntries)
        {
            if (entry is null || entry.N is null || entry.F is null)
            {
                warnings.Add(new TemplateWarning(TemplateWarningCode.InvalidEntryPath, entry?.N ?? NullSubject));
                continue;
            }

            safeEntries.Add(entry);
        }

        var resolution = TemplateDuplicateResolver.Resolve(safeEntries);
        warnings.AddRange(resolution.Warnings);

        var validated = new ValidatedOrganizationTemplate(
            document.Name,
            document.Author,
            document.Description,
            strategy,
            labels,
            folders,
            resolution.EntriesByNormalizedName);

        return new TemplateDecodeResult(validated, null, null, warnings);
    }
}
