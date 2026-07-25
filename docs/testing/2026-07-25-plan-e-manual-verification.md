# Plan E Manual Verification: Operation UI and Diagnostics Presentation

**Scope:** in-game verification for Plan E, merged to `main` at `8839604`. Covers capability
lockout, the Apply/Restore progress bar and Cancel control, per-mod recovery detail, multi-root
incremental recovery resolution, diagnostics dump v2, the History tab's Recent Operations section,
and `completed/` retention pruning.

**Not in scope:** anything from Plans B1/B2/C/D1/D2 (the execution engine, Apply/Restore
themselves, Continue/Restore Previous State) — those are prerequisites this plan builds on, not
things this document re-tests. If a test case below fails in a way that looks like it belongs to
one of those plans (e.g. Apply itself doesn't move mods), stop and treat it as a regression in
already-shipped code, not a Plan E defect.

**Prerequisites:**
- A dev-plugin build of this branch loaded in a real FFXIV/Dalamud/Penumbra session.
- A mod library with at least a handful of mods, ideally including at least one cycle-breaking
  swap (two mods that need to pass through each other's paths) so item 2's per-mod progress
  behavior is actually observable.
- Read `docs/TESTING_GUIDE.md`'s Precautions section first (back up Penumbra's config folder) —
  this plan's changes are presentation-layer only, but the operations underneath still move real
  mod paths.
- Follow along in `docs/superpowers/plans/2026-07-25-plan-e-operation-ui-and-diagnostics-implementation.md`
  and `docs/superpowers/specs/2026-07-25-plan-e-operation-ui-and-diagnostics-design.md` if you want
  the full reasoning behind why a given check matters.

Check off each item as you go. If something fails, note what you saw instead of the expected
result — don't just mark it failed.

---

## 1. Capability lockout

**Setup:** get another organizer operation into progress (e.g. start an Apply on a large-enough
library that it spans multiple frames) so `RequiresRecovery`/`CanScan` etc. are false while it runs.

- [ ] While the operation is in progress, the Scan, Create Backup, Restore (per-snapshot), Clean
  Up Selected Folders, and Rollback Folder Cleanup buttons are all visibly greyed out.
- [ ] Hovering any of those five greyed-out buttons shows a tooltip explaining "Another operation
  is in progress or requires recovery."
- [ ] With no operation in progress and no folders selected, hover the greyed-out Clean Up
  Selected Folders button — confirm it shows **no** "another operation" tooltip (it's disabled for
  an unrelated reason: nothing selected). This is the one button with two independent disabling
  conditions, and the tooltip must only fire for the capability-flag one.

## 2. Apply/Restore progress bar

**Setup:** start an Apply on a real multi-mod library, ideally with at least one cycle-breaking
swap.

- [ ] The progress bar fills proportionally by **mod count**, not step count — a mod involved in
  a swap (temporary hop + final move = 2 steps for 1 target) does not make the bar jump by more
  than its own share.
- [ ] The bar keeps advancing as mods are processed, and the "Applying: `<mod name>`" line updates
  to the most recently finished mod.
- [ ] If any mod fails during the run, the bar still reaches 100% once every mod has been
  attempted — it does not stall short of full just because some failed. The separate succeeded/
  failed count line below the bar (not the bar itself) reflects the failure.
- [ ] A Cancel button appears next to the bar once the operation reaches the Mutating stage, and
  disappears once it leaves that stage — with no clipping or overlap between the bar and the
  button at any window width.
- [ ] Repeat this whole item for a Restore operation on the History tab.

## 3. Cancel control

**Setup:** start an Apply (or Restore) on a library large enough that you can click Cancel while
it's still running.

- [ ] Click Cancel mid-Mutating. **No confirmation popup appears** — it should act immediately.
- [ ] The operation stops at the next safe boundary (not mid-step) and settles as `Cancelled`.
- [ ] No other organizer action is left stuck disabled afterward — Scan/Apply/Restore etc. become
  available again once the cancelled operation settles.

## 4. Recovery panel: per-mod detail

**Setup:** force a single-root interrupted recovery — start an Apply or Restore on a real library,
force-quit the game mid-operation (mid-Mutating or mid-Refreshing), then relaunch. (Same mechanism
D1/D2's own manual checklists used.)

- [ ] The recovery panel's new "Details" section (collapsed by default) shows correct color-coded
  per-mod classification: red for blocking mods, green for already-resolved mods, yellow for mods
  queued for Continue.
- [ ] Immediately after the crash-recovery panel first appears (before classification has had time
  to settle), the Details section shows "Still checking live mod state..." — not an error.
- [ ] Once classification settles, if it settles to **unavailable** rather than a real assessment
  (hard to force deliberately — note if you happen to observe it, e.g. by disabling Penumbra
  mid-check), confirm the message reads "Per-mod classification is unavailable..." and is
  distinguishable from the "still checking" message above — it must not say "still checking"
  forever.
- [ ] If you can corrupt or delete the interrupted operation's `plan.json` or `snapshot.json` on
  disk before relaunching, confirm the Details section shows the corresponding "missing" or
  "corrupt" artifact-status line, with Continue and/or Restore Previous State's buttons disabled
  to match.

## 5. Multi-root / cycle recovery

**Setup:** this state doesn't arise from ordinary use — hand-construct it. Duplicate or hand-edit
bundle directories under `<config>/organizer/operations/active/` to create either (a) two
unrelated non-terminal journals (disconnected roots), or (b) three journals whose
`RecoveryOfOperationId` fields chain into a cycle (A→B→C→A). Relaunch with the plugin/game.

- [ ] Each blocked operation gets its own row, showing type/stage/interruption timestamp.
- [ ] The panel's top explanatory text is accurate: it does not claim clicking a row turns that
  operation into an ordinary recoverable one — it should describe the *remaining* set possibly
  becoming smaller, a same-size different set, a single recoverable operation, or fully resolved.
- [ ] Click one row's "Keep Current State." The confirmation popup explicitly states **that
  selected operation** cannot later be continued or restored (not just "other operations stay
  blocked").
- [ ] After confirming, that row disappears and the remaining list correctly shrinks (or, if it
  was the cycle case, the surviving operation correctly becomes the ordinary single-root recovery
  panel — not still shown as blocked).
- [ ] If it was the last blocked operation, the panel unblocks entirely (either shows nothing, or
  falls through to an ordinary ongoing state).
- [ ] The bulk "Accept Current State and Close All Interrupted Operations" button still works as
  the fallback, resolving everything remaining at once.

## 6. Diagnostics dump v2

**Setup:** ideally run this once with an interrupted recovery still pending (single-root or
multi-root) and once with a clean, idle plugin state, to see both branches.

- [ ] With a pending single-root recovery, the dump's "Interrupted operation" section shows the
  **real** interruption timestamp (matches when the operation actually stopped) — not the moment
  you clicked "Create Diagnostic Dump."
- [ ] With a pending multi-root block instead, the same section lists every blocked operation's
  type/stage/timestamp.
- [ ] With no pending recovery, the section reads "(none)."
- [ ] The "Recent operations" section lists real completed operations with type/stage/resolution/
  timestamp, or "(none)" on a fresh install.
- [ ] The "Slow calls" section is grouped by identifier (count, worst duration, total duration per
  identifier) — not just the five longest individual raw events. If you have no slow calls
  recorded, confirm it reads "(none)" rather than erroring.
- [ ] None of the three new sections throws or silently disappears — if you can simulate one
  section's data source being unreadable (e.g. lock a file under `completed/`), confirm that
  section alone shows a "(failed to read: ...)" line while the other two still render normally.

## 7. History tab: Recent Operations

- [ ] The new "Recent Operations" section appears on the History tab, **collapsed by default**,
  visually distinct from the existing snapshot list above it.
- [ ] Expanding it lists real completed Apply/Restore/Continue/Restore-Previous-State operations
  with correct type/stage/resolution/timestamp.
- [ ] Leave the section expanded and idle for a while (at least several seconds, ideally longer) —
  confirm there's no visible re-query, flicker, or stutter tied to this section while nothing
  else is happening.
- [ ] With the section still open, complete a new operation (e.g. run another Apply), then click
  the "Refresh" button — confirm the new operation now appears without needing to collapse and
  re-expand the section first.

## 8. Retention pruning

**Setup:** get more than 50 bundles under `<config>/organizer/operations/completed/` (or use a
temporarily lowered retention threshold if you have a way to do that for testing), then restart
the plugin or the game.

- [ ] On the next startup, `completed/` is pruned down to the retention window (newest 50, or 30
  days, whichever keeps more) without the plugin erroring or hanging.
- [ ] Separately, simulate a retention failure — e.g. lock a file/folder under `completed/` so it
  can't be deleted or read — and restart. Confirm the plugin still starts up normally (recovery
  discovery still runs, the UI is usable) with a warning logged, not a crash or a stuck startup.

## 9. Stale confirmation popup (Restore)

**Setup:** click a Restore row's button to open its confirmation popup, but don't confirm yet.

- [ ] While the popup is still open, cause `CanStartRestore` to become false — e.g. from another
  window/instance, or by triggering another operation that starts in the meantime — then click
  "Yes" on the still-open popup.
- [ ] Confirm the confirm handler rejects the now-stale confirmation (surfaces an error rather
  than proceeding) instead of silently starting a Restore that should have been blocked.

---

## Sign-off

| # | Item | Result | Notes |
|---|------|--------|-------|
| 1 | Capability lockout | | |
| 2 | Progress bar (Apply) | | |
| 2 | Progress bar (Restore) | | |
| 3 | Cancel control | | |
| 4 | Recovery detail | | |
| 5 | Multi-root/cycle | | |
| 6 | Diagnostics dump v2 | | |
| 7 | Recent Operations | | |
| 8 | Retention pruning | | |
| 9 | Stale popup | | |

**Overall:** ☐ Plan E verified in-game — ☐ Issues found (see notes above)
