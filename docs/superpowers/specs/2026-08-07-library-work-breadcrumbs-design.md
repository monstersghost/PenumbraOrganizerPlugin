# Breadcrumbs: record which mod a library run was on when it died

Date: 2026-08-07

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

## What this buys

The breadcrumb answers a question we currently cannot answer at all: **did the run die on a
particular mod, or after a particular amount of work?**

- Trail stops at mod 37 of 2,242: a specific mod's data is fatal. Get that mod, reproduce locally,
  fix the code that chokes on it.
- Trail stops at mod 2,203 of 2,242: resource exhaustion. A single mod is irrelevant and the fix is
  somewhere else entirely.

Those need completely different investigations and are currently indistinguishable. Stated plainly
because it bounds the claim: the last breadcrumb is **where** the run died, not **why**. If the
cause is cumulative, naming the last mod is a red herring unless read alongside the count.

## Design

### The sink

The per-item loop lives in `LibraryWorkCoordinator.RunBatch`, which is generic over `TSeed`. It
therefore needs both a way to label a seed and somewhere to send the label.

`LibraryWorkBatch<TSeed, TResult>` gains a `Func<TSeed, string> Describe`, supplied by each job in
`Materialize` where the seed type is concrete. `ScanJob` describes a seed by its mod identifier;
`IndexJob` does the same, so index builds are covered at no extra cost.

The coordinator does not touch files. It calls an injected sink:

```csharp
public interface ILibraryRunBreadcrumbSink
{
    void Begin(string runLabel, int totalItems);
    void Item(int oneBasedIndex, string description);
    void End();
}
```

The file-writing implementation lives on the plugin side; tests inject a fake that records calls.
This preserves the coordinator's defining property, that it runs under test without a game, and
keeps file IO out of it.

The sink is optional (`null` means no breadcrumbs), matching how `logInfo` and `logWarning` are
already handled.

### Durability without fsync

The implementation opens one handle for the run and calls `Flush()` after each line.

`Flush()` moves bytes out of the process buffer into the OS. The OS completes the write even if our
process is killed, which is exactly the failure being chased. `Flush(true)` would additionally
survive power loss, at the cost of a disk sync per mod: 2,242 syncs on a scan, for a guarantee
against a failure mode that is not in question.

This distinction is the reason the feature can ship enabled for everyone rather than hidden behind
an opt-in that nobody has turned on at the moment they need it.

### File lifecycle

Location: `library-run-breadcrumbs.txt` in the plugin's config directory, alongside
`organizer-export.txt`, `organizer-history.json` and the operations root.

- `Begin` truncates the file and writes a header: timestamp, plugin version, run label, item count.
- `Item` appends one line: index, total, description.
- `End` **deletes** the file.

Deleting on success is what makes the file's existence meaningful: **if the file is present, the
last run did not finish.** A reader does not have to reason about whether stale contents belong to
an older session.

### Making it reach us without asking anyone to send a new file

A new artifact that reporters do not know about is worth very little. So the breadcrumb is surfaced
through the two things they already produce:

- **On startup**, if the file exists, log one line to `dalamud.log` naming the run, the last index
  reached and the last description. Any future crash report that includes `dalamud.log` therefore
  carries the answer without the reporter doing anything.
- **In Create Diagnostic Dump**, add a section with the same information. This is the button the
  plugin already tells people to press.

The startup line is written before the file could be overwritten by a new run, so a reporter who
restarts the game and immediately scans again does not destroy the evidence from the previous crash.

### Failure isolation

Every sink call is wrapped so that a breadcrumb failure can never affect a run: a full disk, a
locked file, or a permissions problem must degrade to "no breadcrumbs", never to a failed or stranded
scan.

This is the same rule the 0.5.3.0 review forced onto the logging delegates, for the same reason. A
diagnostic that can gate the plugin is worse than no diagnostic. The wrapping belongs in the
coordinator, around the sink calls, so it holds for any sink implementation rather than relying on
each one to be careful.

The `Begin`/`End` pair is not treated as a resource that must be balanced. If `Begin` throws, the run
proceeds without breadcrumbs; if `End` throws, a stale file is left behind, which reads as "the last
run did not finish". That is a false positive on the next report, and it is the right way to fail:
over-reporting a possible crash is cheaper than losing a real one.

### Cost

One buffered write and one `Flush()` per item, on the background thread. On a 2,242 mod library that
is 2,242 write syscalls against a scan that already performs per-mod disk reads for every Gear mod.
It is not measurable against the existing work and does not touch the framework or draw threads.

## Testing

The coordinator's tests already drive `Update()` directly and inject their delegates.

- A fake sink records `Begin`/`Item`/`End` calls. Assert a completed run produces `Begin`, one
  `Item` per seed in order with correct one-based indices, then `End`.
- A cancelled run and a failed run produce `Begin` and some `Item` calls but **no** `End`, so the
  file survives for diagnosis. This is the behaviour the whole design rests on and it gets its own
  test per outcome.
- A sink whose `Begin`, `Item` and `End` all throw must not change the run's outcome: the run still
  completes and publishes. Mirrors `ThrowingLogger_DoesNotStrandTheRun`.
- A run with a null sink behaves exactly as today.
- `Describe` is called once per item and its result reaches the sink unaltered, including for a mod
  identifier containing characters that are awkward in a text file. The reporter populations seen so
  far use decorative mod names heavily, so this is not hypothetical.

The file implementation is tested separately against a temp directory: header contents, one line per
item, deletion on `End`, and that the file survives when `End` is never called.

## What this does not do

It does not fix the crash. It identifies where the crash happens, so that the next report either
names a mod to reproduce against or shows that the cause is cumulative.

It does not change the scan, the classification, or any Penumbra interaction.

## Follow-on, once a report comes back

Deliberately not designed here, because the right move depends on what the breadcrumb says:

- **A specific mod:** obtain it, reproduce locally, and fix whatever in classification or the
  gear-slot reader cannot survive it.
- **Near the end of a large library:** investigate resource exhaustion, at which point per-mod
  memory and handle counts matter and a single identifier does not.
