# Non-Blocking Library Work Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move the Scan tab's mod walk and the Search tab's index build off the game's render thread, so neither can freeze FFXIV on a large or slow-disk mod library.

**Architecture:** A shared `LibraryWorkCoordinator<TSeed, TResult>` runs both jobs in three phases: a single framework-thread frame that copies plain strings out of the Penumbra IPC adapters, a background `Task` that does all classification and disk I/O against those strings, and a framework-thread publish that hands a fully-materialized result list to `OrganizerState.LoadScan` or `LibraryIndex`. Phase-2 code lives in a `LibraryWork.Pure` namespace that an architecture test forbids from referencing Dalamud or Penumbra types.

**Tech Stack:** C# / .NET 10 (`net10.0-windows7.0`), Dalamud.NET.Sdk 15.0.0, Penumbra.Api 5.15.1, xunit 2.5.3.

**Spec:** `docs/superpowers/specs/2026-07-29-non-blocking-library-work-design.md`

## Global Constraints

- Target framework `net10.0-windows7.0`; `ImplicitUsings` and `Nullable` are both enabled in both projects. Do not add `using System;`-style directives that implicit usings already cover.
- Test framework is xunit 2.5.3 with `<Using Include="Xunit" />` in the test csproj, so `[Fact]` and `Assert` need no `using`. Test namespaces mirror folder structure (`PenumbraOrganizer.Plugin.Tests.<Folder>`).
- Temp-directory test convention, copied from `HeliosphereDetectorTests`: `new DirectoryInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()))`, cleaned up in a `finally` with `Delete(recursive: true)`.
- `OperationController.cs` must not be modified by any task in this plan. Coordination with it is read-only, through its published `State` snapshot.
- No type in namespace `PenumbraOrganizer.Plugin.LibraryWork.Pure` may reference a type from the `Dalamud` or `Penumbra.Api` assemblies. Task 6 enforces this.
- Build and test command used throughout: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --nologo`. Verified working; a filtered run of `HeliosphereDetectorTests` passes 3 tests in ~200 ms.
- Work happens on branch `feat/non-blocking-library-work`, which already exists and already contains the spec commit.

---

## Task 1: Thread-safe event log buffer

The Penumbra `ModAdded`/`ModDeleted`/`ModMoved` subscribers call `MainWindow.LogEvent` from whatever thread Penumbra raises the event on, while `Draw()` enumerates the same `List<string>` on the render thread. Extract the buffer into its own testable type that separates the thread-safe write side from the framework-thread read side.

**Files:**
- Create: `PenumbraOrganizer.Plugin/Windows/EventLogBuffer.cs`
- Create: `PenumbraOrganizer.Plugin.Tests/Windows/EventLogBufferTests.cs`
- Modify: `PenumbraOrganizer.Plugin/Windows/MainWindow.cs:22` (field), `:98-103` (`LogEvent`), `:391` (`Draw` enumeration), `:1809` (diagnostic dump enumeration)
- Modify: `PenumbraOrganizer.Plugin/Plugin.cs:126-140` (`OnFrameworkUpdate`)

**Interfaces:**
- Consumes: nothing.
- Produces: `PenumbraOrganizer.Plugin.Windows.EventLogBuffer` with `void Add(string line)` (any thread), `void Drain()` (framework thread only), `IReadOnlyList<string> Lines { get; }` (framework thread only), and `const int MaxLines = 200`.

- [ ] **Step 1: Write the failing test**

Create `PenumbraOrganizer.Plugin.Tests/Windows/EventLogBufferTests.cs`:

```csharp
using PenumbraOrganizer.Plugin.Windows;

namespace PenumbraOrganizer.Plugin.Tests.Windows;

public class EventLogBufferTests
{
    [Fact]
    public void Add_IsNotVisibleUntilDrain()
    {
        var buffer = new EventLogBuffer();

        buffer.Add("first");

        Assert.Empty(buffer.Lines);

        buffer.Drain();

        Assert.Equal(["first"], buffer.Lines);
    }

    [Fact]
    public void Drain_PutsMostRecentFirst()
    {
        var buffer = new EventLogBuffer();

        buffer.Add("older");
        buffer.Add("newer");
        buffer.Drain();

        Assert.Equal(["newer", "older"], buffer.Lines);
    }

    [Fact]
    public void Drain_TrimsToMaxLines()
    {
        var buffer = new EventLogBuffer();

        for (var i = 0; i < EventLogBuffer.MaxLines + 50; i++)
            buffer.Add($"line {i}");
        buffer.Drain();

        Assert.Equal(EventLogBuffer.MaxLines, buffer.Lines.Count);
        // Newest first, so the very last line added must be at index 0.
        Assert.Equal($"line {EventLogBuffer.MaxLines + 49}", buffer.Lines[0]);
    }

