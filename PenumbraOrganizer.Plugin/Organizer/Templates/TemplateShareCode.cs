namespace PenumbraOrganizer.Plugin.Organizer.Templates;

public sealed record TemplateShareCodeDescription(string Code, int Length, bool ExceedsChatLimit);

/// <summary>
/// Measures a share code before the author tries to use it.
/// </summary>
/// <remarks>
/// A Discord message caps at 2000 characters -- roughly a hundred entries once deflated -- and a real
/// nine-hundred-mod library does not fit. The limitation is stated in the UI rather than hidden,
/// because the alternative is an author pasting a code that is silently truncated on send and an
/// importer being told their clipboard contains invalid base64. The file transport stays available at
/// any size; the code stays useful for small hand-curated templates.
/// </remarks>
public static class TemplateShareCode
{
    public const int ChatMessageLimit = 2000;

    public static TemplateShareCodeDescription Describe(OrganizationTemplate template)
    {
        var code = TemplateCodec.EncodeShareCode(template);
        return new TemplateShareCodeDescription(code, code.Length, code.Length > ChatMessageLimit);
    }
}
