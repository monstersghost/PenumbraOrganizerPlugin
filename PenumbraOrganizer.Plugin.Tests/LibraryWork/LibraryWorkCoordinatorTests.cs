using PenumbraOrganizer.Plugin.LibraryWork;

namespace PenumbraOrganizer.Plugin.Tests.LibraryWork;

public class LibraryWorkCoordinatorTests
{
    // Runs nothing until the test says so, so every assertion is deterministic and no test sleeps.
    private sealed class ManualScheduler
    {
        private Func<IReadOnlyList<string>>? _work;
        private CancellationToken _ct;
        private TaskCompletionSource<IReadOnlyList<string>>? _tcs;

        public int ScheduleCalls { get; private set; }

        public Task<IReadOnlyList<string>> Schedule(Func<IReadOnlyList<string>> work, CancellationToken ct)
        {
            ScheduleCalls++;
            _work = work;
            _ct = ct;
            _tcs = new TaskCompletionSource<IReadOnlyList<string>>();
            return _tcs.Task;
        }

        public void RunToCompletion()
        {
            // No-op if Schedule was never called: some terminal paths (materialize failure,
            // cancellation before scheduling, staleness caught during materialize) settle before
            // reaching the scheduler at all, and callers exercise a single sequence across all of them.
            if (_tcs is null)
                return;

            try
            {
                _tcs!.SetResult(_work!());
            }
            catch (OperationCanceledException)
            {
                _tcs!.SetCanceled(_ct);
            }
            catch (Exception ex)
            {
                _tcs!.SetException(ex);
            }
        }
    }

    private sealed class FakeProcessor : ILibraryWorkProcessor<string, string>
    {
        public int PrepareCalls { get; private set; }
        public Exception? PrepareThrows { get; init; }
        public Exception? ProcessThrows { get; init; }
        public Func<string, bool>? Exclude { get; init; }
        public Action? BeforeEachItem { get; init; }

        public void Prepare(CancellationToken ct)
        {
            PrepareCalls++;
            if (PrepareThrows is not null)
                throw PrepareThrows;
        }

        public string? Process(string item, CancellationToken ct)
        {
            BeforeEachItem?.Invoke();
            ct.ThrowIfCancellationRequested();
            if (ProcessThrows is not null)
                throw ProcessThrows;
            return Exclude?.Invoke(item) == true ? null : item.ToUpperInvariant();
        }
    }

    private sealed class FakeJob : ILibraryWorkJob<string, string>
    {
        public required IReadOnlyList<string> Items { get; init; }
        public required ILibraryWorkProcessor<string, string> Processor { get; init; }
        public Exception? MaterializeThrows { get; init; }
        public Exception? PublishThrows { get; init; }
        public Action? DuringMaterialize { get; init; }
        public int MaterializeCalls { get; private set; }

        public string DisplayName => "Fake";
        public List<IReadOnlyList<string>> Published { get; } = [];

        public LibraryWorkBatch<string, string> Materialize()
        {
            MaterializeCalls++;
            DuringMaterialize?.Invoke();
            if (MaterializeThrows is not null)
                throw MaterializeThrows;
            return new LibraryWorkBatch<string, string>(Items, Processor);
        }

        public void Publish(IReadOnlyList<string> results)
        {
            if (PublishThrows is not null)
                throw PublishThrows;
            Published.Add(results);
        }
    }

    private static (LibraryWorkCoordinator<string, string> Coordinator, ManualScheduler Scheduler, Func<long> Epoch)
        NewCoordinator(Func<long>? epoch = null, Action<string>? logInfo = null)
    {
        var scheduler = new ManualScheduler();
        var readEpoch = epoch ?? (() => 0L);
        var coordinator = new LibraryWorkCoordinator<string, string>(
            readEpoch, isFrameworkThread: () => true, scheduler.Schedule, logInfo: logInfo);
        return (coordinator, scheduler, readEpoch);
    }

    [Fact]
    public void State_Initially_Idle()
    {
        var (coordinator, _, _) = NewCoordinator();

        Assert.Equal(LibraryWorkPhase.Idle, coordinator.State.Phase);
        Assert.False(coordinator.State.IsRunning);
        Assert.Null(coordinator.State.LastOutcome);
    }