    [Fact]
    public void ConcurrentAdds_AreAllDelivered_AndDrainDoesNotThrow()
    {
        var buffer = new EventLogBuffer();
        const int threads = 8;
        const int perThread = 500;

        Parallel.For(0, threads, t =>
        {
            for (var i = 0; i < perThread; i++)
                buffer.Add($"{t}-{i}");
        });
        buffer.Drain();

        // MaxLines trimming means we cannot assert on all of them, only that the
        // buffer survived concurrent writes and produced a full, well-formed window.
        Assert.Equal(EventLogBuffer.MaxLines, buffer.Lines.Count);
        Assert.All(buffer.Lines, line => Assert.Contains('-', line));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~EventLogBufferTests" --nologo`

Expected: FAIL to compile, with `CS0246: The type or namespace name 'EventLogBuffer' could not be found`.

- [ ] **Step 3: Write the implementation**

Create `PenumbraOrganizer.Plugin/Windows/EventLogBuffer.cs`:

```csharp
using System.Collections.Concurrent;

namespace PenumbraOrganizer.Plugin.Windows;

/// <summary>
/// Splits the live-event log into a lock-free write side callable from any thread (Penumbra raises
/// ModAdded/ModDeleted/ModMoved on threads it does not document) and a read side touched only by the
/// framework thread. Before this existed, a plain List was Insert(0, ...)-ed from Penumbra's
/// callbacks while Draw() enumerated it, which is an InvalidOperationException at best and a torn
/// backing array at worst. Drain() is called once per framework update; Lines is safe to enumerate
/// for the rest of that frame because nothing else ever touches it.
/// </summary>
public sealed class EventLogBuffer
{
    public const int MaxLines = 200;

    private readonly ConcurrentQueue<string> _incoming = new();
    private readonly List<string> _lines = [];

    /// <summary>Callable from any thread.</summary>
    public void Add(string line) => _incoming.Enqueue(line);

    /// <summary>Framework thread only.</summary>
    public IReadOnlyList<string> Lines => _lines;

    /// <summary>Framework thread only. Moves queued lines into <see cref="Lines"/>, newest first.</summary>
    public void Drain()
    {
        while (_incoming.TryDequeue(out var line))
            _lines.Insert(0, line);

        if (_lines.Count > MaxLines)
            _lines.RemoveRange(MaxLines, _lines.Count - MaxLines);
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~EventLogBufferTests" --nologo`

Expected: PASS, `Failed: 0, Passed: 4`.

- [ ] **Step 5: Wire it into MainWindow**

In `PenumbraOrganizer.Plugin/Windows/MainWindow.cs`, replace the field at line 22:

```csharp
    private readonly List<string> _eventLog = [];
```

with:

```csharp
    private readonly EventLogBuffer _eventLog = new();
```

Delete the `MaxEventLogLines` constant (its value now lives on `EventLogBuffer.MaxLines`; search the file for `MaxEventLogLines` and remove the declaration).

Replace `LogEvent` at lines 98-103:

```csharp
    // Called from Penumbra's IPC subscribers, which may be on any thread. The timestamp is taken
    // here rather than at drain time so ordering reflects when the event actually fired.
    internal void LogEvent(string message) =>
        _eventLog.Add($"{DateTime.Now:HH:mm:ss} {message}");

    // Framework thread only, called once per update from Plugin.OnFrameworkUpdate.
    internal void DrainEventLog() => _eventLog.Drain();
```

Change the `Draw` enumeration at line 391 from `foreach (var line in _eventLog)` to:

```csharp
                foreach (var line in _eventLog.Lines)
```

Change the diagnostic-dump enumeration at line 1809 the same way:

```csharp
        foreach (var line in _eventLog.Lines)
```

- [ ] **Step 6: Call the drain from the framework update**

In `PenumbraOrganizer.Plugin/Plugin.cs`, add to `OnFrameworkUpdate` (line 126), as the first statement in the method body:

```csharp
        _mainWindow.DrainEventLog();
```

- [ ] **Step 7: Build and run the full suite**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --nologo`

Expected: PASS, with no new failures relative to the pre-task baseline. Confirm there is no `CS0103` for `MaxEventLogLines` and no `CS1061` for `_eventLog`.

- [ ] **Step 8: Commit**

```bash
git add PenumbraOrganizer.Plugin/Windows/EventLogBuffer.cs PenumbraOrganizer.Plugin.Tests/Windows/EventLogBufferTests.cs PenumbraOrganizer.Plugin/Windows/MainWindow.cs PenumbraOrganizer.Plugin/Plugin.cs
git commit -m "fix: make the live event log safe across threads

Penumbra raises ModAdded/ModDeleted/ModMoved on undocumented threads, and
LogEvent was writing into the same List the draw thread enumerated."
```

---

## Task 2: Mod-event epoch

A monotonic counter bumped by the same Penumbra subscribers, so a background run can tell whether the mod list moved under it. Lock-free because the write side is the same undocumented-thread callback as Task 1.

**Files:**
- Create: `PenumbraOrganizer.Plugin/LibraryWork/ModEventEpoch.cs`
- Create: `PenumbraOrganizer.Plugin.Tests/LibraryWork/ModEventEpochTests.cs`
- Modify: `PenumbraOrganizer.Plugin/Plugin.cs:84-87` (the three subscribers)

**Interfaces:**
- Consumes: nothing.
- Produces: `PenumbraOrganizer.Plugin.LibraryWork.ModEventEpoch` with `void Increment()` (any thread), `long Current { get; }` (any thread). `Plugin` exposes `internal ModEventEpoch ModEvents { get; }`.

- [ ] **Step 1: Write the failing test**

Create `PenumbraOrganizer.Plugin.Tests/LibraryWork/ModEventEpochTests.cs`:

```csharp
using PenumbraOrganizer.Plugin.LibraryWork;

namespace PenumbraOrganizer.Plugin.Tests.LibraryWork;

public class ModEventEpochTests
{
    [Fact]
    public void Current_StartsAtZero()
    {
        Assert.Equal(0, new ModEventEpoch().Current);
    }

    [Fact]
    public void Increment_AdvancesCurrent()
    {
        var epoch = new ModEventEpoch();

        epoch.Increment();
        epoch.Increment();

        Assert.Equal(2, epoch.Current);
    }

    [Fact]
    public void ConcurrentIncrements_AreNotLost()
    {
        var epoch = new ModEventEpoch();
        const int threads = 8;
        const int perThread = 1000;

        Parallel.For(0, threads, _ =>
        {
            for (var i = 0; i < perThread; i++)
                epoch.Increment();
        });

        Assert.Equal(threads * perThread, epoch.Current);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~ModEventEpochTests" --nologo`

Expected: FAIL to compile, `CS0246: The type or namespace name 'ModEventEpoch' could not be found`.

- [ ] **Step 3: Write the implementation**

Create `PenumbraOrganizer.Plugin/LibraryWork/ModEventEpoch.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.LibraryWork;

/// <summary>
/// Counts observed Penumbra mod-list changes. A background library run snapshots this before it
/// starts and compares it before publishing: any difference means the run was built against a mod
/// list that no longer exists, so the result is discarded rather than published. Interlocked rather
/// than locked because the write side is a Penumbra IPC callback on an undocumented thread and must
/// never block it.
/// </summary>
public sealed class ModEventEpoch
{
    private long _value;

    /// <summary>Callable from any thread.</summary>
    public void Increment() => Interlocked.Increment(ref _value);

    /// <summary>Callable from any thread.</summary>
    public long Current => Interlocked.Read(ref _value);
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~ModEventEpochTests" --nologo`

Expected: PASS, `Failed: 0, Passed: 3`.

- [ ] **Step 5: Wire it into the Penumbra subscribers**

In `PenumbraOrganizer.Plugin/Plugin.cs`, add a field near the other readonly fields (next to `_npcNameRefreshService`, around line 45):

```csharp
    internal ModEventEpoch ModEvents { get; } = new();
```

Add `using PenumbraOrganizer.Plugin.LibraryWork;` to the file's using block if the namespace is not already imported.

Replace the three subscriber registrations at lines 84-87:

```csharp
        _modAdded = ModAdded.Subscriber(PluginInterface, dir =>
        {
            ModEvents.Increment();
            _mainWindow.LogEvent($"Mod added: {dir}");
        });
        _modDeleted = ModDeleted.Subscriber(PluginInterface, dir =>
        {
            ModEvents.Increment();
            _mainWindow.LogEvent($"Mod deleted: {dir}");
        });
        _modMoved = ModMoved.Subscriber(PluginInterface, (oldDir, newDir) =>
        {
            ModEvents.Increment();
            _mainWindow.LogEvent($"Mod moved: {oldDir} -> {newDir}");
        });
```

- [ ] **Step 6: Build and run the full suite**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --nologo`

Expected: PASS, no new failures.

- [ ] **Step 7: Commit**

```bash
git add PenumbraOrganizer.Plugin/LibraryWork/ModEventEpoch.cs PenumbraOrganizer.Plugin.Tests/LibraryWork/ModEventEpochTests.cs PenumbraOrganizer.Plugin/Plugin.cs
git commit -m "feat: count Penumbra mod-list changes for staleness detection"
```

---

## Task 3: Coordinator contracts and happy path

**Files:**
- Create: `PenumbraOrganizer.Plugin/LibraryWork/LibraryWorkContracts.cs`
- Create: `PenumbraOrganizer.Plugin/LibraryWork/LibraryWorkCoordinator.cs`
- Create: `PenumbraOrganizer.Plugin.Tests/LibraryWork/LibraryWorkCoordinatorTests.cs`

**Interfaces:**
- Consumes: `ModEventEpoch` from Task 2, read through a `Func<long>` rather than referenced directly.
- Produces: `LibraryWorkPhase`, `LibraryWorkOutcome`, `LibraryWorkStateSnapshot`, `ILibraryWorkJob<TSeed, TResult>`, `ILibraryWorkProcessor<TSeed, TResult>`, `LibraryWorkBatch<TSeed, TResult>`, and `LibraryWorkCoordinator<TSeed, TResult>` with `State`, `Start(job)`, `Update()`, `RequestCancellation()`, `Dispose()`.

- [ ] **Step 1: Write the contracts**

Create `PenumbraOrganizer.Plugin/LibraryWork/LibraryWorkContracts.cs`:

```csharp
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
```

- [ ] **Step 2: Write the failing happy-path test**

Create `PenumbraOrganizer.Plugin.Tests/LibraryWork/LibraryWorkCoordinatorTests.cs`. This file grows in Task 4; write it now with the shared fakes plus the happy-path facts.

```csharp
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
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~LibraryWorkCoordinatorTests" --nologo`

Expected: FAIL to compile, `CS0246: The type or namespace name 'LibraryWorkCoordinator<,>' could not be found`.

- [ ] **Step 4: Write the coordinator**

Create `PenumbraOrganizer.Plugin/LibraryWork/LibraryWorkCoordinator.cs`:

```csharp
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

    private static readonly TimeSpan DisposeWait = TimeSpan.FromSeconds(2);

    private readonly Func<long> _readEpoch;
    private readonly BackgroundScheduler _scheduler;
    private readonly Action<string>? _logWarning;

    private ILibraryWorkJob<TSeed, TResult>? _job;
    private CancellationTokenSource? _cts;
    private Task<IReadOnlyList<TResult>>? _task;
    private long _startEpoch;
    private int _processed;
    private int _total;

    public LibraryWorkStateSnapshot State { get; private set; } = LibraryWorkStateSnapshot.Idle;

    public LibraryWorkCoordinator(
        Func<long> readEpoch, BackgroundScheduler? scheduler = null, Action<string>? logWarning = null)
    {
        _readEpoch = readEpoch;
        _scheduler = scheduler ?? ((work, ct) => Task.Run(work, ct));
        _logWarning = logWarning;
    }

    public void Start(ILibraryWorkJob<TSeed, TResult> job)
    {
        if (State.IsRunning)
            throw new InvalidOperationException($"{State.JobDisplayName} is already running.");

        _job = job;
        _processed = 0;
        _total = 0;
        _startEpoch = _readEpoch();
        PublishRunning(LibraryWorkPhase.Materializing);

        LibraryWorkBatch<TSeed, TResult> batch;
        try
        {
            batch = job.Materialize();
        }
        catch (Exception ex)
        {
            Settle(LibraryWorkOutcome.Failed, ex.Message);
            return;
        }

        _total = batch.Items.Count;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        PublishRunning(LibraryWorkPhase.Computing);
        _task = _scheduler(() => RunBatch(batch, ct), ct);
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

    public void Dispose()
    {
        _cts?.Cancel();
        try
        {
            // Bounded, not indefinite: Dalamud unloads our AssemblyLoadContext on plugin unload, and
            // a background task still executing our code through that unload is a real crash risk.
            // Per-item work is one file read, so the token is observed quickly in practice.
            if (_task is { } task && !task.Wait(DisposeWait))
                _logWarning?.Invoke("A library work run did not stop within 2s of plugin shutdown.");
        }
        catch (AggregateException)
        {
            // The run's own cancellation or failure. Teardown does not care why it ended.
        }

        _cts?.Dispose();
        _cts = null;
        _task = null;
        _job = null;
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
        State = new LibraryWorkStateSnapshot(
            LibraryWorkPhase.Idle, JobDisplayName: null,
            Volatile.Read(ref _processed), _total,
            LastOutcome: outcome, LastError: error, CanCancel: false);
    }
}
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~LibraryWorkCoordinatorTests" --nologo`

Expected: PASS, `Failed: 0, Passed: 6`.

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/LibraryWork/ PenumbraOrganizer.Plugin.Tests/LibraryWork/LibraryWorkCoordinatorTests.cs
git commit -m "feat: add the three-phase library work coordinator"
```

---

## Task 4: Coordinator cancellation, staleness, failure, and disposal

**Files:**
- Modify: `PenumbraOrganizer.Plugin.Tests/LibraryWork/LibraryWorkCoordinatorTests.cs` (append facts)

**Interfaces:**
- Consumes: everything Task 3 produced. No production code changes are expected; Task 3's coordinator already implements these paths. If a test fails, fix the coordinator, not the test.

- [ ] **Step 1: Append the failing tests**

Add these facts to `LibraryWorkCoordinatorTests` (inside the existing class, after the last fact):

```csharp
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
    public void Dispose_DuringARun_DoesNotPublish()
    {
        var job = new FakeJob { Items = ["a"], Processor = new FakeProcessor() };
        var (coordinator, scheduler, _) = NewCoordinator();
        coordinator.Start(job);

        coordinator.Dispose();
        scheduler.RunToCompletion();

        Assert.Empty(job.Published);
    }
```

- [ ] **Step 2: Run the tests**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~LibraryWorkCoordinatorTests" --nologo`

Expected: PASS, `Failed: 0, Passed: 15`.

If `Dispose_DuringARun_DoesNotPublish` hangs, the `Task.Wait(DisposeWait)` in `Dispose` is blocking on a `ManualScheduler` task that has not been run yet; that is the 2-second bounded wait doing exactly its job, and the test should complete after it expires. If any other test fails, fix `LibraryWorkCoordinator`, not the test.

- [ ] **Step 3: Commit**

```bash
git add PenumbraOrganizer.Plugin.Tests/LibraryWork/LibraryWorkCoordinatorTests.cs
git commit -m "test: cover coordinator cancel, staleness, failure, and disposal paths"
```

---

## Task 5: Scan seed and processor

**Files:**
- Create: `PenumbraOrganizer.Plugin/LibraryWork/Pure/ScanSeed.cs`
- Create: `PenumbraOrganizer.Plugin/LibraryWork/Pure/ScanProcessor.cs`
- Create: `PenumbraOrganizer.Plugin.Tests/LibraryWork/ScanProcessorTests.cs`

**Interfaces:**
- Consumes: `ILibraryWorkProcessor<TSeed, TResult>` from Task 3.
- Produces: `PenumbraOrganizer.Plugin.LibraryWork.Pure.ScanSeed(string Identifier, string Name, string Author, string CurrentPath, string ModDirectoryPath, IReadOnlyList<string> ChangedItemKeys)` and `ScanProcessor : ILibraryWorkProcessor<ScanSeed, OrganizerModRow>` with constructor `(string npcNameListPath, string npcNameSeedJson)` and `IReadOnlyList<string> Warnings { get; }`.

- [ ] **Step 1: Write the seed type**

Create `PenumbraOrganizer.Plugin/LibraryWork/Pure/ScanSeed.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.LibraryWork.Pure;

/// <summary>
/// Everything the background phase needs about one mod, as plain strings copied off the Penumbra
/// adapter on the framework thread. The mod directory is a string rather than the DirectoryInfo the
/// adapter hands out: that severs object identity with adapter-owned state, so a stale adapter can
/// never be reached through a seed even by accident.
///
/// ChangedItemKeys holds references to strings Penumbra already allocated, so materializing them
/// copies 8 bytes each, not character data.
/// </summary>
public sealed record ScanSeed(
    string Identifier,
    string Name,
    string Author,
    string CurrentPath,
    string ModDirectoryPath,
    IReadOnlyList<string> ChangedItemKeys);
```

- [ ] **Step 2: Write the failing test**

Create `PenumbraOrganizer.Plugin.Tests/LibraryWork/ScanProcessorTests.cs`:

```csharp
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.LibraryWork.Pure;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Tests.LibraryWork;

public class ScanProcessorTests
{
    private const string SeedJson =
        """{"Version":1,"NPCs":["Zenos"],"Enemies":[],"Bosses":[],"Excluded":[]}""";

    private static ScanProcessor NewProcessor(string? npcListPath = null)
    {
        var processor = new ScanProcessor(
            npcListPath ?? Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "npc-name-list.json"),
            SeedJson);
        processor.Prepare(CancellationToken.None);
        return processor;
    }

    private static ScanSeed Seed(string modDirectoryPath, string name = "Some Mod", params string[] changedItemKeys) =>
        new("mod-dir", name, "An Author", "Gear/Some Mod", modDirectoryPath, changedItemKeys);

    [Fact]
    public void GearModWithOneSlot_GetsThatSubCategoryAndSingleDiagnostic()
    {
        var dir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        dir.Create();
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "meta.json"),
                """{"DefaultData":{"Files":{"chara/equipment/e0001/model/c0101e0001_top.mdl":"x.mdl"}}}""");

            var row = NewProcessor().Process(Seed(dir.FullName, changedItemKeys: "Ala Mhigan Gown"), CancellationToken.None);

            Assert.NotNull(row);
            Assert.Equal(ModCategory.Gear, row!.Category);
            Assert.Equal(GearSlotDiagnostic.Single, row.GearSlotDiagnostic);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void GearModWithNoEquipmentEvidence_ReportsZeroEvidence()
    {
        var dir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        dir.Create();
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "meta.json"), """{"DefaultData":{"Files":{}}}""");

            var row = NewProcessor().Process(Seed(dir.FullName, changedItemKeys: "Ala Mhigan Gown"), CancellationToken.None);

            Assert.Equal(GearSlotDiagnostic.ZeroEvidence, row!.GearSlotDiagnostic);
            Assert.Null(row.SubCategory);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void GearModWithMissingDirectory_ReportsDirectoryMissing()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

        var row = NewProcessor().Process(Seed(missing, changedItemKeys: "Ala Mhigan Gown"), CancellationToken.None);

        Assert.Equal(GearSlotDiagnostic.DirectoryMissing, row!.GearSlotDiagnostic);
    }

    [Fact]
    public void NonGearMod_NeverTouchesDiskAndReportsNotApplicable()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

        var row = NewProcessor().Process(Seed(missing, name: "Zenos Glam"), CancellationToken.None);

        Assert.Equal(GearSlotDiagnostic.NotApplicable, row!.GearSlotDiagnostic);
    }

    [Fact]
    public void NpcNameHeuristic_IsApplied()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

        var row = NewProcessor().Process(Seed(missing, name: "Zenos Redesign"), CancellationToken.None);

        Assert.Equal(ModCategory.NPC, row!.Category);
    }

    [Fact]
    public void HeliospherePrefix_IsDetectedFromTheIdentifier()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var seed = new ScanSeed("hs-Nightingale-1.0", "Nightingale", "Author", "Gear/N", missing, []);

        var row = NewProcessor().Process(seed, CancellationToken.None);

        Assert.True(row!.HeliosphereManaged);
    }

    [Fact]
    public void RowCarriesIdentityFieldsThroughUnchanged()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

        var row = NewProcessor().Process(Seed(missing), CancellationToken.None);

        Assert.Equal("mod-dir", row!.Identifier);
        Assert.Equal("Some Mod", row.Name);
        Assert.Equal("An Author", row.Author);
        Assert.Equal("Gear/Some Mod", row.CurrentPath);
        Assert.Equal("Gear/Some Mod", row.ProposedPath);
    }

    [Fact]
    public void Cancellation_IsObservedPerItem()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            NewProcessor().Process(Seed(Path.GetTempPath()), cts.Token));
    }
}
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~ScanProcessorTests" --nologo`

Expected: FAIL to compile, `CS0246: The type or namespace name 'ScanProcessor' could not be found`.

- [ ] **Step 4: Write the processor**

Create `PenumbraOrganizer.Plugin/LibraryWork/Pure/ScanProcessor.cs`:

```csharp
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Classification;
using PenumbraOrganizer.Plugin.Organizer.NpcNames;

namespace PenumbraOrganizer.Plugin.LibraryWork.Pure;

/// <summary>
/// The whole of the scan's per-mod work: classification, NPC name matching, and the gear-slot and
/// Heliosphere disk probes. Lifted verbatim from the old synchronous Plugin.RunScan body, with the
/// Penumbra adapter reads left behind on the framework thread in ScanJob.
///
/// May not reference Dalamud or Penumbra types - LibraryWorkPurityTests enforces this. Warnings are
/// collected rather than logged so the framework thread can log them at publish time instead of this
/// class reaching for IPluginLog off-thread.
/// </summary>
public sealed class ScanProcessor : ILibraryWorkProcessor<ScanSeed, OrganizerModRow>
{
    private readonly string _npcNameListPath;
    private readonly string _npcNameSeedJson;
    private readonly List<string> _warnings = [];

    private NpcNameMatcher _npcNameMatcher = NpcNameMatcher.Empty;

    public ScanProcessor(string npcNameListPath, string npcNameSeedJson)
    {
        _npcNameListPath = npcNameListPath;
        _npcNameSeedJson = npcNameSeedJson;
    }

    /// <summary> Framework thread reads this after the run completes. </summary>
    public IReadOnlyList<string> Warnings => _warnings;

    public void Prepare(CancellationToken ct)
    {
        var result = NpcNameListStore.Load(_npcNameListPath, _npcNameSeedJson);
        if (result.Warning is not null)
            _warnings.Add(result.Warning);
        _npcNameMatcher = NpcNameListStore.BuildMatcher(result.Document);
    }

    public OrganizerModRow? Process(ScanSeed item, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var modPath = new DirectoryInfo(item.ModDirectoryPath);
        var classification = ModTypeClassifier.Classify(item.Name, item.ChangedItemKeys, _npcNameMatcher);

        // Disk I/O only for mods the changed-items rule already confirmed are Gear - every other
        // category never touches disk for this.
        var gearSlotDiagnostic = GearSlotDiagnostic.NotApplicable;
        if (classification.Category == ModCategory.Gear)
        {
            var equipmentSlots = ModEquipmentFileReader.ReadEquipmentSlots(modPath);
            classification = ModTypeClassifier.EnrichGearSubCategory(classification, equipmentSlots);

            gearSlotDiagnostic = equipmentSlots switch
            {
                null => GearSlotDiagnostic.ReadFailure,
                { Count: 0 } when !modPath.Exists => GearSlotDiagnostic.DirectoryMissing,
                { Count: 0 } => GearSlotDiagnostic.ZeroEvidence,
                { Count: 1 } => GearSlotDiagnostic.Single,
                _ => GearSlotDiagnostic.Ambiguous,
            };
        }

        return new OrganizerModRow
        {
            Identifier = item.Identifier,
            Name = item.Name,
            Author = item.Author,
            CurrentPath = item.CurrentPath,
            ProposedPath = item.CurrentPath,
            HeliosphereManaged = HeliosphereDetector.IsHeliosphereManaged(item.Identifier, modPath),
            Category = classification.Category,
            SubCategory = classification.SubCategory,
            GearSlotDiagnostic = gearSlotDiagnostic,
        };
    }
}
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~ScanProcessorTests" --nologo`

Expected: PASS, `Failed: 0, Passed: 8`.

If `NpcNameHeuristic_IsApplied` fails, check that `NpcNameListStore.Load` wrote the seed to the temp path successfully; the seed JSON above must satisfy `NpcNameListCodec.Parse`, so compare it against `PenumbraOrganizer.Plugin/Organizer/NpcNames/npc-name-list-seed.json` and match that file's exact shape.

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/LibraryWork/Pure/ PenumbraOrganizer.Plugin.Tests/LibraryWork/ScanProcessorTests.cs
git commit -m "feat: add the pure scan processor"
```

---

## Task 6: Purity architecture test

**Files:**
- Create: `PenumbraOrganizer.Plugin.Tests/LibraryWork/LibraryWorkPurityTests.cs`

**Interfaces:**
- Consumes: `PenumbraOrganizer.Plugin.LibraryWork.Pure.ScanProcessor` from Task 5, used only as an assembly anchor.
- Produces: nothing consumed by later tasks. Guards Task 5 and Task 8.

- [ ] **Step 1: Write the test**

Create `PenumbraOrganizer.Plugin.Tests/LibraryWork/LibraryWorkPurityTests.cs`:

```csharp
using System.Reflection;
using PenumbraOrganizer.Plugin.LibraryWork.Pure;

namespace PenumbraOrganizer.Plugin.Tests.LibraryWork;

/// <summary>
/// The background phase runs off the framework thread, where touching Dalamud or Penumbra is
/// undefined behaviour at best. This pins that rule structurally instead of leaving it as a comment
/// somebody edits past later.
///
/// Checks type signatures (fields, properties, constructor and method parameters, return types),
/// not method bodies - catching a static call buried in a body needs IL inspection, which is
/// disproportionate here because every helper the phase calls was already free of both assemblies
/// before this work started. What this does catch is the realistic regression: someone adding an
/// adapter or IDalamudPluginInterface as a field or parameter.
/// </summary>
public class LibraryWorkPurityTests
{
    private const string PureNamespace = "PenumbraOrganizer.Plugin.LibraryWork.Pure";

    private static readonly string[] ForbiddenAssemblies = ["Dalamud", "Penumbra.Api"];

    [Fact]
    public void PureTypes_DoNotReferenceDalamudOrPenumbraInTheirSignatures()
    {
        var pureTypes = typeof(ScanProcessor).Assembly.GetTypes()
            .Where(t => t.Namespace is { } ns
                && (ns == PureNamespace || ns.StartsWith(PureNamespace + ".", StringComparison.Ordinal)))
            .ToList();

        // Guards against the check silently passing because the namespace was renamed or emptied.
        Assert.NotEmpty(pureTypes);

        var violations = pureTypes
            .SelectMany(type => SignatureTypes(type).Select(referenced => (type, referenced)))
            .Where(pair => IsForbidden(pair.referenced))
            .Select(pair => $"{pair.type.FullName} references {pair.referenced.FullName} "
                + $"from {pair.referenced.Assembly.GetName().Name}")
            .Distinct()
            .ToList();

        Assert.Empty(violations);
    }

    private static bool IsForbidden(Type type)
    {
        var assemblyName = type.Assembly.GetName().Name;
        return assemblyName is not null && ForbiddenAssemblies.Contains(assemblyName);
    }

    private static IEnumerable<Type> SignatureTypes(Type type)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var field in type.GetFields(all))
            foreach (var t in Expand(field.FieldType))
                yield return t;

        foreach (var property in type.GetProperties(all))
            foreach (var t in Expand(property.PropertyType))
                yield return t;

        foreach (var constructor in type.GetConstructors(all))
            foreach (var parameter in constructor.GetParameters())
                foreach (var t in Expand(parameter.ParameterType))
                    yield return t;

        foreach (var method in type.GetMethods(all))
        {
            foreach (var t in Expand(method.ReturnType))
                yield return t;
            foreach (var parameter in method.GetParameters())
                foreach (var t in Expand(parameter.ParameterType))
                    yield return t;
        }
    }

    // Unwraps arrays, by-ref, and generic arguments so IReadOnlyList<SomeDalamudType> is caught.
    private static IEnumerable<Type> Expand(Type type)
    {
        if (type.HasElementType && type.GetElementType() is { } element)
        {
            foreach (var t in Expand(element))
                yield return t;
            yield break;
        }

        yield return type;

        if (!type.IsGenericType)
            yield break;

        foreach (var argument in type.GetGenericArguments())
            foreach (var t in Expand(argument))
                yield return t;
    }
}
```

- [ ] **Step 2: Run the test to verify it passes against the current, clean code**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~LibraryWorkPurityTests" --nologo`

Expected: PASS, `Failed: 0, Passed: 1`.

- [ ] **Step 3: Verify the test actually catches a violation**

A guard test that cannot fail is worthless. Temporarily add this field to `ScanProcessor`:

```csharp
    private Dalamud.Plugin.IDalamudPluginInterface? _deliberateViolation;
```

Add `using Dalamud.Plugin;` if needed. Re-run the command from Step 2.

Expected: FAIL, with the assertion message naming `ScanProcessor references Dalamud.Plugin.IDalamudPluginInterface from Dalamud`.

**Now remove that field again** and re-run to confirm PASS.

- [ ] **Step 4: Commit**

```bash
git add PenumbraOrganizer.Plugin.Tests/LibraryWork/LibraryWorkPurityTests.cs
git commit -m "test: forbid Dalamud and Penumbra types in the background work namespace"
```

---

## Task 7: ScanJob, rewiring, and dead code removal

**Files:**
- Create: `PenumbraOrganizer.Plugin/LibraryWork/ScanJob.cs`
- Modify: `PenumbraOrganizer.Plugin/Plugin.cs` — `RunScan` (`:142-202`), `OnFrameworkUpdate` (`:126`), `Dispose` (`:104-120`), field block (`~:38-45`); delete `ApplyChanges()` (`:373-443`) and `Restore(Guid)` (`:608-693`)
- Modify: `PenumbraOrganizer.Plugin/Windows/MainWindow.cs` — `RunScan` (`:1613-1629`), delete the unused `_lastApplyResults` field (`:35`)

**Interfaces:**
- Consumes: `LibraryWorkCoordinator<ScanSeed, OrganizerModRow>`, `ScanSeed`, `ScanProcessor`, `ModEventEpoch`.
- Produces: `Plugin.ScanWork` of type `LibraryWorkCoordinator<ScanSeed, OrganizerModRow>`; `Plugin.RunScan()` now starts a run instead of completing one; `MainWindow.OnScanPublished()`.

- [ ] **Step 1: Write ScanJob**

Create `PenumbraOrganizer.Plugin/LibraryWork/ScanJob.cs`:

```csharp
using Penumbra.Api.IpcSubscribers;
using PenumbraOrganizer.Plugin.LibraryWork.Pure;
using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.LibraryWork;

/// <summary>
/// Framework-thread half of a scan. Materialize() is the only place Penumbra's adapters are touched,
/// and it releases both before returning - previously the mod-list adapter (a synchronized list, per
/// Penumbra's own API docs) was held across the entire per-mod disk walk.
/// </summary>
public sealed class ScanJob : ILibraryWorkJob<ScanSeed, OrganizerModRow>
{
    private readonly Plugin _plugin;
    private readonly string _npcNameListPath;
    private readonly string _npcNameSeedJson;
    private ScanProcessor? _processor;

    public ScanJob(Plugin plugin, string npcNameListPath, string npcNameSeedJson)
    {
        _plugin = plugin;
        _npcNameListPath = npcNameListPath;
        _npcNameSeedJson = npcNameSeedJson;
    }

    public string DisplayName => "Scan";

    public LibraryWorkBatch<ScanSeed, OrganizerModRow> Materialize()
    {
        // One bulk call for all mods' changed items. If Penumbra is unavailable this throws, and the
        // coordinator turns it into a Failed outcome with the message intact.
        var allChangedItems = new GetChangedItemAdapterDictionary(Plugin.PluginInterface).Invoke();

        using var modList = _plugin.GetModListAdapterIpc.Invoke();

        var seeds = modList.Select(mod => new ScanSeed(
            Identifier: mod.Identifier,
            Name: mod.Name,
            Author: mod.Author,
            CurrentPath: mod.FullPath,
            ModDirectoryPath: mod.ModPath.FullName,
            ChangedItemKeys: allChangedItems.TryGetValue(mod.Identifier, out var changedItems)
                ? changedItems.Keys.ToList()
                : [])).ToList();

        _processor = new ScanProcessor(_npcNameListPath, _npcNameSeedJson);
        return new LibraryWorkBatch<ScanSeed, OrganizerModRow>(seeds, _processor);
    }

    public void Publish(IReadOnlyList<OrganizerModRow> results)
    {
        foreach (var warning in _processor?.Warnings ?? [])
            Plugin.Log.Warning(warning);

        _plugin.OrganizerState.LoadScan(results, _plugin.Config.ProtectedModIdentifiers, _plugin.Config.ProtectedFolderPaths);
        _plugin.SaveProtectionState();
        _plugin.OnScanPublished();
    }
}
```

- [ ] **Step 2: Expose what ScanJob needs from Plugin**

In `PenumbraOrganizer.Plugin/Plugin.cs`, change the accessibility of the members `ScanJob` reads. `Config` is currently `internal Configuration Config` (line 38) and `GetModListAdapterIpc` is `internal readonly` (line 32) — both are already reachable from the same assembly, so no change is needed there. Change `NpcNameListPath` (line 264) and `ReadEmbeddedNpcNameSeed` (line 266) from `private` to `internal`:

```csharp
    internal string NpcNameListPath => Path.Combine(PluginInterface.ConfigDirectory.FullName, "npc-name-list.json");

    internal static string ReadEmbeddedNpcNameSeed()
```

Add an internal hook that routes scan completion to the window, placed next to `ToggleMainUi` (line 124):

```csharp
    internal void OnScanPublished() => _mainWindow.OnScanPublished();
```

- [ ] **Step 3: Replace RunScan and add the coordinator**

In `PenumbraOrganizer.Plugin/Plugin.cs`, add a field next to `ModEvents` (added in Task 2):

```csharp
    internal LibraryWorkCoordinator<Pure.ScanSeed, Organizer.OrganizerModRow> ScanWork { get; }
```

Initialize it in the constructor, after `Config` is assigned (line 56) and before the subscribers are registered:

```csharp
        ScanWork = new LibraryWorkCoordinator<Pure.ScanSeed, Organizer.OrganizerModRow>(
            () => ModEvents.Current, logWarning: message => Log.Warning(message));
```

Replace the entire body of `RunScan()` (lines 142-202) with:

```csharp
    /// <summary>
    /// Starts a scan. Returns as soon as the Penumbra reads are done; classification and the
    /// per-mod disk walk run on a background thread and publish through ScanJob.Publish. Throws
    /// InvalidOperationException if a library run is already in flight.
    /// </summary>
    public void RunScan() => ScanWork.Start(new ScanJob(this, NpcNameListPath, ReadEmbeddedNpcNameSeed()));
```

Add `ScanWork.Update();` to `OnFrameworkUpdate` (line 126), after the `DrainEventLog` call added in Task 1:

```csharp
        ScanWork.Update();
```

Add disposal to `Dispose()` (line 104), before `WindowSystem.RemoveAllWindows()`:

```csharp
        ScanWork.Dispose();
```

Add `using PenumbraOrganizer.Plugin.LibraryWork;` and `using PenumbraOrganizer.Plugin.LibraryWork.Pure;` to the file's using block, or fully qualify as shown above.

- [ ] **Step 4: Rewire MainWindow**

In `PenumbraOrganizer.Plugin/Windows/MainWindow.cs`, replace `RunScan()` (lines 1613-1629):

```csharp
    // Starts the scan; completion lands in OnScanPublished on a later frame. The catch covers a
    // rejected start (another library run in flight); every failure inside the run itself is
    // reported through ScanWork.State.LastError instead.
    private void RunScan()
    {
        try
        {
            _plugin.RunScan();
            _lastError = null;
        }
        catch (Exception ex)
        {
            _lastError = $"Could not start scan: {ex.Message}";
            Plugin.Log.Error(ex, "Scan could not be started.");
        }
    }

    // Framework thread, called by ScanJob.Publish once results are live in OrganizerState.
    internal void OnScanPublished()
    {
        _folderReloadRequired = false; // the banner's instruction is "Rediscover Mods, then Scan here"
        Plugin.Log.Information($"Scan completed: {_plugin.OrganizerState.Mods.Count} mods loaded.");
        RefreshOrphanedFolders();
    }
```

- [ ] **Step 5: Delete the dead Apply and Restore paths**

Delete `Plugin.ApplyChanges()` entirely (`Plugin.cs:373-443`) and `Plugin.Restore(Guid snapshotId)` entirely (`Plugin.cs:608-693`). Both have zero callers in production or tests; they were superseded by `StartApplyOperation`/`StartRestoreOperation` plus `OperationController`, and each contains a now-obsolete synchronous `RunScan()` call.

In `MainWindow.cs`, delete the `_lastApplyResults` field at line 35 — the compiler already reports it as `warning CS0649: Field 'MainWindow._lastApplyResults' is never assigned to`. If deleting it produces `CS0103` at any read site, delete those reads too; they can only be rendering a value that is permanently null.

Do **not** touch `MainWindow.ApplyChanges()` at line 1631 — that is MainWindow's own wrapper around `StartApplyOperation` and is live.

- [ ] **Step 6: Build and run the full suite**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --nologo`

Expected: PASS with no new failures, and `warning CS0649` for `_lastApplyResults` gone from the build output.

- [ ] **Step 7: Commit**

```bash
git add PenumbraOrganizer.Plugin/LibraryWork/ScanJob.cs PenumbraOrganizer.Plugin/Plugin.cs PenumbraOrganizer.Plugin/Windows/MainWindow.cs
git commit -m "feat: run the scan off the render thread

Also removes Plugin.ApplyChanges and Plugin.Restore, dead since the
operation controller took over, and the unused _lastApplyResults field."
```

---

## Task 8: Index seed, processor, job, and rewiring

**Files:**
- Create: `PenumbraOrganizer.Plugin/LibraryWork/Pure/IndexSeed.cs`
- Create: `PenumbraOrganizer.Plugin/LibraryWork/Pure/IndexProcessor.cs`
- Create: `PenumbraOrganizer.Plugin/LibraryWork/IndexJob.cs`
- Create: `PenumbraOrganizer.Plugin.Tests/LibraryWork/IndexProcessorTests.cs`
- Modify: `PenumbraOrganizer.Plugin/Plugin.cs` — `BuildChangedItemIndex` (`:204-239`), `OnFrameworkUpdate`, `Dispose`, field block
- Modify: `PenumbraOrganizer.Plugin/LibrarySearch/ChangedItemIndexBuilder.cs` — split the per-mod body out

**Interfaces:**
- Consumes: everything from Tasks 3, 5, 6.
- Produces: `Plugin.IndexWork` of type `LibraryWorkCoordinator<IndexSeed, IndexedMod>`.

- [ ] **Step 1: Read the existing builder before changing it**

Read `PenumbraOrganizer.Plugin/LibrarySearch/ChangedItemIndexBuilder.cs` and `ChangedItemIndex.cs` in full. `Build` currently does three things: per-mod work (lines ~20-54), an orphan count (lines ~57-59), and the assembly of the final `ChangedItemIndex`. Only the per-mod work moves; the orphan count and final assembly stay on the framework thread because they need the full changed-item identifier set, which `IndexJob.Materialize` already has.

- [ ] **Step 2: Write the seed type**

Create `PenumbraOrganizer.Plugin/LibraryWork/Pure/IndexSeed.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.LibraryWork.Pure;

/// <summary>
/// One mod's worth of index input, as plain strings copied off the Penumbra adapter on the framework
/// thread. Same rationale as ScanSeed: the mod directory is a string, not the adapter's DirectoryInfo.
/// </summary>
public sealed record IndexSeed(
    string Identifier,
    string Name,
    string Author,
    string ModDirectoryPath,
    IReadOnlyList<string> ChangedItemKeys);
```

- [ ] **Step 3: Write the failing processor test**

Create `PenumbraOrganizer.Plugin.Tests/LibraryWork/IndexProcessorTests.cs`:

```csharp
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.LibraryWork.Pure;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Tests.LibraryWork;

public class IndexProcessorTests
{
    private const string SeedJson =
        """{"Version":1,"NPCs":["Zenos"],"Enemies":[],"Bosses":[],"Excluded":[]}""";

    private static IndexProcessor NewProcessor()
    {
        var processor = new IndexProcessor(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "npc-name-list.json"), SeedJson);
        processor.Prepare(CancellationToken.None);
        return processor;
    }

    private static IndexSeed Seed(string name = "Some Mod", params string[] keys) =>
        new("mod-dir", name, "An Author", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()), keys);

