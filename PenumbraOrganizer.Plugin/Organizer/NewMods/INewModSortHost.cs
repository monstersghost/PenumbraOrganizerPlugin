using PenumbraOrganizer.Plugin.LibraryWork;
using PenumbraOrganizer.Plugin.Organizer.Collections;
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Organizer.NewMods;

/// <summary>What a scoped sort did: rows it covered and rows whose proposal now differs from where they are.</summary>
public readonly record struct ScopedSortResult(int InScope, int Changed);

/// <summary>
/// Everything NewModSortPipeline needs from the plugin, so the pipeline stays a pure state machine.
/// Every member is called on the framework thread. Start methods return null when the work started,
/// otherwise the reason it did not (the adapter catches and converts exceptions).
/// </summary>
public interface INewModSortHost
{
    /// <summary>The user's choice for new mods (Configuration.NewModAction).</summary>
    NewModAction Setting { get; }

    bool IsWindowOpen { get; }

    /// <summary>True when the current scan has unapplied proposals that a rescan would discard.</summary>
    bool HasStagedProposals { get; }

    /// <summary>True when this session has tool unprotect overrides that a rescan would reset.</summary>
    bool HasSessionOverrides { get; }

    /// <summary>Why a scan or an Apply cannot start now (OperationController.AdmissionRejectionReason), or null.</summary>
    string? AdmissionRejection();

    /// <summary>Starts a library scan. Null when it started, else the reason.</summary>
    string? StartScan();

    /// <summary>OrganizerState.ScanGeneration: advances when a scan publishes.</summary>
    int ScanGeneration { get; }

    LibraryWorkStateSnapshot ScanState { get; }

    /// <summary>Runs the Sort tab's saved settings over the given scope and stages the proposals.</summary>
    ScopedSortResult SortScoped(SortScope scope);

    /// <summary>Starts an Apply of the staged proposals (Plugin.StartApplyOperation). Null when it started, else the reason.</summary>
    string? StartApply();

    long OperationCompletionGeneration { get; }

    OperationStateSnapshot OperationState { get; }

    /// <summary>Opens the main window on the Review tab.</summary>
    void OpenReview();

    /// <summary>Prints one line to the game chat.</summary>
    void Chat(string message);
}