    [Fact]
    public void StartThenUpdate_MovesToComputing_WithoutRunningTheProcessor()
    {
        var processor = new FakeProcessor();
        var job = new FakeJob { Items = ["a", "b"], Processor = processor };
        var (coordinator, _, _) = NewCoordinator();

        coordinator.Start(job);
        Assert.Equal(LibraryWorkPhase.Materializing, coordinator.State.Phase);

        coordinator.Update();

        Assert.Equal(LibraryWorkPhase.Computing, coordinator.State.Phase);
        Assert.Equal(2, coordinator.State.TotalItems);
        Assert.Equal(0, coordinator.State.ProcessedItems);
        Assert.True(coordinator.State.CanCancel);
        Assert.Equal(0, processor.PrepareCalls);
        Assert.Empty(job.Published);
    }

    [Fact]
    public void HappyPath_PreparesOnce_ProcessesEveryItem_PublishesOnce_AndReturnsToIdle()
    {
        var processor = new FakeProcessor();
        var job = new FakeJob { Items = ["a", "b", "c"], Processor = processor };
        var (coordinator, scheduler, _) = NewCoordinator();

        coordinator.Start(job);
        coordinator.Update();
        scheduler.RunToCompletion();
        coordinator.Update();

        Assert.Equal(1, processor.PrepareCalls);
        var published = Assert.Single(job.Published);
        Assert.Equal(["A", "B", "C"], published);
        Assert.Equal(LibraryWorkPhase.Idle, coordinator.State.Phase);
        Assert.Equal(LibraryWorkOutcome.Completed, coordinator.State.LastOutcome);
        Assert.Null(coordinator.State.LastError);
        Assert.Equal(3, coordinator.State.ProcessedItems);
    }

    [Fact]
    public void Process_ReturningNull_ExcludesTheItemButStillCountsAsProcessed()
    {
        var processor = new FakeProcessor { Exclude = item => item == "b" };
        var job = new FakeJob { Items = ["a", "b", "c"], Processor = processor };
        var (coordinator, scheduler, _) = NewCoordinator();

        coordinator.Start(job);
        coordinator.Update();
        scheduler.RunToCompletion();
        coordinator.Update();

        Assert.Equal(["A", "C"], Assert.Single(job.Published));
        Assert.Equal(3, coordinator.State.ProcessedItems);
    }

    [Fact]
    public void Start_WhileRunning_Throws()
    {
        var job = new FakeJob { Items = ["a"], Processor = new FakeProcessor() };
        var (coordinator, _, _) = NewCoordinator();
        coordinator.Start(job);

        Assert.Throws<InvalidOperationException>(() => coordinator.Start(job));
    }

    [Fact]
    public void Update_BeforeCompletion_LeavesPhaseComputing()
    {
        var job = new FakeJob { Items = ["a"], Processor = new FakeProcessor() };
        var (coordinator, _, _) = NewCoordinator();
        coordinator.Start(job);

        coordinator.Update();

        Assert.Equal(LibraryWorkPhase.Computing, coordinator.State.Phase);
        Assert.Empty(job.Published);
    }

    [Fact]
    public void Cancellation_DoesNotPublish_AndReportsCancelled()
    {
        var processor = new FakeProcessor();
        var job = new FakeJob { Items = ["a", "b"], Processor = processor };
        var (coordinator, scheduler, _) = NewCoordinator();

        coordinator.Start(job);
        coordinator.Update();
        coordinator.RequestCancellation();
        scheduler.RunToCompletion();
        coordinator.Update();

        Assert.Empty(job.Published);
        Assert.Equal(LibraryWorkPhase.Idle, coordinator.State.Phase);
        Assert.Equal(LibraryWorkOutcome.Cancelled, coordinator.State.LastOutcome);
    }

    [Fact]
    public void ModListChangedDuringRun_DiscardsTheResult()
    {
        var epoch = 0L;
        var job = new FakeJob { Items = ["a"], Processor = new FakeProcessor() };
        var scheduler = new ManualScheduler();
        var coordinator = new LibraryWorkCoordinator<string, string>(
            () => epoch, isFrameworkThread: () => true, scheduler.Schedule);

        coordinator.Start(job);
        coordinator.Update();
        epoch = 1; // a Penumbra mod event landed mid-run
        scheduler.RunToCompletion();
        coordinator.Update();

        Assert.Empty(job.Published);
        Assert.Equal(LibraryWorkOutcome.StaleModList, coordinator.State.LastOutcome);
        Assert.Null(coordinator.State.LastError);
    }

