# Non-Blocking Library Work Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move the Scan tab's mod walk and the Search tab's index build off the game's render thread, so neither can freeze FFXIV on a large or slow-disk mod library.

**Architecture:** A shared `LibraryWorkCoordinator<TSeed, TResult>` runs both jobs in three phases: a single framework-thread frame that copies plain strings out of the Penumbra IPC adapters, a background `Task` that does all classification and disk I/O against those strings, and a framework-thread publish that commits atomically via `OrganizerState.ReplaceScanAtomically` or a single `LibraryIndex` assignment. Phase-2 code lives in a `LibraryWork.Pure` namespace that an architecture test forbids from referencing Dalamud or Penumbra types. Mutual exclusion across Scan, Index, and `OperationController` is a domain invariant enforced in `Plugin`, not a consequence of disabled buttons.

**Revision note:** this plan was returned for correction after its first review. Six blocking changes are folded in: domain-level admission control (Task 8), atomic publish separated from post-commit side effects (Tasks 3 and 9), cancellation honoured when it arrives after the background task completed (Tasks 4 and 5), synchronous scheduler failure handled (Tasks 4 and 5), disposal semantics and a `_disposed` flag (Tasks 4 and 5), and recursive purity enforcement covering result DTOs (Task 7). Two new tasks were inserted and the rest renumbered.

**Tech Stack:** C# / .NET 10 (`net10.0-windows7.0`), Dalamud.NET.Sdk 15.0.0, Penumbra.Api 5.15.1, xunit 2.5.3.

**Spec:** `docs/superpowers/specs/2026-07-29-non-blocking-library-work-design.md`

## Global Constraints

