# Breadcrumbs: record which mod a library run was on when the process died

Date: 2026-08-07
Revised: 2026-08-07 after review

## Why

Three reporters have described the game closing instantly to desktop, with no error dialog, while
using the Scan path. On 2026-08-06 a reporter running 0.5.3.0 produced the first log that localizes
it:

```
19:55:57.394  [Scan:1] requested
19:55:57.403  [Scan:1] materialize begin
19:55:57.429  [Scan:1] materialize complete items=2242 elapsedMs=26 epoch=0
19:55:57.430  [Scan:1] worker started
              <end of file, line 61195 of 61195>
```

No `worker complete`, no `settled`, no error, and no line from any other plugin afterwards. The
process died inside the background worker.

That rules out the two places previous work looked. Materialization, which is where every Penumbra
IPC read happens, completed cleanly in 26ms. The 0.5.3.0 change moved those reads off the draw
callback, and the reads are demonstrably not where it dies. The planned follow-up covering the seven
remaining draw-path IPC sites would not have addressed it either.

A managed exception inside the worker `Task` is captured and surfaced as a `Failed` outcome; it
cannot terminate the game. So this is something that kills the process outright: stack overflow,
out-of-memory, a fail-fast, or a native fault. Stack overflow is uncatchable and logs nothing, which
fits "instant close, no dialog, no managed exception" best, and matches the earlier reading that the
cause is deterministic and data-dependent rather than a timing race.

The checkpoint trail localizes the failure to a phase. It cannot say which of 2,242 mods was being
processed. That is the gap this closes.

## What the artifact means

One sentence, and every design decision below serves it:

> **The previous process disappeared after this breadcrumb and before the coordinator reached a
> known terminal settlement.**

Not "the run failed". Not "the user cancelled". Those are outcomes the coordinator observes, records
and settles. This artifact exists only for the case where nothing got to observe anything.

## What this buys, and what it does not

The breadcrumb answers a question we currently cannot answer at all: how far into the batch did the
worker get, and what was it about to touch.

It does **not** identify a cause. A single run cannot distinguish these:

- Mod 37 contains data that is fatal to process.
- Mod 37 is simply where cumulative pressure crossed a threshold.

Nor is a high index proof of exhaustion: mod 2,203 could itself be pathological. Conclusions need
more than one run:

- **Same last mod across reruns, and still fatal when the library order changes so it is processed
  early:** strongly item-specific.
- **Similar processed count but a different last mod after reordering:** suggests cumulative.
- **Threshold moves with library size or available memory:** supports exhaustion.

The spec's job is to make those experiments possible. Stated here so the first report is not
over-read.

## Design

### The sink

The per-item loop lives in `LibraryWorkCoordinator.RunBatch`, which is generic over `TSeed`. It
needs a way to label a seed and somewhere to send the label.

`LibraryWorkBatch<TSeed, TResult>` gains a `Func<TSeed, string> Describe`, supplied by each job in
`Materialize` where the seed type is concrete. `ScanJob` describes a seed by its mod identifier;
`IndexJob` does the same, so index builds are covered at no extra cost.

The coordinator does not touch files. It calls an injected sink:

```csharp
public interface ILibraryRunBreadcrumbSink
{
    void Begin(string runIdentity, int totalItems);
    void ItemBegin(int oneBasedIndex, string description);
    void Clear();
}
```

The file-writing implementation lives on the plugin side; tests inject a fake that records calls.
This preserves the coordinator's defining property, that it runs under test without a game.

The sink is optional (`null` means no breadcrumbs), matching how `logInfo` and `logWarning` are
already handled.

`runIdentity` is the full identity already used by the checkpoint log, `Scan:1` or `Index:4`, not a
bare job name. A recovered breadcrumb must be correlatable with the `[Scan:1] ...` lines in
`dalamud.log` from the same session.

### The breadcrumb is written BEFORE the item is processed

This is load-bearing and was ambiguous in the first draft. The order is:

```csharp
// Describe and record BEFORE Process, so the recovered line names the item the worker was about
// to touch. Writing it afterwards would name the last item that survived, and the fatal one would
// be the unrecorded next.
SafeBreadcrumb(() => sink.ItemBegin(index + 1, batch.Describe(item)));
var result = batch.Processor.Process(item, ct);
```

The method and the record are both named `item-begin` rather than anything that could be read as
"item completed".

### Record format: JSON Lines