    [Fact]
    public void ModWithNoChangedItems_IsExcluded()
    {
        Assert.Null(NewProcessor().Process(Seed(), CancellationToken.None));
    }

    [Fact]
    public void ModWithChangedItems_IsIncludedWithItsFacets()
    {
        var indexed = NewProcessor().Process(Seed("Some Mod", "Ala Mhigan Gown"), CancellationToken.None);

        Assert.NotNull(indexed);
        Assert.Equal("mod-dir", indexed!.Identifier);
        Assert.Contains(ModCategory.Gear, indexed.Categories);
    }

    [Fact]
    public void GearModWithMissingDirectory_ReportsDirectoryMissing()
    {
        var indexed = NewProcessor().Process(Seed("Some Mod", "Ala Mhigan Gown"), CancellationToken.None);

        Assert.Equal(GearSlotDiagnostic.DirectoryMissing, indexed!.SlotDiagnostic);
    }

    [Fact]
    public void NpcNameHeuristic_IsRecorded()
    {
        var indexed = NewProcessor().Process(Seed("Zenos Redesign", "Ala Mhigan Gown"), CancellationToken.None);

        Assert.True(indexed!.MatchedByNpcNameHeuristic);
    }

    [Fact]
    public void Cancellation_IsObservedPerItem()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            NewProcessor().Process(Seed("Some Mod", "Ala Mhigan Gown"), cts.Token));
    }
}
```

- [ ] **Step 4: Run to verify it fails**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~IndexProcessorTests" --nologo`

