namespace PenumbraOrganizer.Plugin.Organizer.Templates;

/// <summary>
/// Bounds fragments of an untrusted template that get echoed back to the user. Both error details
/// and warning subjects are rendered in the UI, and a shared document can carry thousands of
/// entries whose names have no length limit of their own -- so every echoed fragment is truncated
/// at one place rather than trusted to be reasonable.
/// </summary>
public static class TemplateText
{
    public const int PreviewLength = 64;
    public const string NullSubject = "(null)";

    public static string Preview(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "(empty)";

        return value.Length <= PreviewLength ? value : value[..PreviewLength] + "...";
    }
}