Descriptions are mod identifiers, which in the reporter populations seen so far are decorative and
non-ASCII. A line-oriented plain text format breaks on an embedded newline or carriage return, and
the project has already been bitten once by a pasted glyph reaching code as the wrong codepoint.

Each record is one JSON object on one line:

```json
{"kind":"header","schema":1,"timestampUtc":"2026-08-07T01:22:03Z","pluginVersion":"0.5.4.0","run":"Scan:1","total":2242}
{"kind":"item-begin","index":37,"total":2242,"description":"[⚘] dream's hair"}
```

The header carries a `schema` number so a future format change is detectable rather than silently
misparsed. Process id and Dalamud version may be added if already available cheaply; **no new IPC
call is added to enrich the header.**

Tests assert that a description survives serialize-then-parse, not that raw characters appear
literally in the file.

### Durability

The implementation opens one handle for the run and explicitly flushes after each complete encoded
record.

`Flush()` pushes the record through the process-level buffers to the operating system on every item,
which makes it highly likely to remain available after an abrupt process exit. `Flush(true)` would
additionally request a durable storage flush; that defends against power or kernel failure, which is
not what is being diagnosed, and would cost a disk sync per item.

The flush is explicit rather than relying on `AutoFlush`, so the contract is visible at the call site
and holds for whatever writer the implementation uses.

### File lifecycle

Two files in the plugin's config directory, alongside `organizer-export.txt` and
`organizer-history.json`:

- `library-run-breadcrumbs.txt` — the **active** file. Represents a run that is executing now, or one
  whose process vanished.
- `library-run-breadcrumbs.recovered.txt` — the **consumed** file. The most recent evidence already
  reported at startup.

The state machine:

```
Begin                     truncate the active file, write and flush the header
before each item          write and flush an item-begin record
terminal settlement       delete the active file  (Completed, Cancelled, AND managed Failed)
abrupt process death      no cleanup runs, so the active file survives
next plugin startup       parse the active file best-effort, log the summary,
                          rename it over the recovered file, then delete the active file
```

**The active file is cleared on every settled outcome, including cancellation and managed failure.**
This is a change from the first draft, which kept it for those cases. Keeping it was wrong: a
cancelled scan is an ordinary event, and a managed failure already records its exception type and
message through the checkpoint log. Leaving the file behind for either would make the next startup
report a process death that did not happen, which is precisely the signal this artifact exists to
carry. Ordinary usage must not manufacture false evidence.

If breadcrumbs for a managed failure are ever wanted, they belong attached to that failure's own
diagnostic, not left lying around to be misread on the next launch.

### Startup consumption

At plugin construction, before any run can call `Begin`:

1. If the active file exists, parse it best-effort.
2. Log one summary line to `dalamud.log`.
3. Rename it over `library-run-breadcrumbs.recovered.txt`, replacing any previous one.
4. Hold the parsed summary in memory for this session's diagnostic dumps.

Consuming it is what stops the same stale crash being re-reported on every launch forever, and the
rename happens before any new run so a reporter who restarts and immediately scans again does not
destroy the evidence.

### Best-effort parsing

The process can die during description formatting, encoding, writing or flushing. The active file
may therefore contain a header and nothing else, a truncated final UTF-8 sequence, a half-written
record, or records from an older schema.

The parser reads forward and keeps the last record that parses completely, rather than assuming the
final line is valid. It reports distinct outcomes:

- header plus at least one valid item-begin: the run reached that item
- header only, or no item-begin survived: **"a run began but no item breadcrumb was durably
  recovered"**, which is a different claim from "item 1 killed it"
- unreadable, locked, or unknown schema: reported as such, never thrown

### What the diagnostic dump shows

The dump is text a reporter pastes, so it does not inline 2,242 records. It carries the header
fields, the total count, the **last 20** item-begin records, and the path to the recovered file for
anyone who wants the full trail.

### Failure isolation

Every breadcrumb operation is wrapped, and the wrapping is in the coordinator so it holds for any
sink implementation rather than depending on each one being careful. The protected unit is the
**whole operation including `Describe`**, because a describer is as capable of throwing as a sink:

```csharp
try
{
    sink.ItemBegin(index + 1, batch.Describe(item));
}
catch (Exception ex)
{
    breadcrumbsEnabled = false;
    Warn($"Breadcrumbs disabled for this run: {ex.GetType().Name}: {ex.Message}");
}
```