Expected: FAIL to compile, `CS0246: The type or namespace name 'IndexProcessor' could not be found`.

- [ ] **Step 5: Write the processor**

Create `PenumbraOrganizer.Plugin/LibraryWork/Pure/IndexProcessor.cs`. Move the per-mod body of `ChangedItemIndexBuilder.Build` into it verbatim, adapted to read from `IndexSeed`:

```csharp
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.LibrarySearch;
using PenumbraOrganizer.Plugin.Organizer.Classification;
using PenumbraOrganizer.Plugin.Organizer.NpcNames;

namespace PenumbraOrganizer.Plugin.LibraryWork.Pure;

/// <summary>
/// The Search index's per-mod work: changed-item facet classification, NPC name matching, and the
/// gear-slot disk probe. Same purity rule as ScanProcessor.
/// </summary>
public sealed class IndexProcessor : ILibraryWorkProcessor<IndexSeed, IndexedMod>
{
    private readonly string _npcNameListPath;
    private readonly string _npcNameSeedJson;
    private readonly List<string> _warnings = [];

    private NpcNameMatcher _npcNameMatcher = NpcNameMatcher.Empty;

    public IndexProcessor(string npcNameListPath, string npcNameSeedJson)
    {
        _npcNameListPath = npcNameListPath;
        _npcNameSeedJson = npcNameSeedJson;
    }

    public IReadOnlyList<string> Warnings => _warnings;

    public void Prepare(CancellationToken ct)
    {
        var result = NpcNameListStore.Load(_npcNameListPath, _npcNameSeedJson);
        if (result.Warning is not null)
            _warnings.Add(result.Warning);
        _npcNameMatcher = NpcNameListStore.BuildMatcher(result.Document);
    }

    public IndexedMod? Process(IndexSeed item, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (item.ChangedItemKeys.Count == 0)
            return null; // zero-changed-item mods are excluded from the browsable index

        var changedItems = item.ChangedItemKeys
            .Select(key => new IndexedChangedItem(
                key, ModTypeClassifier.ClassifyKeyFacet(ChangedItemKeyParser.Parse(key))))
            .ToList();

        var categories = changedItems
            .Where(indexed => indexed.Facet is not null)
            .Select(indexed => indexed.Facet!.Value)
            .ToHashSet();
        var hasUnknownFacetItems = changedItems.Any(indexed => indexed.Facet is null);
        var matchedByNpcNameHeuristic = _npcNameMatcher.Match(item.Name) is not null;

        IReadOnlySet<EquipmentSlot> equipmentSlots = new HashSet<EquipmentSlot>();
        var slotDiagnostic = GearSlotDiagnostic.NotApplicable;
        if (categories.Contains(ModCategory.Gear))
        {
            var modPath = new DirectoryInfo(item.ModDirectoryPath);
            var resolvedSlots = ModEquipmentFileReader.ReadEquipmentSlots(modPath);
            slotDiagnostic = resolvedSlots switch
            {
                null => GearSlotDiagnostic.ReadFailure,
                { Count: 0 } when !modPath.Exists => GearSlotDiagnostic.DirectoryMissing,
                { Count: 0 } => GearSlotDiagnostic.ZeroEvidence,
                { Count: 1 } => GearSlotDiagnostic.Single,
                _ => GearSlotDiagnostic.Ambiguous,
            };
            equipmentSlots = resolvedSlots ?? new HashSet<EquipmentSlot>();
        }

        return new IndexedMod(
            item.Identifier, item.Name, item.Author, changedItems, categories,
            hasUnknownFacetItems, matchedByNpcNameHeuristic, equipmentSlots, slotDiagnostic);
    }
}
```

