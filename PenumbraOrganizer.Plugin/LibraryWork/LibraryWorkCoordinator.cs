using System.Diagnostics;

namespace PenumbraOrganizer.Plugin.LibraryWork;

/// <summary>
/// Runs a library job in three phases: Materialize on the framework thread, the whole of Process on
/// a background thread, Publish back on the framework thread. Deliberately holds no Dalamud
/// reference - the framework thread reaches it only by calling Update() once per frame, and the
/// staleness counter arrives through a delegate. That is what makes it unit-testable without a game
/// process.
///
/// Not a port of OperationController: Scan and Index are pure reads, so there is no journal, no
/// checkpoint, and no recovery. A run that dies is simply re-run.
/// </summary>
public sealed class LibraryWorkCoordinator<TSeed, TResult> : IDisposable
{
    public delegate Task<IReadOnlyList<TResult>> BackgroundScheduler(
        Func<IReadOnlyList<TResult>> work, CancellationToken ct);

    public static readonly TimeSpan MaterializeWarningThreshold = TimeSpan.FromMilliseconds(100);

    private readonly Func<long> _readEpoch;
    private readonly BackgroundScheduler _scheduler;
    private readonly Action<string>? _logWarning;
    private readonly TimeSpan _disposeWait;

    private ILibraryWorkJob<TSeed, TResult>? _job;
    private ILibraryWorkJob<TSeed, TResult>? _pendingJob;
    private CancellationTokenSource? _cts;
    private Task<IReadOnlyList<TResult>>? _task;
    private long _startEpoch;
    private int _processed;
    private int _total;
    private bool _disposed;

    public LibraryWorkStateSnapshot State { get; private set; } = LibraryWorkStateSnapshot.Idle;

    public LibraryWorkCoordinator(
        Func<long> readEpoch,
        BackgroundScheduler? scheduler = null,
        Action<string>? logWarning = null,
        TimeSpan? disposeWait = null)
    {
        _readEpoch = readEpoch;
        _scheduler = scheduler ?? ((work, ct) => Task.Run(work, ct));
        _logWarning = logWarning;
        _disposeWait = disposeWait ?? TimeSpan.FromSeconds(2);
    }

    /// <summary>
    /// Called from the UI thread. Takes ownership of the job and closes every admission gate that
    /// keys off Phase, but does no Penumbra work: materialization is deferred to the next Update()
    /// so that all IPC reads happen on the framework thread.
    /// </summary>
    public void Start(ILibraryWorkJob<TSeed, TResult> job)
    {
        // Without this, anything calling RunScan during teardown schedules fresh background work
        // into a plugin that is going away.
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (State.IsRunning)
            throw new InvalidOperationException($"{State.JobDisplayName} is already running.");

        _job = job;
        _pendingJob = job;
        _processed = 0;
        _total = 0;

        // Created here rather than at materialize time so a run can be cancelled during the pending
        // window. CanCancel stays false for that window on purpose: it is one frame, and a Cancel
        // button that appears and vanishes within a frame is worse than no button.
        _cts = new CancellationTokenSource();

        PublishRunning(LibraryWorkPhase.Materializing);
    }

    /// <summary>
    /// Framework thread. Captures the epoch, takes the Penumbra snapshot, and launches the worker.
    /// Always terminal for this Update: it either settles the run or starts the background task.
    /// </summary>
    private void MaterializePending()
    {
        var job = _pendingJob!;
        _pendingJob = null;

        // Checked before any Penumbra call: a run cancelled while pending must not touch Penumbra.
        if (_cts?.IsCancellationRequested == true)
        {
            Settle(LibraryWorkOutcome.Cancelled, null);
            return;
        }

        // Captured BEFORE the snapshot, never after. Capturing after would fold a change that
        // happened during materialization into the new baseline, letting a snapshot that spans two
        // Penumbra states publish as valid. That interval is exactly the one this design exists to
        // catch, so it is the last one to stop watching.
        _startEpoch = _readEpoch();

        LibraryWorkBatch<TSeed, TResult> batch;
        var materializeStarted = Stopwatch.GetTimestamp();
        try
        {
            batch = job.Materialize();
        }
        catch (Exception ex)
        {
            Settle(LibraryWorkOutcome.Failed, ex.Message);
            return;
        }

        // Materialization holds the framework thread for its whole duration, so it is measured
        // rather than assumed. 100ms is roughly six frames at 60fps: long enough not to fire on a
        // healthy library, short enough to catch a hitch a user would notice. A starting value to
        // revise once real numbers exist, not a claim about what is achievable.
        var materializeElapsed = Stopwatch.GetElapsedTime(materializeStarted);
        if (materializeElapsed > MaterializeWarningThreshold)
            _logWarning?.Invoke(
                $"{job.DisplayName}: materializing {batch.Items.Count} mods held the framework "
                + $"thread for {materializeElapsed.TotalMilliseconds:F0}ms.");

        // Penumbra mutated while the snapshot was being taken, so it may describe two different
        // states. Settling now turns a doomed multi-second scan into an immediate, accurate
        // message; waiting for the worker would reach the same verdict much later.
        if (_readEpoch() != _startEpoch)
        {
            Settle(LibraryWorkOutcome.StaleModList, null);
            return;
        }

        _total = batch.Items.Count;
        var ct = _cts!.Token;
        PublishRunning(LibraryWorkPhase.Computing);

        // A scheduler that throws synchronously (or hands back null) would otherwise leave
        // Phase == Computing with _task == null - a state Update() can never settle, permanently
        // gating Scan, Index, Apply, Restore, cleanup and backup with no recovery short of
        // reloading the plugin. Unreachable with Task.Run; the scheduler is an injectable boundary.
        try
        {
            _task = _scheduler(() => RunBatch(batch, ct), ct)
                ?? throw new InvalidOperationException("The background scheduler returned no task.");
        }
        catch (Exception ex)
        {
            Settle(LibraryWorkOutcome.Failed, ex.Message);
        }
    }