**After the first breadcrumb-path failure, breadcrumbs are disabled for the remainder of the run**
and one warning is emitted. Retrying a known-broken diagnostic 2,242 times would produce thousands
of exceptions, thousands of warning lines, and real slowdown, in service of nothing.

The catch policy matches the existing `SafeLog` helper rather than inventing a second convention.
`StackOverflowException` is not catchable in .NET and is out of scope for this or any handler.

`Begin` and `Clear` get the same treatment. If `Begin` fails, breadcrumbs are off for the run and no
item or clear calls are attempted. If `Clear` fails, a stale active file is left behind and reads as
a process death on the next launch: a false positive, which is the correct direction to fail, since
over-reporting a possible crash is cheaper than losing a real one.

A diagnostic that can gate the plugin is worse than no diagnostic. This is the same rule the 0.5.3.0
review forced onto the logging delegates.

### Single active run

The sink has no run token, which is safe only if one library run can be active at a time. That is
structural today: `EnsureAdmitted()` precedes both `RunScan` and `BuildChangedItemIndex`, and
`LibraryActivityGate.Reason` returns a blocking reason when **either** coordinator reports
`IsRunning`. So a scan blocks an index build and vice versa, and neither can truncate the other's
file.

This invariant is relied upon rather than defended in the sink, so it gets its own test. If it is
ever relaxed, the filename must carry run identity or the sink must reject a second active run.

### Cost

One buffered write and one explicit flush per item, on the background thread, never on the framework
or draw thread.

The overhead is **not** assumed to be negligible. The config directory can sit behind antivirus
filtering, a redirected profile, cloud sync, or a Proton-style filesystem layer, any of which makes a
per-item write far more expensive than on a local SSD. Measure before release: breadcrumbs on versus
off, at 2,500 and 10,000 items, on a normal SSD and on at least one representative slower path.

The honest claim is that this work stays off the responsive threads and its wall-clock overhead will
be measured, not that it is free.

## Testing

The coordinator's tests already drive `Update()` directly and inject their delegates. A fake sink
records calls.

**Ordering and lifecycle**

- A completed run produces `Begin`, one `ItemBegin` per seed in ascending one-based order, then
  `Clear`.
- `ItemBegin` for item N is recorded **before** the processor is invoked for item N. A processor that
  blocks on item N proves the breadcrumb for N is already present.
- A **cancelled** run and a **managed failed** run both call `Clear`. These are the tests that pin
  the corrected semantic and are the ones most likely to be broken by a later "simplification".
- An empty batch writes a header and then clears it.

**Failure isolation**

- `Describe` throwing does not change the run's outcome, and disables breadcrumbs for the rest of
  the run rather than throwing once per seed.
- `ItemBegin` throwing behaves the same way, and emits exactly one warning, not one per item.
- `Begin` throwing means no `ItemBegin` and no `Clear` are attempted for that run.
- `Clear` throwing does not change the outcome and is safely logged.
- A warning logger that itself throws does not alter the run.
- A null sink behaves exactly as today.

**Format and recovery** (against the file implementation, in a temp directory)

- A description containing an embedded newline, carriage return, tab, quote and a non-BMP character
  round-trips exactly through serialize and parse.
- A truncated final record is ignored and the previous valid record is reported.
- A header-only file reports "no item breadcrumb recovered", distinctly from item 1.
- An unknown `schema` value is reported without throwing and the file is preserved.
- A locked or unreadable file is reported without throwing.
- Startup consumption runs before any `Begin`, renames the active file over the recovered file, and
  leaves no active file.
- A second startup with no new run does not re-log the same evidence.
- The diagnostic dump contains the recovered summary and at most 20 item records.

**Invariants**

- A second library run cannot begin while one is active, pinning the assumption the sink relies on.
- Plugin disposal during an active run does not call `Clear`, so an interrupted-by-unload run is not
  falsely marked settled.

## What this does not do

It does not fix the crash. It identifies where the process died, so the next report either names a
mod to reproduce against or shows the failure is a function of how much work was done.

It does not change the scan, the classification, or any Penumbra interaction.

## Follow-on

Deliberately not designed here, because the right move depends on what comes back:

- **A specific mod, stable across reruns and reordering:** obtain it, reproduce locally, fix whatever
  in classification or the gear-slot reader cannot survive it.
- **A count rather than a mod:** investigate resource exhaustion, where per-item memory and handle
  counts matter and a single identifier does not.