- [ ] **Step 6: Run to verify it passes**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~IndexProcessorTests" --nologo`

Expected: PASS, `Failed: 0, Passed: 5`.

- [ ] **Step 7: Write IndexJob**

Create `PenumbraOrganizer.Plugin/LibraryWork/IndexJob.cs`:

```csharp
using Penumbra.Api.IpcSubscribers;
using PenumbraOrganizer.Plugin.LibrarySearch;
using PenumbraOrganizer.Plugin.LibraryWork.Pure;

namespace PenumbraOrganizer.Plugin.LibraryWork;

/// <summary> Framework-thread half of a Search index build. Mirrors ScanJob. </summary>
public sealed class IndexJob : ILibraryWorkJob<IndexSeed, IndexedMod>
{
    private readonly Plugin _plugin;
    private readonly string _npcNameListPath;
    private readonly string _npcNameSeedJson;
    private IndexProcessor? _processor;
    private HashSet<string> _changedItemIdentifiers = new(StringComparer.Ordinal);
    private List<string> _allModIdentifiers = [];

    public IndexJob(Plugin plugin, string npcNameListPath, string npcNameSeedJson)
    {
        _plugin = plugin;
        _npcNameListPath = npcNameListPath;
        _npcNameSeedJson = npcNameSeedJson;
    }

    public string DisplayName => "Search index";

    public LibraryWorkBatch<IndexSeed, IndexedMod> Materialize()
    {
        var allChangedItems = new GetChangedItemAdapterDictionary(Plugin.PluginInterface).Invoke();

        using var modList = _plugin.GetModListAdapterIpc.Invoke();

        var seeds = modList.Select(mod => new IndexSeed(
            Identifier: mod.Identifier,
            Name: mod.Name,
            Author: mod.Author,
            ModDirectoryPath: mod.ModPath.FullName,
            ChangedItemKeys: allChangedItems.TryGetValue(mod.Identifier, out var changedItems)
                ? changedItems.Keys.ToList()
                : [])).ToList();

        // Both are needed at publish time and neither is derivable from the processed results:
        // IndexProcessor drops zero-changed-item mods, but TotalModsSeen and the orphan count are
        // both defined over every mod Penumbra returned.
        _changedItemIdentifiers = allChangedItems.Keys.ToHashSet(StringComparer.Ordinal);
        _allModIdentifiers = seeds.Select(seed => seed.Identifier).ToList();

        _processor = new IndexProcessor(_npcNameListPath, _npcNameSeedJson);
        return new LibraryWorkBatch<IndexSeed, IndexedMod>(seeds, _processor);
    }

    public void Publish(IReadOnlyList<IndexedMod> results)
    {
        foreach (var warning in _processor?.Warnings ?? [])
            Plugin.Log.Warning(warning);

        // Atomic replacement: LibraryIndex is only assigned here, after every phase succeeded. A
        // failed or discarded run leaves the previous index and its BuiltAt timestamp exactly as
        // they were - a failed refresh must not discard a previously good result.
        _plugin.SetLibraryIndex(
            ChangedItemIndexBuilder.Assemble(results, _allModIdentifiers, _changedItemIdentifiers));
    }
}
```

