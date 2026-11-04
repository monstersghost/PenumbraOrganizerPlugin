# UI mock

A static HTML emulation of the plugin's in-game window. It exists for one reason: to produce the
screenshots in [../USER_GUIDE.md](../USER_GUIDE.md) without needing a capture from a running game.

It is **not** the plugin. Nothing here is wired to anything, and no file in this folder is compiled
or shipped.

## Keeping it honest

The whole value of these pictures is that they match what a user sees, so:

- Colours come from `PenumbraOrganizer.Plugin/Windows/PluginTheme.cs` (`--window-bg`, `--surface`,
  `--border`, `--accent`, `--protected`, `--changed-good`, `--collision-bad` map to its constants).
- Every control label is copied verbatim from the drawing code — `MainWindow.*Tab.cs`,
  `SortPanel.cs`, `FolderProtectionTree.cs`, and the confirm prompt's strings.
- The window title carries the released version.

**If you change a label, a colour, or the tab order in the plugin, change it here and retake the
affected screenshot**, or the guide starts describing a UI that no longer exists.

## Files

- `index.html` — the source of truth. All screens live in it as `<section>` elements, picked with a
  URL hash (`#protect`, `#sort`, …) when you open it in a normal browser.
- `<screen>.html` — one generated file per screen, which is what gets screenshotted.
- `style.css` — the shared stylesheet, for reading and editing.

The per-screen files are generated because the screenshot preview renders local files as **static
snapshots**: it ignores `:target` (so hash switching does nothing) and does not fetch a linked
stylesheet (so a `<link>` produces unstyled screenshots). Hence one file per screen, each with its
CSS inlined.

## Regenerating

1. Edit `index.html`.
2. Split it into per-screen files:

```bash
python docs/ui-mock/split.py
```

3. Open each `docs/ui-mock/<screen>.html` at a viewport of **800 wide** and screenshot it. The mock
   is set to `width: 100%` up to 1120px, so at 800 it is captured 1:1 and the small text stays
   sharp; anything wider gets downscaled by the pane and smears.
4. Crop the page background and save into `docs/images/<name>.png`:

```bash
python docs/ui-mock/crop.py <screenshot.jpg> docs/images/<name>.png
```

Current screenshots and their screens:

| Image | Screen |
| --- | --- |
| `images/scan.png` | `scan.html` |
| `images/protect-tree.png` | `protect.html` |
| `images/protect-search.png` | `protect-search.html` |
| `images/protect-unprotect-confirm.png` | `protect-confirm.html` |
| `images/sort.png` | `sort.html` |
| `images/review-changes.png` | `review.html` |
| `images/history.png` | `history.html` |