    [Fact]
    public void MaterializeThrowing_FailsBeforeAnyBackgroundWork()
    {
        var processor = new FakeProcessor();
        var job = new FakeJob
        {
            Items = ["a"],
            Processor = processor,
            MaterializeThrows = new InvalidOperationException("penumbra is not ready"),
        };
        var (coordinator, scheduler, _) = NewCoordinator();

        coordinator.Start(job);
        coordinator.Update();

        Assert.Equal(LibraryWorkOutcome.Failed, coordinator.State.LastOutcome);
        Assert.Equal(0, scheduler.ScheduleCalls);

        Assert.Equal(LibraryWorkPhase.Idle, coordinator.State.Phase);
        Assert.Equal("penumbra is not ready", coordinator.State.LastError);
        Assert.Equal(0, processor.PrepareCalls);
        Assert.Empty(job.Published);
    }

    [Fact]
    public void PrepareThrowing_FailsWithoutPublishing()
    {
        var processor = new FakeProcessor { PrepareThrows = new IOException("npc list unreadable") };
        var job = new FakeJob { Items = ["a"], Processor = processor };
        var (coordinator, scheduler, _) = NewCoordinator();

        coordinator.Start(job);
        coordinator.Update();
        scheduler.RunToCompletion();
        coordinator.Update();

        Assert.Equal(LibraryWorkOutcome.Failed, coordinator.State.LastOutcome);
        Assert.Equal("npc list unreadable", coordinator.State.LastError);
        Assert.Empty(job.Published);
    }

    [Fact]
    public void ProcessThrowing_AbortsTheWholeRun()
    {
        var processor = new FakeProcessor { ProcessThrows = new InvalidDataException("bad item") };
        var job = new FakeJob { Items = ["a", "b"], Processor = processor };
        var (coordinator, scheduler, _) = NewCoordinator();

        coordinator.Start(job);
        coordinator.Update();
        scheduler.RunToCompletion();
        coordinator.Update();

        Assert.Equal(LibraryWorkOutcome.Failed, coordinator.State.LastOutcome);
        Assert.Equal("bad item", coordinator.State.LastError);
        Assert.Empty(job.Published);
    }

    [Fact]
    public void PublishThrowing_ReportsFailed()
    {
        var job = new FakeJob
        {
            Items = ["a"],
            Processor = new FakeProcessor(),
            PublishThrows = new InvalidOperationException("load failed"),
        };
        var (coordinator, scheduler, _) = NewCoordinator();

        coordinator.Start(job);
        coordinator.Update();
        scheduler.RunToCompletion();
        coordinator.Update();

        Assert.Equal(LibraryWorkPhase.Idle, coordinator.State.Phase);
        Assert.Equal(LibraryWorkOutcome.Failed, coordinator.State.LastOutcome);
        Assert.Equal("load failed", coordinator.State.LastError);
    }

    [Fact]
    public void AfterAnyTerminalOutcome_StartIsAllowedAgain()
    {
        var job = new FakeJob { Items = ["a"], Processor = new FakeProcessor() };
        var (coordinator, scheduler, _) = NewCoordinator();
        coordinator.Start(job);
        coordinator.Update();
        scheduler.RunToCompletion();
        coordinator.Update();

        var second = new FakeJob { Items = ["b"], Processor = new FakeProcessor() };
        coordinator.Start(second);
        coordinator.Update();

        Assert.Equal(LibraryWorkPhase.Computing, coordinator.State.Phase);
    }

    [Fact]
    public void ProgressCounters_AdvanceAsItemsAreProcessed()
    {
        var seen = 0;
        var processor = new FakeProcessor { BeforeEachItem = () => seen++ };
        var job = new FakeJob { Items = ["a", "b", "c", "d"], Processor = processor };
        var (coordinator, scheduler, _) = NewCoordinator();

        coordinator.Start(job);
        coordinator.Update();
        Assert.Equal(0, coordinator.State.ProcessedItems);
        Assert.Equal(4, coordinator.State.TotalItems);

        scheduler.RunToCompletion();
        coordinator.Update();

        Assert.Equal(4, seen);
        Assert.Equal(4, coordinator.State.ProcessedItems);
    }

