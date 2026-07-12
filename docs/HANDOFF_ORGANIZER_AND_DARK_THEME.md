# Handoff: Phase 1a/1b organizer + dark theme

Both features are done and merged to `main`. This note is for whoever picks up Phase 1c or further
UI work.

## What's on `main` now

- Scan / Protect / Sort / Review Changes tabs, live Penumbra IPC scan, Heliosphere auto-protect,
  manual + by-creator sort, collision/protected-violation validation. Apply stays disabled (Phase 1
  non-goal). Design: `docs/superpowers/specs/2026-07-12-plugin-organizer-phase1-design.md`. Plan:
  `docs/superpowers/plans/2026-07-12-plugin-organizer-phase1a-1b.md`.
- A dark theme (`PluginTheme.cs`) matching the sibling app's `Theme.xaml` accent color, wired in via a
  single `PluginTheme.Push()` wrapping `MainWindow.Draw()`. Design:
  `docs/superpowers/specs/2026-07-12-dark-theme-design.md`. Plan:
  `docs/superpowers/plans/2026-07-12-dark-theme.md`.

23 tests pass (`dotnet test PenumbraOrganizer.Plugin.Tests`), build is clean.

## Known limitations, not fixed here

- **By Creator sort can collide.** Mods that share a display name but differ only by Penumbra's own
  numeric suffix (duplicate installs) collapse onto the same proposed path. `Validate()` catches it
  correctly; the sort logic itself has no dedup strategy. Needs a design decision before fixing.
- **`GetChangedItems`' key format doesn't match the assumed `"{Slot}, {Item name}"` convention.**
  Confirmed empirically; see
  `docs/superpowers/specs/2026-07-12-changed-items-format-spike-findings.md`. Phase 1c (by mod type)
  needs a different classification approach, not written yet.
- **The window's title bar and outer border are Dalamud's own chrome**, shared across every plugin.
  The theme covers everything inside the window; it can't and shouldn't touch that.
- **A custom font** (to get closer to the app's Segoe UI look) is possible via Dalamud's font-atlas
  API but wasn't attempted. Separate task, not a style tweak.

## One thing to know if you create another worktree here

`PenumbraOrganizer.Plugin.csproj` links a few source files from the sibling `PenumbraOrganizer` repo
using a path relative to the plugin csproj's own folder (`..\..\PenumbraOrganizer\...`). That only
resolves correctly when the two repos sit as real siblings under the same parent. A nested worktree
under `.claude/worktrees/<name>/` breaks that path. Fix used here: an NTFS junction at
`.claude/worktrees/PenumbraOrganizer` pointing at the real `C:\Repo\PenumbraOrganizer`, so any worktree
nested one level under `.claude/worktrees/` resolves the link correctly without needing its own copy
of the app repo.
