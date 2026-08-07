# Help tab

Date: 2026-08-07
Part of `2026-08-07-ui-overhaul-umbrella-design.md` (piece 4 of 5)

## The problem

Everything explaining the plugin lives outside the game. `docs/USER_GUIDE.md` is thorough and
current, but reading it means alt-tabbing to a browser, and most users will never find it. The plugin
itself explains nothing beyond its labels.

Hover explanations (piece 3) answer "what does this button do". They cannot answer "what is this
plugin for", "what is safe", "why did my mod end up there", or "I clicked Apply and something went
wrong".

## Design

### A seventh tab, last

`Help` sits at the end of the tab bar, after Search. It is read-only: nothing in it changes state,
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

Much of the prose adapts from `docs/USER_GUIDE.md`, which is accurate and current. Adapts, not
copies: the guide is reference documentation organised by tab, and several Help sections
("The safety rules", "When something goes wrong") cut across tabs and have no counterpart there.

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
- The tab is read-only: no plugin method that mutates state is reachable from it. Enforced by review
  rather than a test, and stated here so the constraint is explicit.

## Out of scope

- Search within Help.
- Any images or diagrams.
- Localisation.
- Replacing `docs/USER_GUIDE.md`. The Help tab is the in-game subset; the guide stays canonical.
