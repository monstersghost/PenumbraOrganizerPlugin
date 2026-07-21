namespace PenumbraOrganizer.Plugin.Organizer;

/// <summary>
/// Display-only projection of the linked WorkbookImportResult's summary fields, so MainWindow.cs
/// doesn't need a using directive into PenumbraOrganizer.Infrastructure.Exports (the linked file's
/// original namespace) just to declare a field.
/// </summary>
public sealed record WorkbookImportResultView(string Summary, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);