- [ ] **Step 8: Add the Assemble entry point to the builder**

`Assemble` needs **three** inputs, not two. `ChangedItemIndex.TotalModsSeen` is documented as "every mod GetModListAdapter returned, including 0-item ones" (`ChangedItemIndex.cs:21`), and the orphan count diffs against every mod identifier — but `IndexProcessor` returns `null` for zero-changed-item mods, so the processed result list is a strict subset. Deriving either number from `indexedMods` alone would silently under-report both.

In `PenumbraOrganizer.Plugin/LibrarySearch/ChangedItemIndexBuilder.cs`, add this method and leave `Build`'s signature unchanged so its existing callers and tests still compile:

```csharp
    /// <summary>
    /// Final assembly from already-processed mods. Split out of Build so the per-mod work can run on
    /// a background thread (see LibraryWork.Pure.IndexProcessor) while this stays on the framework
    /// thread. allModIdentifiers must list every mod Penumbra returned, including the zero-changed-
    /// item ones IndexProcessor excludes from indexedMods - both TotalModsSeen and the orphan count
    /// are defined over the full set, not the indexed subset.
    /// </summary>
    public static ChangedItemIndex Assemble(
        IReadOnlyList<IndexedMod> indexedMods,
        IReadOnlyList<string> allModIdentifiers,
        IReadOnlySet<string> modIdentifiersWithChangedItems)
    {
        var orphanedCount = modIdentifiersWithChangedItems
            .Except(allModIdentifiers, StringComparer.Ordinal)
            .Count();

        return new ChangedItemIndex(indexedMods, allModIdentifiers.Count, orphanedCount, DateTime.Now);
    }
```

