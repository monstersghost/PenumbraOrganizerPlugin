namespace PenumbraOrganizer.Plugin.LibraryWork;

public enum LibraryWorkPhase
{
    Idle,
    Materializing, // framework thread: copying plain data out of the Penumbra adapters
    Computing,     // background thread: classification and disk I/O
    Publishing,    // framework thread: handing the finished result set to the consumer
}

public enum LibraryWorkOutcome { Completed, Cancelled, StaleModList, Failed }

/// <summary>
/// The only thing the UI is allowed to read. Published as a whole new instance after every
/// transition, never mutated in place - the same convention OperationStateSnapshot established.
/// ProcessedItems/TotalItems are retained on the Idle snapshot so a finished run can still show
/// its final counts.
/// </summary>
public sealed record LibraryWorkStateSnapshot(
    LibraryWorkPhase Phase,
    string? JobDisplayName,
    int ProcessedItems,
    int TotalItems,
    LibraryWorkOutcome? LastOutcome,
    string? LastError,
    bool CanCancel)
{
    public bool IsRunning => Phase != LibraryWorkPhase.Idle;

    public static LibraryWorkStateSnapshot Idle { get; } = new(
        LibraryWorkPhase.Idle, JobDisplayName: null, ProcessedItems: 0, TotalItems: 0,
        LastOutcome: null, LastError: null, CanCancel: false);
}

/// <summary> Framework-thread side of a library job. May touch Dalamud and Penumbra freely. </summary>
public interface ILibraryWorkJob<TSeed, TResult>
{
    string DisplayName { get; }

    /// <summary> Phase 1, framework thread. Copies plain data out of the IPC adapters and builds
    /// the processor that will run against it. Must not retain adapter-owned objects. </summary>
    LibraryWorkBatch<TSeed, TResult> Materialize();

    /// <summary> Phase 3, framework thread. Receives a fully-materialized result list. </summary>
    void Publish(IReadOnlyList<TResult> results);
}

/// <summary>
/// Phase 2. Implementations live in LibraryWork.Pure and may not reference Dalamud or Penumbra
/// types - LibraryWorkPurityTests enforces this. Constructed on the framework thread from plain
/// data, executed on a background thread.
/// </summary>
public interface ILibraryWorkProcessor<TSeed, TResult>
{
    /// <summary> One-time setup before any item is processed (loading files, building matchers). </summary>
    void Prepare(CancellationToken ct);

    /// <summary> Returns null to exclude the item from the published results. </summary>
    TResult? Process(TSeed item, CancellationToken ct);
}

public sealed record LibraryWorkBatch<TSeed, TResult>(
    IReadOnlyList<TSeed> Items,
    ILibraryWorkProcessor<TSeed, TResult> Processor);