    [Fact]
    public void CancellationRequestedAfterComputeButBeforeUpdate_DiscardsTheCompletedResult()
    {
        // The one-frame race the first draft published through: the task finished, then the user
        // clicked Cancel, then Update() ran. Honouring the cancel is free here because these runs
        // are read-only - the cost is one wasted scan, versus the UI lying about what it did.
        var job = new FakeJob { Items = ["a", "b"], Processor = new FakeProcessor() };
        var (coordinator, scheduler, _) = NewCoordinator();

        coordinator.Start(job);
        coordinator.Update();
        scheduler.RunToCompletion();
        coordinator.RequestCancellation();
        coordinator.Update();

        Assert.Empty(job.Published);
        Assert.Equal(LibraryWorkOutcome.Cancelled, coordinator.State.LastOutcome);
    }

    [Fact]
    public void SchedulerThrowingSynchronously_FailsInsteadOfWedging()
    {
        var job = new FakeJob { Items = ["a"], Processor = new FakeProcessor() };
        var coordinator = new LibraryWorkCoordinator<string, string>(
            () => 0L, isFrameworkThread: () => true,
            (_, _) => throw new InvalidOperationException("no thread available"));

        coordinator.Start(job);
        coordinator.Update();

        Assert.Equal(LibraryWorkPhase.Idle, coordinator.State.Phase);
        Assert.Equal(LibraryWorkOutcome.Failed, coordinator.State.LastOutcome);
        Assert.Equal("no thread available", coordinator.State.LastError);
    }

    [Fact]
    public void SchedulerReturningNull_FailsInsteadOfWedging()
    {
        var job = new FakeJob { Items = ["a"], Processor = new FakeProcessor() };
        var coordinator = new LibraryWorkCoordinator<string, string>(
            () => 0L, isFrameworkThread: () => true, (_, _) => null!);

        coordinator.Start(job);
        coordinator.Update();

        Assert.Equal(LibraryWorkPhase.Idle, coordinator.State.Phase);
        Assert.Equal(LibraryWorkOutcome.Failed, coordinator.State.LastOutcome);
    }

    [Fact]
    public void AfterSchedulerFailure_StartIsAllowedAgain()
    {
        var thrown = true;
        var scheduler = new ManualScheduler();
        var coordinator = new LibraryWorkCoordinator<string, string>(
            () => 0L, isFrameworkThread: () => true,
            (work, ct) => thrown ? throw new InvalidOperationException("boom") : scheduler.Schedule(work, ct));

        coordinator.Start(new FakeJob { Items = ["a"], Processor = new FakeProcessor() });
        coordinator.Update();
        Assert.Equal(LibraryWorkOutcome.Failed, coordinator.State.LastOutcome);

        thrown = false;
        coordinator.Start(new FakeJob { Items = ["b"], Processor = new FakeProcessor() });
        coordinator.Update();

        Assert.Equal(LibraryWorkPhase.Computing, coordinator.State.Phase);
    }

    [Fact]
    public void EmptyBatch_PublishesAnEmptyResultAndCompletes()
    {
        var job = new FakeJob { Items = [], Processor = new FakeProcessor() };
        var (coordinator, scheduler, _) = NewCoordinator();

        coordinator.Start(job);
        coordinator.Update();
        scheduler.RunToCompletion();
        coordinator.Update();

        Assert.Empty(Assert.Single(job.Published));
        Assert.Equal(LibraryWorkOutcome.Completed, coordinator.State.LastOutcome);
        Assert.Equal(0, coordinator.State.TotalItems);
    }

    [Fact]
    public void Dispose_DuringARun_DoesNotPublish()
    {
        var job = new FakeJob { Items = ["a"], Processor = new FakeProcessor() };
        var scheduler = new ManualScheduler();
        // Zero dispose timeout: the real 2s wait belongs in the one test that covers the warning,
        // not in every test that happens to dispose.
        var coordinator = new LibraryWorkCoordinator<string, string>(
            () => 0L, isFrameworkThread: () => true, scheduler.Schedule, disposeWait: TimeSpan.Zero);
        coordinator.Start(job);
        coordinator.Update();

        coordinator.Dispose();
        scheduler.RunToCompletion();
        // Publish only ever happens inside Update(); without this call the assertion below
        // would pass vacuously regardless of Dispose()'s behavior. Do not remove it.
        coordinator.Update();

        Assert.Empty(job.Published);
    }