Then replace lines 57-61 of `Build` (its own orphan count and `ChangedItemIndex` construction) with a single delegating return, so the logic exists in exactly one place:

```csharp
        return Assemble(indexedMods, mods.Select(m => m.Identifier).ToList(), modIdentifiersWithChangedItems);
```

- [ ] **Step 9: Rewire BuildChangedItemIndex**

In `PenumbraOrganizer.Plugin/Plugin.cs`, add the coordinator field next to `ScanWork`:

```csharp
    internal LibraryWorkCoordinator<Pure.IndexSeed, LibrarySearch.IndexedMod> IndexWork { get; }
```

Initialize it beside `ScanWork` in the constructor:

```csharp
        IndexWork = new LibraryWorkCoordinator<Pure.IndexSeed, LibrarySearch.IndexedMod>(
            () => ModEvents.Current, logWarning: message => Log.Warning(message));
```

`LibraryIndex` currently has a private setter (line 36). Add an internal setter method next to it so `IndexJob` can publish:

```csharp
    internal void SetLibraryIndex(LibrarySearch.ChangedItemIndex index)
    {
        LibraryIndex = index;
        LibraryIndexError = null;
    }
```

Replace the entire body of `BuildChangedItemIndex()` (lines 204-239) with:

```csharp
    /// <summary>
    /// Starts a Search index build. Same three-phase shape as RunScan; a failed or discarded run
    /// leaves the previous LibraryIndex untouched. Throws InvalidOperationException if a library run
    /// is already in flight.
    /// </summary>
    public void BuildChangedItemIndex() =>
        IndexWork.Start(new IndexJob(this, NpcNameListPath, ReadEmbeddedNpcNameSeed()));
```

Add `IndexWork.Update();` to `OnFrameworkUpdate` next to `ScanWork.Update();`, and `IndexWork.Dispose();` to `Dispose()` next to `ScanWork.Dispose();`.

- [ ] **Step 10: Build and run the full suite**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --nologo`

Expected: PASS with no new failures. `ChangedItemIndexBuilderTests` (if present) must still pass unchanged, proving `Build` still behaves identically after delegating to `Assemble`.

- [ ] **Step 11: Commit**

```bash
git add PenumbraOrganizer.Plugin/LibraryWork/ PenumbraOrganizer.Plugin/LibrarySearch/ChangedItemIndexBuilder.cs PenumbraOrganizer.Plugin/Plugin.cs PenumbraOrganizer.Plugin.Tests/LibraryWork/IndexProcessorTests.cs
git commit -m "feat: run the Search index build off the render thread"
```

---

## Task 9: UI gating, progress, and cancel

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Windows/MainWindow.cs` — `DrawScanTab` (`:363-394`), Search tab button (`:1155-1159`), Apply gate (`:868`), Folder Cleanup gate (`:1463`), Create Backup (`:956`), Restore gates (`~:994`), Sort tab staging (`:728`)

