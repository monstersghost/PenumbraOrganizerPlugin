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
}