    [Fact]
    public void StartAfterDispose_IsRejected()
    {
        var coordinator = new LibraryWorkCoordinator<string, string>(
            () => 0L, isFrameworkThread: () => true, new ManualScheduler().Schedule, disposeWait: TimeSpan.Zero);
        coordinator.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
            coordinator.Start(new FakeJob { Items = ["a"], Processor = new FakeProcessor() }));
    }

    [Fact]
    public void UpdateAfterDispose_DoesNotPublish()
    {
        var job = new FakeJob { Items = ["a"], Processor = new FakeProcessor() };
        var scheduler = new ManualScheduler();
        var coordinator = new LibraryWorkCoordinator<string, string>(
            () => 0L, isFrameworkThread: () => true, scheduler.Schedule, disposeWait: TimeSpan.Zero);
        coordinator.Start(job);
        coordinator.Update();
        scheduler.RunToCompletion();

        coordinator.Dispose();
        coordinator.Update();

        Assert.Empty(job.Published);
    }

    [Fact]
    public void AbandonRun_WhileComputing_SettlesToFailedAndReturnsToIdle()
    {
        var job = new FakeJob { Items = ["a"], Processor = new FakeProcessor() };
        var (coordinator, _, _) = NewCoordinator();
        coordinator.Start(job);

        coordinator.AbandonRun("gave up");

        Assert.Equal(LibraryWorkPhase.Idle, coordinator.State.Phase);
        Assert.Equal(LibraryWorkOutcome.Failed, coordinator.State.LastOutcome);
        Assert.Equal("gave up", coordinator.State.LastError);
    }

    [Fact]
    public void AbandonRun_AllowsStartAgainAfterwards()
    {
        var job = new FakeJob { Items = ["a"], Processor = new FakeProcessor() };
        var (coordinator, _, _) = NewCoordinator();
        coordinator.Start(job);
        coordinator.AbandonRun("gave up");

        var second = new FakeJob { Items = ["b"], Processor = new FakeProcessor() };
        coordinator.Start(second);
        coordinator.Update();

        Assert.Equal(LibraryWorkPhase.Computing, coordinator.State.Phase);
    }

    [Fact]
    public void AbandonRun_WhenIdle_IsASafeNoOp()
    {
        var (coordinator, _, _) = NewCoordinator();

        coordinator.AbandonRun("nothing was running");

        Assert.Equal(LibraryWorkPhase.Idle, coordinator.State.Phase);
        Assert.Equal(LibraryWorkOutcome.Failed, coordinator.State.LastOutcome);
        Assert.Equal("nothing was running", coordinator.State.LastError);

        // And it does not block a subsequent Start.
        coordinator.Start(new FakeJob { Items = ["a"], Processor = new FakeProcessor() });
        coordinator.Update();
        Assert.Equal(LibraryWorkPhase.Computing, coordinator.State.Phase);
    }

    [Fact]
    public void Dispose_WhenTheRunDoesNotStop_LogsATeardownWarning()
    {
        var warnings = new List<string>();
        var scheduler = new ManualScheduler();
        var coordinator = new LibraryWorkCoordinator<string, string>(
            () => 0L, isFrameworkThread: () => true, scheduler.Schedule,
            logWarning: warnings.Add, disposeWait: TimeSpan.FromMilliseconds(50));
        coordinator.Start(new FakeJob { Items = ["a"], Processor = new FakeProcessor() });
        coordinator.Update();

        coordinator.Dispose(); // the manual scheduler's task never completes

        Assert.Single(warnings);
        Assert.Contains("teardown", warnings[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Start_DoesNotMaterializeOrSchedule_UntilUpdate()
    {
        var job = new FakeJob { Items = ["a", "b"], Processor = new FakeProcessor() };
        var (coordinator, scheduler, _) = NewCoordinator();

        coordinator.Start(job);
        Assert.Equal(0, job.MaterializeCalls);
        // Also pins that Start does not schedule a closure that would materialize on a worker.
        Assert.Equal(0, scheduler.ScheduleCalls);

        coordinator.Update();
        Assert.Equal(1, job.MaterializeCalls);
        Assert.Equal(1, scheduler.ScheduleCalls);
    }

    [Fact]
    public void Start_ClosesTheGateImmediately_BeforeAnyUpdate()
    {
        var job = new FakeJob { Items = ["a"], Processor = new FakeProcessor() };
        var (coordinator, _, _) = NewCoordinator();

        coordinator.Start(job);

        Assert.Equal(LibraryWorkPhase.Materializing, coordinator.State.Phase);
        Assert.True(coordinator.State.IsRunning);
        Assert.Throws<InvalidOperationException>(() => coordinator.Start(job));
    }

    [Fact]
    public void Update_Twice_MaterializesOnlyOnce()
    {
        var job = new FakeJob { Items = ["a"], Processor = new FakeProcessor() };
        var (coordinator, _, _) = NewCoordinator();

        coordinator.Start(job);
        coordinator.Update();
        coordinator.Update();

        Assert.Equal(1, job.MaterializeCalls);
    }

    [Fact]
    public void PendingRun_IsCancellableByCode_ButNotOfferedToTheUser()
    {
        var job = new FakeJob { Items = ["a"], Processor = new FakeProcessor() };
        var (coordinator, scheduler, _) = NewCoordinator();

        coordinator.Start(job);

        // Deliberate asymmetry: the pending window is one frame, so a Cancel button that appears
        // and vanishes within it would be worse than none. Cancellation still works if code asks.
        Assert.False(coordinator.State.CanCancel);

        coordinator.RequestCancellation();
        coordinator.Update();

        Assert.Equal(0, job.MaterializeCalls);
        Assert.Equal(0, scheduler.ScheduleCalls);
        Assert.Equal(LibraryWorkPhase.Idle, coordinator.State.Phase);
        Assert.Equal(LibraryWorkOutcome.Cancelled, coordinator.State.LastOutcome);
        Assert.Empty(job.Published);
    }

    [Fact]
    public void DisposeBeforeUpdate_DoesNotMaterializeOrPublish()
    {
        var job = new FakeJob { Items = ["a"], Processor = new FakeProcessor() };
        var (coordinator, scheduler, _) = NewCoordinator();

        coordinator.Start(job);
        coordinator.Dispose();
        coordinator.Update();

        Assert.Equal(0, job.MaterializeCalls);
        Assert.Equal(0, scheduler.ScheduleCalls);
        Assert.Empty(job.Published);

        // Dispose deliberately does NOT normalize the published snapshot. Nothing reads State after
        // teardown, and rewriting it would invent a terminal outcome that never happened. Asserted
        // so the choice is explicit rather than accidental.
        Assert.Equal(LibraryWorkPhase.Materializing, coordinator.State.Phase);
    }

    [Fact]
    public void MaterializeFailure_ClearsPendingJob_AndStartIsAllowedAgain()
    {
        var boom = new InvalidOperationException("Penumbra is unavailable.");
        var failing = new FakeJob { Items = [], Processor = new FakeProcessor(), MaterializeThrows = boom };
        var (coordinator, _, _) = NewCoordinator();

        coordinator.Start(failing);
        coordinator.Update();

        Assert.Equal(LibraryWorkOutcome.Failed, coordinator.State.LastOutcome);
        Assert.Equal("Penumbra is unavailable.", coordinator.State.LastError);

        coordinator.Update(); // must not retry the failed job
        Assert.Equal(1, failing.MaterializeCalls);

        var good = new FakeJob { Items = ["a"], Processor = new FakeProcessor() };
        coordinator.Start(good);
        coordinator.Update();
        Assert.Equal(1, good.MaterializeCalls);
    }

    [Fact]
    public void Epoch_ChangedBetweenStartAndUpdate_DoesNotInvalidateTheRun()
    {
        var epoch = 10L;
        var (coordinator, scheduler, _) = NewCoordinator(() => epoch);
        var job = new FakeJob { Items = ["a"], Processor = new FakeProcessor() };

        coordinator.Start(job);
        epoch = 11L; // the snapshot has not been taken yet, so it will represent epoch 11
        coordinator.Update();
        scheduler.RunToCompletion();
        coordinator.Update();

        Assert.Equal(LibraryWorkOutcome.Completed, coordinator.State.LastOutcome);
        Assert.Single(job.Published);
    }

    [Fact]
    public void Epoch_ChangedDuringMaterialize_InvalidatesTheRunImmediately()
    {
        var epoch = 10L;
        var (coordinator, scheduler, _) = NewCoordinator(() => epoch);
        var job = new FakeJob
        {
            Items = ["a"],
            Processor = new FakeProcessor(),
            // Penumbra mutating while the snapshot is taken: it may describe two different states.
            DuringMaterialize = () => epoch = 11L,
        };

        coordinator.Start(job);
        coordinator.Update();

        Assert.Equal(LibraryWorkOutcome.StaleModList, coordinator.State.LastOutcome);
        Assert.Empty(job.Published);
        Assert.Equal(0, scheduler.ScheduleCalls); // settled without paying for a doomed worker
    }

    [Fact]
    public void Update_OffTheFrameworkThread_ThrowsWithoutMaterializing()
    {
        var job = new FakeJob { Items = ["a"], Processor = new FakeProcessor() };
        var scheduler = new ManualScheduler();
        var coordinator = new LibraryWorkCoordinator<string, string>(
            () => 0L, isFrameworkThread: () => false, scheduler.Schedule);

        coordinator.Start(job);

        var ex = Assert.Throws<InvalidOperationException>(() => coordinator.Update());
        Assert.Contains("framework thread", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, job.MaterializeCalls);
    }

    [Fact]
    public void Update_OffTheFrameworkThread_DoesNotPublishCompletedWork()
    {
        // The case a materialize-only guard would miss entirely: by the time the worker finishes,
        // _pendingJob is null, so a guard inside MaterializePending never runs again.
        var onFrameworkThread = true;
        var scheduler = new ManualScheduler();
        var job = new FakeJob { Items = ["a"], Processor = new FakeProcessor() };
        var coordinator = new LibraryWorkCoordinator<string, string>(
            () => 0L, isFrameworkThread: () => onFrameworkThread, scheduler.Schedule);

        coordinator.Start(job);
        coordinator.Update();
        scheduler.RunToCompletion();

        onFrameworkThread = false;

        Assert.Throws<InvalidOperationException>(() => coordinator.Update());
        Assert.Empty(job.Published);
    }

    [Fact]
    public void AbandonRunAfterAWrongThreadThrow_DoesNotStrandThePendingJob()
    {
        var onFrameworkThread = false;
        var job = new FakeJob { Items = ["a"], Processor = new FakeProcessor() };
        var scheduler = new ManualScheduler();
        var coordinator = new LibraryWorkCoordinator<string, string>(
            () => 0L, isFrameworkThread: () => onFrameworkThread, scheduler.Schedule);

        coordinator.Start(job);
        Assert.Throws<InvalidOperationException>(() => coordinator.Update());

        // This is what Plugin.OnFrameworkUpdate does when Update() throws.
        coordinator.AbandonRun("Update threw.");

        onFrameworkThread = true;
        coordinator.Update();

        // The abandoned job must not come back to life on a later, correctly-threaded update.
        Assert.Equal(0, job.MaterializeCalls);
        Assert.Equal(0, scheduler.ScheduleCalls);
    }

    private static List<string> WithoutTimings(IEnumerable<string> lines) =>
        // Elapsed values are non-deterministic, so the ordered comparison below drops them and the
        // materialize line's contents are asserted separately.
        lines.Select(l => System.Text.RegularExpressions.Regex.Replace(l, @" elapsedMs=\d+", "")).ToList();

    [Fact]
    public void Checkpoints_RecordEveryBoundaryInOrder()
    {
        var lines = new List<string>();
        var (coordinator, scheduler, _) = NewCoordinator(logInfo: lines.Add);
        var job = new FakeJob { Items = ["a"], Processor = new FakeProcessor() };

        coordinator.Start(job);
        coordinator.Update();
        scheduler.RunToCompletion();
        coordinator.Update();

        Assert.Equal(
        [
            "[Fake:1] requested",
            "[Fake:1] materialize begin",
            "[Fake:1] materialize complete items=1 epoch=0",
            "[Fake:1] worker started",
            "[Fake:1] worker complete results=1",
            "[Fake:1] publish begin capturedEpoch=0 currentEpoch=0",
            "[Fake:1] publish complete",
            "[Fake:1] settled Completed",
        ], WithoutTimings(lines));
    }

    [Fact]
    public void Checkpoints_RunIdIncrementsPerRun_AndSurvivesSettlement()
    {
        var lines = new List<string>();
        var (coordinator, scheduler, _) = NewCoordinator(logInfo: lines.Add);

        coordinator.Start(new FakeJob { Items = [], Processor = new FakeProcessor() });
        coordinator.Update();
        scheduler.RunToCompletion();
        coordinator.Update();
        coordinator.Start(new FakeJob { Items = [], Processor = new FakeProcessor() });

        // The terminal line must still carry its label, which it cannot if identity is read from
        // State (JobDisplayName is null once settled).
        Assert.Contains("[Fake:1] settled Completed", lines);
        Assert.Contains("[Fake:2] requested", lines);
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("stale")]
    [InlineData("materialize-failure")]
    [InlineData("worker-fault")]
    [InlineData("scheduler-failure")]
    [InlineData("publish-failure")]
    public void Checkpoints_EveryTerminalPath_LogsItsOutcome(string scenario)
    {
        var lines = new List<string>();
        var epoch = 0L;
        var scheduler = new ManualScheduler();
        var throwingScheduler = scenario == "scheduler-failure";
        var job = new FakeJob
        {
            Items = ["a"],
            Processor = new FakeProcessor
            {
                ProcessThrows = scenario == "worker-fault" ? new InvalidOperationException("worker") : null,
            },
            MaterializeThrows = scenario == "materialize-failure" ? new TimeoutException("no response") : null,
            PublishThrows = scenario == "publish-failure" ? new InvalidOperationException("publish") : null,
            DuringMaterialize = scenario == "stale" ? () => epoch = 1L : null,
        };
        var coordinator = new LibraryWorkCoordinator<string, string>(
            () => epoch,
            isFrameworkThread: () => true,
            throwingScheduler ? (_, _) => throw new InvalidOperationException("scheduler") : scheduler.Schedule,
            logInfo: lines.Add);

        coordinator.Start(job);
        if (scenario == "cancelled")
            coordinator.RequestCancellation();
        coordinator.Update();
        scheduler.RunToCompletion();
        coordinator.Update();

        Assert.Contains(lines, l => l.Contains("settled"));
        Assert.Equal(LibraryWorkPhase.Idle, coordinator.State.Phase);
    }

    [Fact]
    public void Checkpoints_FailureLogsExceptionType_ButLastErrorStaysUserFacing()
    {
        var lines = new List<string>();
        var (coordinator, _, _) = NewCoordinator(logInfo: lines.Add);
        var job = new FakeJob
        {
            Items = [],
            Processor = new FakeProcessor(),
            MaterializeThrows = new TimeoutException("Penumbra did not respond."),
        };

        coordinator.Start(job);
        coordinator.Update();

        Assert.Contains(lines, l => l.Contains("TimeoutException"));
        // The user sees the message, not the type name.
        Assert.Equal("Penumbra did not respond.", coordinator.State.LastError);
    }

    [Fact]
    public void ThrowingLogger_DoesNotStrandTheRun()
    {
        var scheduler = new ManualScheduler();
        var job = new FakeJob { Items = ["a"], Processor = new FakeProcessor() };
        var coordinator = new LibraryWorkCoordinator<string, string>(
            () => 0L,
            isFrameworkThread: () => true,
            scheduler.Schedule,
            logWarning: _ => throw new InvalidOperationException("logger is broken"),
            logInfo: _ => throw new InvalidOperationException("logger is broken"));

        coordinator.Start(job);
        coordinator.Update();
        scheduler.RunToCompletion();
        coordinator.Update();

        // A diagnostic delegate must never be able to gate the plugin.
        Assert.Equal(LibraryWorkPhase.Idle, coordinator.State.Phase);
        Assert.Equal(LibraryWorkOutcome.Completed, coordinator.State.LastOutcome);
        Assert.Single(job.Published);
    }
}
