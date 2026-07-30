using PenumbraOrganizer.Plugin.LibraryWork;
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Windows;

/// <summary>
/// The whole UI lockout matrix as one pure function. OperationController owns Apply/Restore
/// lockout; the two library coordinators own scan/index lockout; this merges them so no call site
/// has to remember to consult all three, and so the rules can be tested without a game process.
///
/// This mirrors, but does not replace, Plugin's own admission checks - those are the invariant, this
/// is the presentation of it.
/// </summary>
public readonly record struct ActivityGates(
    bool CanScan,
    bool CanIndex,
    bool CanStartApply,
    bool CanStartRestore,
    bool CanRunFolderCleanup,
    bool CanRunFolderCleanupRollback,
    bool CanCreateBackup,
    bool CanStageProposals)
{
    public static ActivityGates Build(
        OperationStateSnapshot operation,
        LibraryWorkStateSnapshot scan,
        LibraryWorkStateSnapshot index)
    {
        var libraryBusy = scan.IsRunning || index.IsRunning;

        return new ActivityGates(
            CanScan: operation.CanScan && !libraryBusy,
            CanIndex: operation.CanIndex && !libraryBusy,
            CanStartApply: operation.CanStartApply && !libraryBusy,
            CanStartRestore: operation.CanStartRestore && !libraryBusy,
            CanRunFolderCleanup: operation.CanRunFolderCleanup && !libraryBusy,
            CanRunFolderCleanupRollback: operation.CanRunFolderCleanupRollback && !libraryBusy,
            CanCreateBackup: operation.CanCreateBackup && !libraryBusy,
            // A library run is read-only, but a completing scan replaces every row and resets every
            // ProposedPath - so staging must be blocked for its duration or the user's staged work is
            // silently wiped when it lands. Deliberately NOT gated on the operation snapshot: an
            // Apply in flight has no reason to stop the user preparing the next batch.
            CanStageProposals: !libraryBusy);
    }
}
