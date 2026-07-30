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
                $"Unknown fallback strategy '{document.FallbackStrategy}'.");
        }

        if (document.Entries.Count > TemplateLimits.MaxEntries)
            return TemplateDecodeResult.Fail(TemplateDecodeError.LimitExceeded, $"Entries: {document.Entries.Count}.");
        if (document.Folders.Count > TemplateLimits.MaxFolders)
            return TemplateDecodeResult.Fail(TemplateDecodeError.LimitExceeded, $"Folders: {document.Folders.Count}.");
        if (document.FolderLabels.Count > TemplateLimits.MaxFolderLabels)
            return TemplateDecodeResult.Fail(TemplateDecodeError.LimitExceeded, $"Folder labels: {document.FolderLabels.Count}.");

        var warnings = new List<TemplateWarning>();

        var folders = new List<string>();
        foreach (var folder in document.Folders)
        {
            if (TemplatePathValidator.IsValidFolder(folder))
                folders.Add(folder);
            else
                warnings.Add(new TemplateWarning(TemplateWarningCode.InvalidEntryPath, folder));
        }

        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, replacement) in document.FolderLabels)
        {
            // A malformed replacement value would inject a broken path into every fallback
            // proposal, so it is fatal; a malformed key only fails to match anything.
            if (!TemplatePathValidator.IsValidFolder(replacement) || replacement.Length == 0)
            {
                return TemplateDecodeResult.Fail(
                    TemplateDecodeError.InvalidFolderLabelValue,
                    $"Folder label '{key}' has invalid replacement '{replacement}'.");
            }

            if (!TemplatePathValidator.IsValidFolder(key) || key.Length == 0)
            {
                warnings.Add(new TemplateWarning(TemplateWarningCode.UnknownFolderLabelKey, key));
                continue;
            }

            labels[key] = replacement;
        }

        var resolution = TemplateDuplicateResolver.Resolve(document.Entries);
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
