# Handoff: body detection, UX pass, tool-managed mods (2026-11-05)

Everything here is merged to local `main` (merge `0f649ad`) and NOT pushed or released. Released
version is still 0.6.2.1. 1872 tests pass.

## What changed
- **Body type from content.** Evidence tiers: mod content (option groups/names, first folder of file
  values), then mod name, then library path; a saved `BodyPriority` breaks ties at sort time.
  Body-category mods swap content and name (the mod's own name wins). "Yet Another Body+" now matches
  YAB. Specs/plans: `docs/superpowers/{specs,plans}/2026-11-05-body-priority-from-mod-content*`.
- **Sort/Review UX pass.** Three-section Sort tab (Group by + Sort always visible; Gear/NPC/Hair and
  Body type sections), `(?)` help popups, separate gear/body-mod body splits, Workbook group on
  Review, collapsed Diagnostics, History delete confirmation, collapsed Scan events, "Protect all".
  Every Sort tab choice is now saved (`Configuration.Sort*`).
- **Tool-managed mods.** `HeliosphereMode`/`ProteusMode` (Protected/Sort inside/Treat as normal) and
  `ProteusManagedAtRoot`, saved. Detection: Proteus-made = `meta.json` author exactly `Proteus`;
  managed mod = directory `Proteus` + that author. Dropdowns show only when the scan finds that
  tool's mods. Templates apply the same placement. Spec/plan: `2026-11-05-tool-managed-mods*`.
- **Failed mods window.** `LastOperationFailures` reads the newest finished operation bundle
  (`operations/completed/<id>/{journal,plan,results}`) and lists failed mods with reason and target.
- **Apply fix.** `ApplyPlanner.OrderMovesForApply` parks a mod that sits where a folder is needed.
- **Templates.** Fallback carries optional hair/body flags (older files import unchanged); the
  importer's own hair race and body priority apply; the creator remembers its fallback choices and
  closes on save.

## Verified in game (by the author)
Dropdowns for Heliosphere (22) and Proteus (4, via test mods), the managed-at-root option, saved
modes, Apply of the body folders after the blocker fix, history/diagnostic output.

## Not verified / open
- Proteus modes against a real Proteus library (only test mods).
- `(?)` popup keyboard activation (depends on ImGui keyboard nav in Dalamud).
- Help tab "Open the guide as a PDF": 404s until a release tag includes the PDF; test on that build.
- First-run tutorial rewrite with on-screen highlights; retake guide screenshots (mock is stale).
- Remove the stale `.claude\worktrees\body-type` dev-plugin path in Dalamud (/xlsettings,
  Experimental, Dev Plugin Locations) - done by hand, not from code.

## Testing checklists
`docs/testing/2026-11-05-sort-review-ux-checklist.md`, `docs/testing/2026-11-05-tool-managed-mods-checklist.md`.
