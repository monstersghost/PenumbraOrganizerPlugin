# Help tab

Date: 2026-08-07
Part of `2026-08-07-ui-overhaul-umbrella-design.md` (piece 4 of 6, built after piece 3)

## The problem

Everything explaining the plugin lives outside the game. `docs/USER_GUIDE.md` is thorough and
current, but reading it means alt-tabbing to a browser, and most users will never find it. The plugin
itself explains nothing beyond its labels.

Hover explanations (piece 3) answer "what does this button do". They cannot answer "what is this
plugin for", "what is safe", "why did my mod end up there", or "I clicked Apply and something went
wrong".

## Design

### A seventh tab, last

`Help` sits at the end of the tab bar, after Search. It **touches nothing outside the plugin**: no
Penumbra IPC, no mod library, no file writes, nothing that can be interrupted or need recovery.

That is the invariant, and it is narrower than an earlier draft's "nothing in it changes state".
Piece 5 adds a **Show the walkthrough** button here, which opens a window and is therefore
state-changing in the literal sense. That is expected and allowed: it affects only which plugin
windows are open. Piece 5 owns adding the button and the three-button layout that results; this spec
reserves the space. Nothing in it changes state,
starts work, or touches Penumbra.

### Structure

Collapsing sections, all closed on open except the first, so the tab is a contents page rather than
a wall of text:

```
Help

  > What this plugin does                    (open by default)
  > The safety rules
  > Scan
  > Protect
  > Sort
  > Review Changes and Apply
  > History and backups
  > Search
  > When something goes wrong
  > Where your files are

  [ Open the full guide on GitHub ]  [ Show config folder ]
```

**"The safety rules" is second on purpose**, before any per-tab detail. It is the section that
prevents damage:

- Nothing moves until you press Apply.
- Protected mods are never moved by sorting.
- Every Apply and Restore writes a snapshot first; History can put things back.
- The plugin only changes Penumbra's folder organisation. It never edits, moves or deletes the mod
  files themselves.

That last point is the single most common misunderstanding and is worth stating in the plugin, in
those words.

### Content comes from the shared resource

Sections render the `body` field of topics in `help-content.json` — the same file piece 3
introduces, keyed by the same ids. A section is a topic with a `body`; a tooltip is the same topic's
`short`.

This is the whole reason the umbrella spec exists. Writing help text separately from tooltip text
guarantees they diverge, and the tooltip is the one users see most.

Much of the prose adapts from `docs/USER_GUIDE.md`. Adapts, not copies: the guide is reference
documentation organised by tab, and several Help sections ("The safety rules", "When something goes
wrong") cut across tabs and have no counterpart there.

**The guide is not currently accurate, and this piece fixes that.** An earlier draft called it
"accurate and current" and made it canonical. As of this overhaul it says "The six tabs" (line 20)
and "Five buttons compute a proposed folder path" (line 81) — piece 4 makes it seven tabs and piece 2
replaces those buttons with a dropdown. **Piece 2 updates the Sort section; this piece updates the
tab list and adds the Help tab.** Shipping either without its guide edit means the canonical document
is wrong on release day.

**Section topics are not control topics.** Sections like "The safety rules" carry a `body` and no
`short`, which the umbrella's schema permits (a topic needs `title` plus at least one content field).
A section is an **ordered list of topic ids**: the section's own `body` renders first, then the `body`
of each control topic it covers. Without that composition rule a flat schema cannot express "the Sort
section is these five controls in this order", which is what this tab actually needs.

**The GitHub link is version-pinned.** "Open the full guide on GitHub" pointing at `main` serves the
newest guide to someone running an older build, which is worse than no link. The URL carries the
release tag the plugin was built from.

**The guide remains the deeper document.** The Help tab is not trying to replace it, and the button
linking to it is not an afterthought.

### Rendering

Dalamud's ImGui has no markdown renderer, and adding one for this is disproportionate. `body` is
plain text with two conventions the renderer honours:

- Blank line separates paragraphs, wrapped to the window.
- A line beginning `- ` is a bullet.

Anything else is drawn as-is. If a section ever genuinely needs richer formatting, that is a signal
it belongs in the guide instead.

### The two buttons

- **Open the full guide on GitHub** launches the default browser at the guide URL. Opening a browser
  from a game plugin is mildly intrusive, so it is a button the user presses, never automatic.
- **Show config folder** reuses the existing Explorer-opening helper from Review Changes. It is here
  because "where are my files" is a support question, and the answer is a folder most users cannot
  find.

### Where the code goes

`Windows/HelpTab.cs`, drawing into the tab. `MainWindow` gains one tab entry and one call. Given
`MainWindow.cs` is already around 2,000 lines and three other pieces of this overhaul also touch it,
none of this content lands there.

## Testing

- Every section id referenced by the tab resolves to a topic with a non-empty `body`. Same failure
  mode as piece 3's tooltip test, same reason for having it.
- The paragraph and bullet conventions render as specified, tested against the pure formatter rather
  than through ImGui.
- The tab touches nothing outside the plugin: no Penumbra IPC, no mod-library read or write, no file
  write. Enforced by review rather than a test, and stated here so the constraint is explicit. Piece
  5's **Show the walkthrough** button is compatible with this and is not a violation.
- Every section's topic list resolves, and each listed topic has a non-empty `body`.
- The GitHub link carries a version tag rather than `main`.

## Out of scope

- Search within Help.
- Any images or diagrams.
- Localisation.
- Replacing `docs/USER_GUIDE.md`. The Help tab is the in-game subset; the guide stays canonical.
