namespace PenumbraOrganizer.Plugin.Organizer.Operations;

public enum SetModPathStatus
{
    Success, NothingChanged, ModMissing, InvalidArgument, PathRenameFailed,
    ProviderUnavailable, InvalidState, Rejected,
}

/// <summary> Design doc section 5, revised in this plan's second review round: a plugin-owned
/// status rather than the raw Penumbra.Api.Enums.PenumbraApiEc, so "the adapter itself is unusable"
/// (ProviderUnavailable) is distinguishable from an item-level rejection - the raw provider enum
/// cannot express that distinction, and the stop-vs-continue policy (Task 4) depends on it.
/// ProviderResultName preserves the real enum value's name for diagnostics only. </summary>
public sealed record SetModPathResult(SetModPathStatus Status, string? ProviderResultName, string? Diagnostic);

public enum LiveModReadStatus { Success, TemporarilyUnavailable, ProviderUnavailable, InvalidData }

/// <summary> Design doc section 6. Snapshot is null for any non-Success status. </summary>
public sealed record LiveModReadResult(LiveModReadStatus Status, LiveModSnapshot? Snapshot);

public enum RefreshStatus { Success, TemporarilyUnavailable, ProviderUnavailable, InvalidState }

/// <summary> Design doc section 5b. </summary>
public sealed record RefreshResult(RefreshStatus Status);

/// <summary>
/// Narrow Penumbra IPC boundary the execution engine depends on instead of the concrete Plugin
/// class - design doc section 2. Deliberately has no dependency on Penumbra.Api: a real
/// implementation (PenumbraOperationsAdapter, wrapping the actual Penumbra IPC subscribers and
/// translating PenumbraApiEc into SetModPathResult) is built in Plan B2. This interface is what
/// makes everything in Plan B1 unit-testable without Dalamud.
/// </summary>
public interface IPenumbraOperations
{
    LiveModReadResult GetLiveMods();
    SetModPathResult SetModPath(string identifier, string targetPath);
    RefreshResult RequestPostMutationRefresh();
}
