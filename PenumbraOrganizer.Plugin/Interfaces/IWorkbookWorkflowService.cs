using PenumbraOrganizer.Core.Models;

// Namespace deliberately matches the standalone app's PenumbraOrganizer.Core.Interfaces, not this
// file's own PenumbraOrganizer.Plugin.Interfaces folder -- the linked, unmodified
// WorkbookWorkflowService.cs declares ": IWorkbookWorkflowService" under that namespace, and this
// hand-written copy exists only so that resolves without linking the sibling repo's much larger
// Services.cs (which also declares IOrganizerSessionService and other unrelated interfaces). Keep
// this interface's members in sync with PenumbraOrganizer.Core/Interfaces/Services.cs's
// IWorkbookWorkflowService in the standalone app repo -- if they diverge, the linked service class
// fails to compile here, which is the safety net for genuine drift.
namespace PenumbraOrganizer.Core.Interfaces;

/// <summary>
/// Service for exporting and importing workbooks containing mod organization data.
/// </summary>
public interface IWorkbookWorkflowService
{
    /// <summary>
    /// Export mod inventory and proposals to a workbook file.
    /// </summary>
    Task<WorkbookExportResult> ExportAsync(
        ScanInventory inventory,
        IReadOnlyList<OrganizerModProposal> proposals,
        OrganizationPreferences organizationPreferences,
        string workbookPath,
        CancellationToken cancellationToken);

    /// <summary>
    /// Import workbook data and parse it into a structured result.
    /// </summary>
    Task<WorkbookImportResult> ImportAsync(
        string workbookPath,
        ScanInventory inventory,
        CancellationToken cancellationToken);
}