- Target framework `net10.0-windows7.0`; `ImplicitUsings` and `Nullable` are both enabled in both projects. Do not add `using System;`-style directives that implicit usings already cover.
- Test framework is xunit 2.5.3 with `<Using Include="Xunit" />` in the test csproj, so `[Fact]` and `Assert` need no `using`. Test namespaces mirror folder structure (`PenumbraOrganizer.Plugin.Tests.<Folder>`).
- Temp-directory test convention, copied from `HeliosphereDetectorTests`: `new DirectoryInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()))`, cleaned up in a `finally` with `Delete(recursive: true)`.
- `OperationController.cs` must not be modified by any task in this plan. Coordination with it is read-only, through its published `State` snapshot.
- No type in namespace `PenumbraOrganizer.Plugin.LibraryWork.Pure` may reference a type from the `Dalamud` or `Penumbra.Api` assemblies, and neither may the `TSeed`/`TResult` types that cross the thread boundary. Task 7 enforces this.
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

    /// <summary>
    /// Framework thread only. Moves queued lines into <see cref="Lines"/>, most recently arrived
    /// first. Note this is queue ARRIVAL order, not a chronological ordering of when the events
    /// fired: callbacks on different threads have no meaningful universal order. Timestamps are
    /// captured at callback time in the line text itself.
    /// </summary>
    public void Drain()
    {
        var drained = new List<string>();
        while (_incoming.TryDequeue(out var line))
            drained.Add(line);

        if (drained.Count == 0)
            return;

        // One InsertRange of a reversed batch rather than repeated Insert(0, ...), which is O(n)
        // per line. Harmless at a 200-line cap either way; the batch form is simply clearer.
        drained.Reverse();
        _lines.InsertRange(0, drained);

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
    // Called from Penumbra's IPC subscribers, which may be on any thread. The timestamp is captured
    // here rather than at drain time so it records when the callback fired; display order is queue
    // arrival order, which is not the same thing and does not claim to be.
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

Three subscribers are **not enough**. Seeds carry absolute `ModDirectoryPath` strings, so a mod-root change mid-run makes every one of them wrong and every Gear mod resolve to `DirectoryMissing` — a wrong-but-plausible published result, which is worse than a failure because nothing looks broken. Add two more subscribers alongside the existing three, and matching `EventSubscriber` fields next to `_modMoved` (`Plugin.cs:47-49`):

```csharp
    private readonly EventSubscriber<string> _modDirectoryChanged;
    private readonly EventSubscriber _penumbraDisposed;
```

Check the exact generic arity against `Penumbra.Api.IpcSubscribers.ModDirectoryChanged` and `Disposed` before compiling; `ModDirectoryChanged` carries the new directory, `Disposed` carries nothing. Register them with the others:

```csharp
        _modDirectoryChanged = ModDirectoryChanged.Subscriber(PluginInterface, dir =>
        {
            ModEvents.Increment();
            _mainWindow.LogEvent($"Mod directory changed: {dir}");
        });
        // Penumbra's own docs for GetChangedItemAdapterDictionary say to clear it on Disposed, so a
        // run holding one across this event is working from storage that is no longer valid.
        _penumbraDisposed = Disposed.Subscriber(PluginInterface, () =>
        {
            ModEvents.Increment();
            _mainWindow.LogEvent("Penumbra unloaded.");
        });
```

Dispose both in `Plugin.Dispose()` next to the existing three (`Plugin.cs:111-113`):

```csharp
        _modDirectoryChanged.Dispose();
        _penumbraDisposed.Dispose();
```

`Initialized` deliberately does not bump the epoch: a run cannot have started before Penumbra existed, and a run in flight when Penumbra initialises was already invalidated by the `Disposed` that preceded it.

- [ ] **Step 6: Build and run the full suite**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --nologo`

Expected: PASS, no new failures.

- [ ] **Step 7: Commit**

```bash
git add PenumbraOrganizer.Plugin/LibraryWork/ModEventEpoch.cs PenumbraOrganizer.Plugin.Tests/LibraryWork/ModEventEpochTests.cs PenumbraOrganizer.Plugin/Plugin.cs
git commit -m "feat: count Penumbra mod-list changes for staleness detection"
```

---

## Task 3: Atomic scan replacement in OrganizerState

`LoadScan` calls `_mods.Clear()` (`OrganizerState.cs:50`) and then fills. Anything throwing after that line leaves the state half-replaced while the caller reports failure. Passing a fully-materialized list does not fix this — it only removes deferred-enumeration risk. Build-then-swap does.

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs:8-11` (field modifiers), `:44-72` (`LoadScan`)
- Modify: `PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateTests.cs` (append)

**Interfaces:**
- Consumes: nothing.
- Produces: `OrganizerState.ReplaceScanAtomically(IEnumerable<OrganizerModRow> scanned, IReadOnlySet<string> previouslyProtectedIdentifiers, IReadOnlySet<string>? previouslyProtectedFolders = null)`. `LoadScan` keeps its exact current signature and delegates, so Apply, Restore, Protect, and Folder Cleanup are untouched.

- [ ] **Step 1: Write the failing test**

Append to `PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateTests.cs` (match the file's existing helpers for constructing an `OrganizerModRow`; read it first):

```csharp
    [Fact]
    public void ReplaceScanAtomically_ThrowingDuringDerivation_LeavesPreviousStateIntact()
    {
        var state = new OrganizerState();
        state.LoadScan(
            [new OrganizerModRow { Identifier = "a", Name = "A", Author = "x", CurrentPath = "Gear/A", ProposedPath = "Gear/A" }],
            new HashSet<string>(StringComparer.Ordinal));

        // A row whose enumeration throws part-way: the first item is fine, the second blows up
        // during derivation, exactly like a malformed path would.
        IEnumerable<OrganizerModRow> Exploding()
        {
            yield return new OrganizerModRow { Identifier = "b", Name = "B", Author = "y", CurrentPath = "Gear/B", ProposedPath = "Gear/B" };
            throw new InvalidOperationException("derivation failed");
        }

        Assert.Throws<InvalidOperationException>(() =>
            state.ReplaceScanAtomically(Exploding(), new HashSet<string>(StringComparer.Ordinal)));

        // The previous scan must still be entirely present and uncontaminated.
        var mods = state.Mods;
        Assert.Single(mods);
        Assert.Equal("a", mods[0].Identifier);
        Assert.Contains("Gear", state.KnownFolders);
    }

    [Fact]
    public void ReplaceScanAtomically_OnSuccess_BehavesExactlyLikeLoadScan()
    {
        var viaLoadScan = new OrganizerState();
        var viaReplace = new OrganizerState();
        OrganizerModRow[] Rows() =>
        [
            new() { Identifier = "a", Name = "A", Author = "x", CurrentPath = "Gear/Feet/A", ProposedPath = "somewhere/else" },
        ];
        var protectedIds = new HashSet<string>(["a"], StringComparer.Ordinal);

        viaLoadScan.LoadScan(Rows(), protectedIds);
        viaReplace.ReplaceScanAtomically(Rows(), protectedIds);

        Assert.Equal(viaLoadScan.Mods.Select(m => m.Identifier), viaReplace.Mods.Select(m => m.Identifier));
        Assert.Equal(viaLoadScan.Mods[0].Protected, viaReplace.Mods[0].Protected);
        Assert.Equal(viaLoadScan.Mods[0].ProposedPath, viaReplace.Mods[0].ProposedPath);
        Assert.Equal(viaLoadScan.KnownFolders, viaReplace.KnownFolders);
        Assert.True(viaReplace.HasScanned);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~OrganizerStateTests" --nologo`

Expected: FAIL to compile, `CS1061: 'OrganizerState' does not contain a definition for 'ReplaceScanAtomically'`.

- [ ] **Step 3: Drop readonly from the four state fields**

In `PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs`, lines 8-11:

```csharp
    // Not readonly: ReplaceScanAtomically swaps these references only after every replacement
    // collection has been built successfully, so a throw during derivation cannot leave the state
    // half-replaced. Nothing else reassigns them.
    private Dictionary<string, OrganizerModRow> _mods = new();
    private HashSet<string> _protectedModIdentifiers = new(StringComparer.Ordinal);
    private HashSet<string> _protectedFolders = new(StringComparer.Ordinal);
    private List<string> _knownFolders = [];
```

- [ ] **Step 4: Add ReplaceScanAtomically and make LoadScan delegate**

Replace the body of `LoadScan` (lines 44-72) with a delegation, and add the new method beside it:

```csharp
    // previouslyProtectedFolders defaults to null (treated as empty) so every existing call
    // site across this test project that predates folder protection keeps compiling unchanged.
    public void LoadScan(
        IEnumerable<OrganizerModRow> scanned,
        IReadOnlySet<string> previouslyProtectedIdentifiers,
        IReadOnlySet<string>? previouslyProtectedFolders = null) =>
        ReplaceScanAtomically(scanned, previouslyProtectedIdentifiers, previouslyProtectedFolders);

    /// <summary>
    /// Whole-state replacement that either fully happens or does not happen at all. Every
    /// replacement collection is built first; the field references are swapped only once all
    /// derivation has succeeded. A background scan publishes through this, so a throw here must
    /// leave the previously published scan exactly as it was rather than half-replaced.
    /// </summary>
    public void ReplaceScanAtomically(
        IEnumerable<OrganizerModRow> scanned,
        IReadOnlySet<string> previouslyProtectedIdentifiers,
        IReadOnlySet<string>? previouslyProtectedFolders = null)
    {
        var replacementProtectedIdentifiers = new HashSet<string>(previouslyProtectedIdentifiers, StringComparer.Ordinal);
        var replacementProtectedFolders = new HashSet<string>(
            previouslyProtectedFolders ?? new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);

        var replacementMods = new Dictionary<string, OrganizerModRow>();
        foreach (var row in scanned)
        {
            // Protection is derived against the REPLACEMENT sets, not the live fields, so this loop
            // reads nothing it is about to overwrite.
            row.Protected = IsEffectivelyProtected(row, replacementProtectedIdentifiers, replacementProtectedFolders);
            row.ProposedPath = row.CurrentPath;
            replacementMods[row.Identifier] = row;
        }

        var replacementKnownFolders = replacementMods.Values
            .Select(m => OrganizationCleanupPlanner.GetVirtualParent(m.CurrentPath))
            .Where(f => f is not null)
            .Select(f => f!)
            .SelectMany(AncestorChain)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        // COMMIT. Nothing above this point has touched published state.
        _protectedModIdentifiers = replacementProtectedIdentifiers;
        _protectedFolders = replacementProtectedFolders;
        _mods = replacementMods;
        _knownFolders = replacementKnownFolders;
        HasScanned = true;
    }
```

Read the existing `IsEffectivelyProtectedFull` before writing this: it currently reads the `_protectedModIdentifiers`/`_protectedFolders` fields directly. Add an overload (named `IsEffectivelyProtected` above) taking the two sets as parameters, and have the existing field-reading version delegate to it so no other caller changes.

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~OrganizerStateTests" --nologo`

Expected: PASS, including every pre-existing `OrganizerStateTests` fact. Those passing unchanged is the proof that `LoadScan`'s behaviour is identical after delegating.

- [ ] **Step 6: Run the full suite**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --nologo`

Expected: PASS with no new failures. `OrganizerState` is shared with Apply, Restore, Protect, and Folder Cleanup, so a regression here would surface across several unrelated test classes.

- [ ] **Step 7: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateTests.cs
git commit -m "feat: make whole-scan replacement atomic

Build every replacement collection first, swap references only once all
derivation succeeded. LoadScan keeps its signature and delegates."
```

---

## Task 4: Coordinator contracts and happy path

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

Create `PenumbraOrganizer.Plugin.Tests/LibraryWork/LibraryWorkCoordinatorTests.cs`. This file grows in Task 5; write it now with the shared fakes plus the happy-path facts.

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

    public void Start(ILibraryWorkJob<TSeed, TResult> job)
    {
        // Without this, anything calling RunScan during teardown schedules fresh background work
        // into a plugin that is going away.
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (State.IsRunning)
            throw new InvalidOperationException($"{State.JobDisplayName} is already running.");

        _job = job;
        _processed = 0;
        _total = 0;
        _startEpoch = _readEpoch();
        PublishRunning(LibraryWorkPhase.Materializing);

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

        // Materialize is the last unbounded piece of per-run work still on the render thread, and
        // render-thread latency is the entire point of this design - so it is measured rather than
        // assumed. 100ms is roughly six frames at 60fps: long enough not to fire on a healthy
        // library, short enough to catch a hitch a user would notice. A starting value to revise
        // once real numbers exist, not a claim about what is achievable.
        var materializeElapsed = Stopwatch.GetElapsedTime(materializeStarted);
        if (materializeElapsed > MaterializeWarningThreshold)
            _logWarning?.Invoke(
                $"{job.DisplayName}: materializing {batch.Items.Count} mods held the framework "
                + $"thread for {materializeElapsed.TotalMilliseconds:F0}ms.");

        _total = batch.Items.Count;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
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

## Task 5: Coordinator cancellation, staleness, failure, and disposal

**Files:**
- Modify: `PenumbraOrganizer.Plugin.Tests/LibraryWork/LibraryWorkCoordinatorTests.cs` (append facts)

**Interfaces:**
- Consumes: everything Task 4 produced. No production code changes are expected; Task 4's coordinator already implements these paths. If a test fails, fix the coordinator, not the test.

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
```

- [ ] **Step 2: Run the tests**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~LibraryWorkCoordinatorTests" --nologo`

Expected: PASS, `Failed: 0, Passed: 24`.

No test should take more than ~50 ms of wall clock; every dispose path injects a short or zero timeout. If a test hangs, the injected `disposeWait` is not being honoured. If any test fails, fix `LibraryWorkCoordinator`, not the test.

- [ ] **Step 3: Commit**

```bash
git add PenumbraOrganizer.Plugin.Tests/LibraryWork/LibraryWorkCoordinatorTests.cs
git commit -m "test: cover coordinator cancel, staleness, failure, and disposal paths"
```

---

## Task 6: Scan seed and processor

**Files:**
- Create: `PenumbraOrganizer.Plugin/LibraryWork/Pure/ScanSeed.cs`
- Create: `PenumbraOrganizer.Plugin/LibraryWork/Pure/ScanProcessor.cs`
- Create: `PenumbraOrganizer.Plugin.Tests/LibraryWork/ScanProcessorTests.cs`

**Interfaces:**
- Consumes: `ILibraryWorkProcessor<TSeed, TResult>` from Task 4.
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

## Task 7: Purity architecture test

**Files:**
- Create: `PenumbraOrganizer.Plugin.Tests/LibraryWork/LibraryWorkPurityTests.cs`

**Interfaces:**
- Consumes: `ScanProcessor`, `ScanSeed` from Task 6; `OrganizerModRow` and `IndexedMod` as additional roots.
- Produces: nothing consumed by later tasks. Guards Tasks 6 and 10.

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
    public void PureTypesAndCrossThreadDtos_DoNotReferenceDalamudOrPenumbra()
    {
        var assembly = typeof(ScanProcessor).Assembly;

        var roots = assembly.GetTypes()
            .Where(t => t.Namespace is { } ns
                && (ns == PureNamespace || ns.StartsWith(PureNamespace + ".", StringComparison.Ordinal)))
            .ToList();

        // Guards against the check silently passing because the namespace was renamed or emptied.
        Assert.NotEmpty(roots);

        // The DTOs that cross the thread boundary but live OUTSIDE the Pure namespace. Without
        // these as explicit roots, a Penumbra-typed field on OrganizerModRow or IndexedMod would
        // violate the rule and still pass - which is exactly the regression the rule exists to stop.
        roots.Add(typeof(PenumbraOrganizer.Plugin.Organizer.OrganizerModRow));
        roots.Add(typeof(PenumbraOrganizer.Plugin.LibrarySearch.IndexedMod));

        var violations = new List<string>();
        var visited = new HashSet<Type>();
        var queue = new Queue<Type>(roots);

        while (queue.Count > 0)
        {
            var type = queue.Dequeue();
            if (!visited.Add(type))
                continue;

            foreach (var referenced in SignatureTypes(type))
            {
                if (IsForbidden(referenced))
                {
                    violations.Add($"{type.FullName} references {referenced.FullName} "
                        + $"from {referenced.Assembly.GetName().Name}");
                    continue;
                }

                // Recurse only into our own types; stop at BCL and third-party boundaries so the
                // walk terminates and stays meaningful.
                if (referenced.Assembly == assembly && !visited.Contains(referenced))
                    queue.Enqueue(referenced);
            }
        }

        Assert.Empty(violations.Distinct());
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

- [ ] **Step 3: Verify the test actually catches both kinds of violation**

A guard test that cannot fail is worthless, and the recursive DTO walk is the part most likely to be subtly broken. Check both.

First, a direct violation. Temporarily add this field to `ScanProcessor`:

```csharp
    private Dalamud.Plugin.IDalamudPluginInterface? _deliberateViolation;
```

Re-run the command from Step 2. Expected: FAIL, naming `ScanProcessor references Dalamud.Plugin.IDalamudPluginInterface from Dalamud`. **Remove the field.**

Second, the reachable-DTO violation the namespace-only version would have missed. Temporarily add this property to `OrganizerModRow` (in `PenumbraOrganizer.Plugin/Organizer/OrganizerModRow.cs`):

```csharp
    public Dalamud.Plugin.IDalamudPluginInterface? DeliberateViolation { get; init; }
```

Re-run. Expected: FAIL, naming `OrganizerModRow`. If this one passes, the extra roots or the recursion are not wired correctly — fix the test before continuing. **Remove the property** and re-run to confirm PASS.

- [ ] **Step 4: Commit**

```bash
git add PenumbraOrganizer.Plugin.Tests/LibraryWork/LibraryWorkPurityTests.cs
git commit -m "test: forbid Dalamud and Penumbra types in the background work namespace"
```

---

## Task 8: Admission control

Mutual exclusion across Scan, Index, and `OperationController` must be a domain invariant, not a consequence of disabled buttons. Nothing today prevents `plugin.RunScan(); plugin.BuildChangedItemIndex();` from running both at once, and `StartApplyOperation` (`Plugin.cs:447-452`) has no knowledge of library work at all.

**Files:**
- Create: `PenumbraOrganizer.Plugin/LibraryWork/ActivityAdmission.cs`
- Create: `PenumbraOrganizer.Plugin.Tests/LibraryWork/ActivityAdmissionTests.cs`

**Interfaces:**
- Consumes: `LibraryWorkStateSnapshot` (Task 4), `OperationStateSnapshot`.
- Produces: `ActivityAdmission.Check(operation, scan, index)` returning `string?` — null when admission is allowed, otherwise the reason to put in the exception message. `Plugin.EnsureNoConflictingActivity()` and `Plugin.TryRequestScan()` are added in Task 9.

- [ ] **Step 1: Write the failing test**

Create `PenumbraOrganizer.Plugin.Tests/LibraryWork/ActivityAdmissionTests.cs`:

```csharp
using PenumbraOrganizer.Plugin.LibraryWork;
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.LibraryWork;

public class ActivityAdmissionTests
{
    private static LibraryWorkStateSnapshot Running(string name) => new(
        LibraryWorkPhase.Computing, name, 1, 10, null, null, CanCancel: true);

    private static LibraryWorkStateSnapshot Idle => LibraryWorkStateSnapshot.Idle;

    [Fact]
    public void NothingRunning_IsAdmitted()
    {
        Assert.Null(ActivityAdmission.Check(OperationStateSnapshot.Idle, Idle, Idle));
    }

    [Fact]
    public void ScanRunning_BlocksAdmission_AndNamesTheJob()
    {
        var reason = ActivityAdmission.Check(OperationStateSnapshot.Idle, Running("Scan"), Idle);

        Assert.NotNull(reason);
        Assert.Contains("Scan", reason);
    }

    [Fact]
    public void IndexRunning_BlocksAdmission_AndNamesTheJob()
    {
        var reason = ActivityAdmission.Check(OperationStateSnapshot.Idle, Idle, Running("Search index"));

        Assert.NotNull(reason);
        Assert.Contains("Search index", reason);
    }

    [Fact]
    public void OperationLockout_BlocksAdmission()
    {
        var operation = OperationStateSnapshot.Idle with { CanScan = false };

        Assert.NotNull(ActivityAdmission.Check(operation, Idle, Idle));
    }

    [Fact]
    public void RecoveryRequired_BlocksAdmission()
    {
        var operation = OperationStateSnapshot.Idle with { RequiresRecovery = true, CanScan = false };

        var reason = ActivityAdmission.Check(operation, Idle, Idle);

        Assert.NotNull(reason);
        Assert.Contains("recovery", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LibraryWorkIsReportedBeforeOperationLockout()
    {
        // Both blocked: the library run is the more actionable message, since the user can cancel it.
        var operation = OperationStateSnapshot.Idle with { CanScan = false };

        var reason = ActivityAdmission.Check(operation, Running("Scan"), Idle);

        Assert.Contains("Scan", reason);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~ActivityAdmissionTests" --nologo`

Expected: FAIL to compile, `CS0246: The type or namespace name 'ActivityAdmission' could not be found`.

- [ ] **Step 3: Write the policy**

Create `PenumbraOrganizer.Plugin/LibraryWork/ActivityAdmission.cs`:

```csharp
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.LibraryWork;

/// <summary>
/// The single admission rule shared by every activity that must not overlap another: Scan, Index,
/// Apply, Restore, folder cleanup, cleanup rollback, and backup.
///
/// This is the invariant. ActivityGates is the presentation of it. Disabling a button prevents a
/// click; it does not prevent a slash command, a test hook, or an existing code path that predates
/// the gate - and three recovery paths (Plugin.cs:549, :590, :604) already call RunScan() directly.
/// </summary>
public static class ActivityAdmission
{
    /// <summary> Null when admission is allowed; otherwise the reason, for an exception message. </summary>
    public static string? Check(
        OperationStateSnapshot operation,
        LibraryWorkStateSnapshot scan,
        LibraryWorkStateSnapshot index)
    {
        // Reported first because it is the more actionable of the two: the user can cancel a library
        // run, whereas an operation lockout has to resolve on its own terms.
        if (scan.IsRunning)
            return $"{scan.JobDisplayName ?? "A scan"} is already running.";
        if (index.IsRunning)
            return $"{index.JobDisplayName ?? "An index build"} is already running.";

        if (operation.RequiresRecovery)
            return "An interrupted operation requires recovery before anything else can run.";
        if (!operation.CanScan)
            return "An Apply or Restore operation is currently active.";

        return null;
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~ActivityAdmissionTests" --nologo`

Expected: PASS, `Failed: 0, Passed: 6`.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/LibraryWork/ActivityAdmission.cs PenumbraOrganizer.Plugin.Tests/LibraryWork/ActivityAdmissionTests.cs
git commit -m "feat: add the shared activity admission rule"
```

---

## Task 9: ScanJob, rewiring, and dead code removal

**Files:**
- Create: `PenumbraOrganizer.Plugin/LibraryWork/ScanJob.cs`
- Modify: `PenumbraOrganizer.Plugin/Plugin.cs` — `RunScan` (`:142-202`), `OnFrameworkUpdate` (`:126`), `Dispose` (`:104-120`), field block (`~:38-45`), `CreateBackup` (`:322`), `StartApplyOperation` (`:445`), `StartRestoreOperation` (`:490`), `ResolveKeepCurrent` (`:549`), `AcceptAllAndCloseInterruptedOperations` (`:590`), `ResolveOneMultiRootOperation` (`:604`), `CleanUpFolders` (`:787`), `RollbackFolderCleanup` (`:807`); delete `ApplyChanges()` (`:373-443`) and `Restore(Guid)` (`:608-693`)
- Modify: `PenumbraOrganizer.Plugin/Windows/MainWindow.cs` — `RunScan` (`:1613-1629`), delete the unused `_lastApplyResults` field (`:35`)

**Interfaces:**
- Consumes: `LibraryWorkCoordinator<ScanSeed, OrganizerModRow>` (Task 4), `ScanSeed`/`ScanProcessor` (Task 6), `ModEventEpoch` (Task 2), `ActivityAdmission` (Task 8), `OrganizerState.ReplaceScanAtomically` (Task 3).
- Produces: `Plugin.ScanWork` of type `LibraryWorkCoordinator<ScanSeed, OrganizerModRow>`; `Plugin.RunScan()` now starts a run instead of completing one; `Plugin.EnsureNoConflictingActivity()`; `Plugin.TryRequestScan()`; `Plugin.RunPostScanSideEffects()`; `MainWindow.OnScanPublished()`.

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

        // THE COMMIT. Build-then-swap: either the whole new state is installed or none of it is.
        // Anything above this line that throws leaves the previous scan completely intact.
        _plugin.OrganizerState.ReplaceScanAtomically(
            results, _plugin.Config.ProtectedModIdentifiers, _plugin.Config.ProtectedFolderPaths);

        // POST-COMMIT. The new data is already live, so a failure here is a warning, never a failed
        // run - reporting Failed would tell the UI to say nothing was published when it was.
        _plugin.RunPostScanSideEffects();
    }
}
```

- [ ] **Step 2: Expose what ScanJob needs from Plugin**

In `PenumbraOrganizer.Plugin/Plugin.cs`, change the accessibility of the members `ScanJob` reads. `Config` is currently `internal Configuration Config` (line 38) and `GetModListAdapterIpc` is `internal readonly` (line 32) — both are already reachable from the same assembly, so no change is needed there. Change `NpcNameListPath` (line 264) and `ReadEmbeddedNpcNameSeed` (line 266) from `private` to `internal`:

```csharp
    internal string NpcNameListPath => Path.Combine(PluginInterface.ConfigDirectory.FullName, "npc-name-list.json");

    internal static string ReadEmbeddedNpcNameSeed()
```

Add the post-commit side effects, placed next to `ToggleMainUi` (line 124). These belong to `Plugin`, not to a window: refreshing orphaned folders is a consequence of new scan data, and routing it through `MainWindow` would invert the dependency and hide the fact that these run *after* the commit point.

```csharp
    // Everything that must happen once new scan data is live, none of which can be rolled back.
    // Each is isolated: one failing must not skip the others, and none may fail the run - the data
    // is already published by the time this is called.
    internal void RunPostScanSideEffects()
    {
        try
        {
            SaveProtectionState();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Saving protection state after a scan failed; the scan itself succeeded.");
        }

        try
        {
            _mainWindow.OnScanPublished();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Post-scan refresh failed; the scan itself succeeded.");
        }
    }
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
    /// per-mod disk walk run on a background thread and publish through ScanJob.Publish.
    /// Throws InvalidOperationException if any conflicting activity is running.
    /// </summary>
    public void RunScan()
    {
        EnsureNoConflictingActivity();
        ScanWork.Start(new ScanJob(this, NpcNameListPath, ReadEmbeddedNpcNameSeed()));
    }

    /// <summary>
    /// Best-effort scan for callers that must not fail if one cannot start right now - specifically
    /// the recovery-resolution paths, whose scan is a refresh after the fact, not a correctness
    /// requirement. Returns false and logs rather than throwing out of a committed recovery.
    /// </summary>
    internal bool TryRequestScan()
    {
        if (ActivityAdmission.Check(OperationController.State, ScanWork.State, IndexWork.State) is { } reason)
        {
            Log.Information($"Post-recovery scan skipped: {reason} Use Refresh mod list when it clears.");
            return false;
        }

        RunScan();
        return true;
    }

    // The single admission point every long-running activity shares. See ActivityAdmission for why
    // this is a domain invariant rather than something the UI can be trusted to enforce.
    internal void EnsureNoConflictingActivity()
    {
        if (ActivityAdmission.Check(OperationController.State, ScanWork.State, IndexWork.State) is { } reason)
            throw new InvalidOperationException(reason);
    }
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

- [ ] **Step 3b: Apply admission to every other starting wrapper, and fix the recovery paths**

Add `EnsureNoConflictingActivity();` as the **first** statement of `StartApplyOperation` (`:445`), `StartRestoreOperation` (`:490`), `CleanUpFolders` (`:787`), `RollbackFolderCleanup` (`:807`), and `CreateBackup` (`:322`). It goes before their existing `_operationInProgress` checks, so a library run blocks them the same way an operation does.

Then change the three unguarded recovery call sites, which would otherwise throw out of a committed recovery resolution:

`Plugin.cs:549` in `ResolveKeepCurrent`:

```csharp
    internal void ResolveKeepCurrent()
    {
        OperationController.ResolveKeepCurrent();
        TryRequestScan();
    }
```

`Plugin.cs:590` in `AcceptAllAndCloseInterruptedOperations`:

```csharp
    internal void AcceptAllAndCloseInterruptedOperations()
    {
        OperationController.AcceptAllAndCloseInterruptedOperations();
        TryRequestScan();
    }
```

`Plugin.cs:604` in `ResolveOneMultiRootOperation` — keep its existing `RequiresRecovery` guard and swap only the call:

```csharp
        if (!OperationController.State.RequiresRecovery)
            TryRequestScan();
```

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

## Task 10: Index seed, processor, job, and rewiring

**Files:**
- Create: `PenumbraOrganizer.Plugin/LibraryWork/Pure/IndexSeed.cs`
- Create: `PenumbraOrganizer.Plugin/LibraryWork/Pure/IndexProcessor.cs`
- Create: `PenumbraOrganizer.Plugin/LibraryWork/IndexJob.cs`
- Create: `PenumbraOrganizer.Plugin.Tests/LibraryWork/IndexProcessorTests.cs`
- Modify: `PenumbraOrganizer.Plugin/Plugin.cs` — `BuildChangedItemIndex` (`:204-239`), `OnFrameworkUpdate`, `Dispose`, field block
- Modify: `PenumbraOrganizer.Plugin/LibrarySearch/ChangedItemIndexBuilder.cs` — split the per-mod body out

**Interfaces:**
- Consumes: everything from Tasks 4, 6, 7, 8.
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
    public void BuildChangedItemIndex()
    {
        EnsureNoConflictingActivity();
        IndexWork.Start(new IndexJob(this, NpcNameListPath, ReadEmbeddedNpcNameSeed()));
    }
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

## Task 11: ActivityGates as a testable policy

The lockout matrix must be a pure function with its own tests. As a private `MainWindow` helper it would be verified only by "the UI still compiles", which makes a missed call site invisible.

**Files:**
- Create: `PenumbraOrganizer.Plugin/Windows/ActivityGates.cs`
- Create: `PenumbraOrganizer.Plugin.Tests/Windows/ActivityGatesTests.cs`

**Interfaces:**
- Consumes: `OperationStateSnapshot`, `LibraryWorkStateSnapshot`.
- Produces: `ActivityGates.Build(operation, scan, index)` returning an `ActivityGates` with `CanScan`, `CanIndex`, `CanStartApply`, `CanStartRestore`, `CanRunFolderCleanup`, `CanRunFolderCleanupRollback`, `CanCreateBackup`, `CanStageProposals`.

- [ ] **Step 1: Write the failing test**

Create `PenumbraOrganizer.Plugin.Tests/Windows/ActivityGatesTests.cs`:

```csharp
using PenumbraOrganizer.Plugin.LibraryWork;
using PenumbraOrganizer.Plugin.Organizer.Operations;
using PenumbraOrganizer.Plugin.Windows;

namespace PenumbraOrganizer.Plugin.Tests.Windows;

public class ActivityGatesTests
{
    private static LibraryWorkStateSnapshot Running => new(
        LibraryWorkPhase.Computing, "Scan", 1, 10, null, null, CanCancel: true);

    private static LibraryWorkStateSnapshot Finished(LibraryWorkOutcome outcome) => new(
        LibraryWorkPhase.Idle, null, 10, 10, outcome, null, CanCancel: false);

    private static LibraryWorkStateSnapshot Idle => LibraryWorkStateSnapshot.Idle;

    [Fact]
    public void EverythingIdle_AllowsEverything()
    {
        var gates = ActivityGates.Build(OperationStateSnapshot.Idle, Idle, Idle);

        Assert.True(gates.CanScan);
        Assert.True(gates.CanIndex);
        Assert.True(gates.CanStartApply);
        Assert.True(gates.CanStartRestore);
        Assert.True(gates.CanRunFolderCleanup);
        Assert.True(gates.CanRunFolderCleanupRollback);
        Assert.True(gates.CanCreateBackup);
        Assert.True(gates.CanStageProposals);
    }

    [Fact]
    public void ScanRunning_BlocksEverythingIncludingStaging()
    {
        var gates = ActivityGates.Build(OperationStateSnapshot.Idle, Running, Idle);

        Assert.False(gates.CanScan);
        Assert.False(gates.CanIndex);
        Assert.False(gates.CanStartApply);
        Assert.False(gates.CanStartRestore);
        Assert.False(gates.CanRunFolderCleanup);
        Assert.False(gates.CanRunFolderCleanupRollback);
        Assert.False(gates.CanCreateBackup);
        Assert.False(gates.CanStageProposals);
    }

    [Fact]
    public void IndexRunning_BlocksScan()
    {
        var gates = ActivityGates.Build(OperationStateSnapshot.Idle, Idle, Running);

        Assert.False(gates.CanScan);
        Assert.False(gates.CanIndex);
    }

    [Fact]
    public void OperationLockout_BlocksLibraryWork()
    {
        var operation = OperationStateSnapshot.Idle with
        {
            CanScan = false, CanIndex = false, CanStartApply = false, CanStartRestore = false,
            CanRunFolderCleanup = false, CanRunFolderCleanupRollback = false, CanCreateBackup = false,
        };

        var gates = ActivityGates.Build(operation, Idle, Idle);

        Assert.False(gates.CanScan);
        Assert.False(gates.CanIndex);
        Assert.False(gates.CanStartApply);
    }

    [Fact]
    public void RecoveryRequired_BlocksLibraryWork()
    {
        var operation = OperationStateSnapshot.Idle with
        {
            RequiresRecovery = true, CanScan = false, CanIndex = false,
        };

        var gates = ActivityGates.Build(operation, Idle, Idle);

        Assert.False(gates.CanScan);
        Assert.False(gates.CanIndex);
    }

    [Theory]
    [InlineData(LibraryWorkOutcome.Completed)]
    [InlineData(LibraryWorkOutcome.Cancelled)]
    [InlineData(LibraryWorkOutcome.StaleModList)]
    [InlineData(LibraryWorkOutcome.Failed)]
    public void AnyTerminalOutcome_ReleasesEveryGate(LibraryWorkOutcome outcome)
    {
        var gates = ActivityGates.Build(OperationStateSnapshot.Idle, Finished(outcome), Idle);

        Assert.True(gates.CanScan);
        Assert.True(gates.CanStartApply);
        Assert.True(gates.CanStageProposals);
    }

    [Fact]
    public void StagingIsBlockedOnlyByLibraryWork_NotByOperationLockout()
    {
        // Staging edits ProposedPath, which only a completing LoadScan clobbers. An Apply in flight
        // is already prevented from starting a second Apply by CanStartApply; it has no reason to
        // stop the user preparing the next batch.
        var operation = OperationStateSnapshot.Idle with { CanStartApply = false };

        var gates = ActivityGates.Build(operation, Idle, Idle);

        Assert.True(gates.CanStageProposals);
        Assert.False(gates.CanStartApply);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~ActivityGatesTests" --nologo`

Expected: FAIL to compile, `CS0246: The type or namespace name 'ActivityGates' could not be found`.

- [ ] **Step 3: Write the policy**

Create `PenumbraOrganizer.Plugin/Windows/ActivityGates.cs`:

```csharp
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
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~ActivityGatesTests" --nologo`

Expected: PASS, `Failed: 0, Passed: 10` (7 facts, one of which is a 4-case theory).

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Windows/ActivityGates.cs PenumbraOrganizer.Plugin.Tests/Windows/ActivityGatesTests.cs
git commit -m "feat: extract the UI lockout matrix into a tested policy"
```

---

## Task 12: UI wiring, progress, and cancel

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Windows/MainWindow.cs` — `DrawScanTab` (`:363-394`), Search tab button (`:1155-1159`), Apply gate (`:868`), Folder Cleanup gate (`:1463`), Create Backup (`:956`), Restore gates (`~:994`), Sort tab staging (`:728`)

**Interfaces:**
- Consumes: `ActivityGates.Build` from Task 11, `Plugin.ScanWork`, `Plugin.IndexWork`.
- Produces: nothing consumed by later tasks. Final task.

- [ ] **Step 1: Add the drawing helpers**

In `PenumbraOrganizer.Plugin/Windows/MainWindow.cs`, add near `DrawOperationProgress` (line 620):

```csharp
    private ActivityGates CurrentGates() => ActivityGates.Build(
        _plugin.OperationController.State, _plugin.ScanWork.State, _plugin.IndexWork.State);

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
- [ ] Resolve an interrupted operation (Keep Current, or Accept All) while a scan happens to be running. The recovery must complete normally, with a "post-recovery scan skipped" line in the log rather than an exception — this is the `TryRequestScan` path.
- [ ] Check the Dalamud log after a full scan for the materialize-duration warning. If it fires on a healthy library, the 100 ms threshold in `LibraryWorkCoordinator.MaterializeWarningThreshold` needs revisiting against the real number it reports.
- [ ] Unload the plugin while a scan is running. No crash, no hang beyond about two seconds.
- [ ] After a scan, hit Export in Review Changes and compare the gear-slot breakdown against a pre-change run on the same library. A jump in `ZeroEvidence` would mean the Penumbra update changed the `meta.json` layout, which is a separate issue from this work but is easiest to spot here.
