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

        public Task<IReadOnlyList<string>> Schedule(Func<IReadOnlyList<string>> work, CancellationToken ct)
        {
            _work = work;
            _ct = ct;
            _tcs = new TaskCompletionSource<IReadOnlyList<string>>();
            return _tcs.Task;
        }

        public void RunToCompletion()
        {
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

        public string DisplayName => "Fake";
        public List<IReadOnlyList<string>> Published { get; } = [];

        public LibraryWorkBatch<string, string> Materialize()
        {
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
        NewCoordinator(Func<long>? epoch = null)
    {
        var scheduler = new ManualScheduler();
        var readEpoch = epoch ?? (() => 0L);
        var coordinator = new LibraryWorkCoordinator<string, string>(readEpoch, scheduler.Schedule);
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
    public void Start_MovesToComputing_WithoutRunningTheProcessor()
    {
        var processor = new FakeProcessor();
        var job = new FakeJob { Items = ["a", "b"], Processor = processor };
        var (coordinator, _, _) = NewCoordinator();

        coordinator.Start(job);

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
        var coordinator = new LibraryWorkCoordinator<string, string>(() => epoch, scheduler.Schedule);

        coordinator.Start(job);
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
        var (coordinator, _, _) = NewCoordinator();

        coordinator.Start(job);

        Assert.Equal(LibraryWorkPhase.Idle, coordinator.State.Phase);
        Assert.Equal(LibraryWorkOutcome.Failed, coordinator.State.LastOutcome);
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
        scheduler.RunToCompletion();
        coordinator.Update();

        var second = new FakeJob { Items = ["b"], Processor = new FakeProcessor() };
        coordinator.Start(second);

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
            () => 0L, (_, _) => throw new InvalidOperationException("no thread available"));

        coordinator.Start(job);

        Assert.Equal(LibraryWorkPhase.Idle, coordinator.State.Phase);
        Assert.Equal(LibraryWorkOutcome.Failed, coordinator.State.LastOutcome);
        Assert.Equal("no thread available", coordinator.State.LastError);
    }

    [Fact]
    public void SchedulerReturningNull_FailsInsteadOfWedging()
    {
        var job = new FakeJob { Items = ["a"], Processor = new FakeProcessor() };
        var coordinator = new LibraryWorkCoordinator<string, string>(() => 0L, (_, _) => null!);

        coordinator.Start(job);

        Assert.Equal(LibraryWorkPhase.Idle, coordinator.State.Phase);
        Assert.Equal(LibraryWorkOutcome.Failed, coordinator.State.LastOutcome);
    }

    [Fact]
    public void AfterSchedulerFailure_StartIsAllowedAgain()
    {
        var thrown = true;
        var scheduler = new ManualScheduler();
        var coordinator = new LibraryWorkCoordinator<string, string>(
            () => 0L,
            (work, ct) => thrown ? throw new InvalidOperationException("boom") : scheduler.Schedule(work, ct));

        coordinator.Start(new FakeJob { Items = ["a"], Processor = new FakeProcessor() });
        Assert.Equal(LibraryWorkOutcome.Failed, coordinator.State.LastOutcome);

        thrown = false;
        coordinator.Start(new FakeJob { Items = ["b"], Processor = new FakeProcessor() });

        Assert.Equal(LibraryWorkPhase.Computing, coordinator.State.Phase);
    }

    [Fact]
    public void EmptyBatch_PublishesAnEmptyResultAndCompletes()
    {
        var job = new FakeJob { Items = [], Processor = new FakeProcessor() };
        var (coordinator, scheduler, _) = NewCoordinator();

        coordinator.Start(job);
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
            () => 0L, scheduler.Schedule, disposeWait: TimeSpan.Zero);
        coordinator.Start(job);

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
            () => 0L, new ManualScheduler().Schedule, disposeWait: TimeSpan.Zero);
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
            () => 0L, scheduler.Schedule, disposeWait: TimeSpan.Zero);
        coordinator.Start(job);
        scheduler.RunToCompletion();

        coordinator.Dispose();
        coordinator.Update();

        Assert.Empty(job.Published);
    }

    [Fact]
    public void Dispose_WhenTheRunDoesNotStop_LogsATeardownWarning()
    {
        var warnings = new List<string>();
        var scheduler = new ManualScheduler();
        var coordinator = new LibraryWorkCoordinator<string, string>(
            () => 0L, scheduler.Schedule,
            logWarning: warnings.Add, disposeWait: TimeSpan.FromMilliseconds(50));
        coordinator.Start(new FakeJob { Items = ["a"], Processor = new FakeProcessor() });

        coordinator.Dispose(); // the manual scheduler's task never completes

        Assert.Single(warnings);
        Assert.Contains("teardown", warnings[0], StringComparison.OrdinalIgnoreCase);
    }
}