    // Background thread. Everything reachable from here is in LibraryWork.Pure.
    private IReadOnlyList<TResult> RunBatch(LibraryWorkBatch<TSeed, TResult> batch, CancellationToken ct)
    {
        batch.Processor.Prepare(ct);

        var results = new List<TResult>(batch.Items.Count);
        foreach (var item in batch.Items)
        {
            ct.ThrowIfCancellationRequested();
            if (batch.Processor.Process(item, ct) is { } result)
                results.Add(result);
            Interlocked.Increment(ref _processed);
        }

        return results;
    }

    /// <summary> Framework thread, once per update. </summary>
    public void Update()
    {
        if (_disposed)
            return;

        if (_pendingJob is not null)
        {
            MaterializePending();
            return;
        }

        if (_task is not { IsCompleted: true })
        {
            // Only republish when the counter actually moved, so an idle frame allocates nothing.
            if (State.Phase == LibraryWorkPhase.Computing && Volatile.Read(ref _processed) != State.ProcessedItems)
                PublishRunning(LibraryWorkPhase.Computing);
            return;
        }

        var task = _task;
        _task = null;

        if (task.IsCanceled)
        {
            Settle(LibraryWorkOutcome.Cancelled, null);
            return;
        }

        if (task.IsFaulted)
        {
            Settle(LibraryWorkOutcome.Failed, task.Exception!.GetBaseException().Message);
            return;
        }

        // Checked BEFORE the epoch and before Publish. The background task can finish in the same
        // frame the user clicks Cancel, leaving a RanToCompletion task and a cancellation the UI has
        // already acknowledged. Discarding a finished, valid result is safe precisely because these
        // runs are read-only: the cost is one wasted scan, versus the UI lying about what it did.
        if (_cts?.IsCancellationRequested == true)
        {
            Settle(LibraryWorkOutcome.Cancelled, null);
            return;
        }

        // Checked here rather than at the start of Publish so a stale result is never handed to a
        // consumer at all, not even briefly.
        if (_readEpoch() != _startEpoch)
        {
            Settle(LibraryWorkOutcome.StaleModList, null);
            return;
        }

        PublishRunning(LibraryWorkPhase.Publishing);
        try
        {
            _job!.Publish(task.Result);
        }
        catch (Exception ex)
        {
            Settle(LibraryWorkOutcome.Failed, ex.Message);
            return;
        }

        Settle(LibraryWorkOutcome.Completed, null);
    }

    public void RequestCancellation() => _cts?.Cancel();

    /// <summary>
    /// Force-settles the current run (if any) to <see cref="LibraryWorkOutcome.Failed"/> with
    /// <paramref name="reason"/>, releasing every gate that depends on Phase == Idle. Deliberately
    /// does not touch <see cref="_task"/>: the background task may still be running, and this method
    /// is giving up on observing it, not stopping it. Safe to call when nothing is running - it just
    /// overwrites State with a Failed/Idle snapshot - and Start() may be called again immediately
    /// afterwards.
    /// </summary>
    public void AbandonRun(string reason)
    {
        _job = null;
        _pendingJob = null;
        State = new LibraryWorkStateSnapshot(
            LibraryWorkPhase.Idle, JobDisplayName: null,
            Volatile.Read(ref _processed), _total,
            LastOutcome: LibraryWorkOutcome.Failed, LastError: reason, CanCancel: false);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _cts?.Cancel();
        try
        {
            // Bounded, not indefinite: Dalamud unloads our AssemblyLoadContext on plugin unload, and
            // a background task still executing our code through that unload is a real crash risk.
            // Per-item work is one file read, so the token is observed quickly in practice.
            //
            // This REDUCES the hazard; it does not remove it. If the wait expires the task is still
            // running, still holding its batch and processor, and still executing plugin assembly
            // code - clearing the fields below does not stop it. A synchronous filesystem call
            // blocked on an unresponsive network share cannot be interrupted at all.
            if (_task is { } task && !task.Wait(_disposeWait))
                _logWarning?.Invoke(
                    "Teardown integrity: a library work run was still executing when the plugin "
                    + "unloaded. This is unmanaged risk, not merely a slow run.");
        }
        catch (AggregateException)
        {
            // The run's own cancellation or failure. Teardown does not care why it ended.
        }

        _cts?.Dispose();
        _cts = null;
        _task = null;
        _job = null;
        _pendingJob = null;
    }

    private void PublishRunning(LibraryWorkPhase phase) =>
        State = new LibraryWorkStateSnapshot(
            phase, _job?.DisplayName,
            Volatile.Read(ref _processed), _total,
            LastOutcome: null, LastError: null,
            CanCancel: phase == LibraryWorkPhase.Computing);

    private void Settle(LibraryWorkOutcome outcome, string? error)
    {
        _cts?.Dispose();
        _cts = null;
        _task = null;
        _job = null;
        _pendingJob = null;
        State = new LibraryWorkStateSnapshot(
            LibraryWorkPhase.Idle, JobDisplayName: null,
            Volatile.Read(ref _processed), _total,
            LastOutcome: outcome, LastError: error, CanCancel: false);
    }
}
