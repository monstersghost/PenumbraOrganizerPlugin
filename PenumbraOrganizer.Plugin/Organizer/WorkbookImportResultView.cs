namespace PenumbraOrganizer.Plugin.Organizer;

/// <summary>
/// Display-only projection of the workbook import result's summary fields, so MainWindow.cs
/// doesn't need a using directive into the workbook namespace just to declare a field. Lines are the
/// per-custom-code usage report.
/// </summary>
public sealed record WorkbookImportResultView(
    string Summary,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string>? Lines = null);