**Interfaces:**
- Consumes: `Plugin.ScanWork`, `Plugin.IndexWork`, `LibraryWorkStateSnapshot`.
- Produces: nothing consumed by later tasks. Final task.

- [ ] **Step 1: Add the merged gates helper**

In `PenumbraOrganizer.Plugin/Windows/MainWindow.cs`, add near `DrawOperationProgress` (line 620):

```csharp
    // OperationController owns Apply/Restore lockout; the two library coordinators own scan/index
    // lockout. Merged in one place so no call site has to remember to consult all three.
    private readonly record struct ActivityGates(
        bool CanScan, bool CanIndex, bool CanStartApply, bool CanStartRestore,
        bool CanRunFolderCleanup, bool CanRunFolderCleanupRollback, bool CanCreateBackup,
        bool CanStageProposals);

    private ActivityGates CurrentGates()
    {
        var op = _plugin.OperationController.State;
        // A library run is read-only, but a completing scan calls LoadScan, which resets every
        // ProposedPath - so staging must be blocked for its duration or the user's staged work is
        // silently wiped when it lands.
        var libraryBusy = _plugin.ScanWork.State.IsRunning || _plugin.IndexWork.State.IsRunning;

        return new ActivityGates(
            CanScan: op.CanScan && !libraryBusy,
            CanIndex: op.CanIndex && !libraryBusy,
            CanStartApply: op.CanStartApply && !libraryBusy,
            CanStartRestore: op.CanStartRestore && !libraryBusy,
            CanRunFolderCleanup: op.CanRunFolderCleanup && !libraryBusy,
            CanRunFolderCleanupRollback: op.CanRunFolderCleanupRollback && !libraryBusy,
            CanCreateBackup: op.CanCreateBackup && !libraryBusy,
            CanStageProposals: !libraryBusy);
    }

    // Progress bar plus a right-aligned Cancel, reserving the button's width before the bar claims
    // it - same layout approach as DrawOperationProgress, against the library work snapshot.
    private static void DrawLibraryWorkProgress(LibraryWork.LibraryWorkStateSnapshot state, Action onCancel)
    {
        if (!state.IsRunning)
            return;

        var fraction = state.TotalItems > 0 ? (float)state.ProcessedItems / state.TotalItems : 0f;
        var buttonWidth = ImGui.CalcTextSize("Cancel").X + ImGui.GetStyle().FramePadding.X * 2;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var barWidth = state.CanCancel
            ? MathF.Max(1f, ImGui.GetContentRegionAvail().X - buttonWidth - spacing)
            : -1f;

        ImGui.ProgressBar(fraction, new Vector2(barWidth, 0),
            $"{state.ProcessedItems}/{state.TotalItems} mods");
        if (state.CanCancel)
        {
            ImGui.SameLine();
            if (ImGui.Button($"Cancel##library-work-{state.JobDisplayName}", new Vector2(buttonWidth, 0)))
                onCancel();
        }

        ImGui.TextDisabled($"{state.JobDisplayName}: {state.Phase}");
    }

    private static void DrawLibraryWorkOutcome(LibraryWork.LibraryWorkStateSnapshot state)
    {
        if (state.IsRunning)
            return;

        switch (state.LastOutcome)
        {
            case LibraryWork.LibraryWorkOutcome.Failed:
                ImGui.TextColored(PluginTheme.CollisionBad, state.LastError ?? "The run failed.");
                break;
            case LibraryWork.LibraryWorkOutcome.StaleModList:
                ImGui.TextColored(ImGuiColors.DalamudYellow,
                    "The mod list changed while this was running, so nothing was applied. Run it again.");
                break;
            case LibraryWork.LibraryWorkOutcome.Cancelled:
                ImGui.TextDisabled("Cancelled. The previous results are unchanged.");
                break;
            case LibraryWork.LibraryWorkOutcome.Completed:
            case null:
                break;
        }
    }
```

`ImGuiColors` comes from `Dalamud.Interface.Colors`, already imported by this file (see the existing use at `MainWindow.cs:1456`). `Vector2` and `MathF` are likewise already in scope.

- [ ] **Step 2: Update the Scan tab**

Replace the button block in `DrawScanTab` (lines 369-378):

```csharp
        var gates = CurrentGates();
        var scanState = _plugin.ScanWork.State;
        using (PluginTheme.PrimaryButton())
        {
            ImGui.BeginDisabled(!gates.CanScan);
            if (ImGui.Button("Refresh mod list"))
                RunScan();
            ImGui.EndDisabled();
        }
        if (!gates.CanScan && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Another operation is in progress or requires recovery.");

        DrawLibraryWorkProgress(scanState, _plugin.ScanWork.RequestCancellation);
        DrawLibraryWorkOutcome(scanState);
```

- [ ] **Step 3: Gate the Search tab, which has never had a gate**

Replace lines 1155-1159:

```csharp
        var gates = CurrentGates();
        var indexState = _plugin.IndexWork.State;
        using (PluginTheme.PrimaryButton())
        {
            ImGui.BeginDisabled(!gates.CanIndex);
            if (ImGui.Button("Build/Refresh Index"))
                BuildChangedItemIndex();
            ImGui.EndDisabled();
        }
        if (!gates.CanIndex && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Another operation is in progress or requires recovery.");

        DrawLibraryWorkProgress(indexState, _plugin.IndexWork.RequestCancellation);
        DrawLibraryWorkOutcome(indexState);
```

Add the wrapper next to `RunScan()`:

```csharp
    private void BuildChangedItemIndex()
    {
        try
        {
            _plugin.BuildChangedItemIndex();
            _lastError = null;
        }
        catch (Exception ex)
        {
            _lastError = $"Could not start index build: {ex.Message}";
            Plugin.Log.Error(ex, "Index build could not be started.");
        }
    }
```

- [ ] **Step 4: Route the remaining gated call sites through ActivityGates**

Replace each of these reads of `operationState.Can*` with the matching `CurrentGates()` field. Call `CurrentGates()` once at the top of each drawing method and reuse the local.

- `MainWindow.cs:868` — `ImGui.BeginDisabled(result.HasIssues || !operationState.CanStartApply);` becomes `ImGui.BeginDisabled(result.HasIssues || !gates.CanStartApply);`
- `MainWindow.cs:956` — the Create Backup button gains `ImGui.BeginDisabled(!gates.CanCreateBackup);` / `ImGui.EndDisabled();` around it if it does not already have one.
- `MainWindow.cs:994` — the per-snapshot Restore button gains the same treatment with `gates.CanStartRestore`.
- `MainWindow.cs:1463` — `ImGui.BeginDisabled(_selectedOrphans.Count == 0 || !operationState.CanRunFolderCleanup);` becomes `... || !gates.CanRunFolderCleanup);`
- `MainWindow.cs:1507` — the Rollback Folder Cleanup button gains `gates.CanRunFolderCleanupRollback`.
- `MainWindow.cs:728` — the Sort tab's `Assign N selected mods` button gains `gates.CanStageProposals` in its existing disabled condition.

- [ ] **Step 5: Build and run the full suite**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --nologo`

Expected: PASS with no new failures, and no `CS0165`/`CS0103` from a `gates` local used before it is declared in any drawing method.

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/Windows/MainWindow.cs
git commit -m "feat: gate, progress, and cancel for background library work

Also adds the disabled gate the Search index button never had."
```

---

## Manual verification (in-game, cannot be automated)

The test suite has no game process, so these must be checked by hand before release. Record results in the release notes.

- [ ] Scan a full library. Framerate stays smooth throughout; the progress bar advances; the mod count at the end matches what a pre-change build reported.
- [ ] Cancel a scan mid-run. The previously loaded mod list is still shown, unchanged.
- [ ] Click Rediscover Mods in Penumbra while a scan is running. The scan reports the stale-mod-list message and publishes nothing.
- [ ] Build/Refresh Index on a full library. Same framerate and progress expectations; the index summary matches a pre-change build.
- [ ] Confirm Scan, Index, Apply, Restore, Folder Cleanup, Create Backup, and Sort staging are all disabled while either run is in flight, and that the Protect tab still works and its toggles survive the scan landing.
- [ ] Unload the plugin while a scan is running. No crash, no hang beyond about two seconds.
- [ ] After a scan, hit Export in Review Changes and compare the gear-slot breakdown against a pre-change run on the same library. A jump in `ZeroEvidence` would mean the Penumbra update changed the `meta.json` layout, which is a separate issue from this work but is easiest to spot here.
