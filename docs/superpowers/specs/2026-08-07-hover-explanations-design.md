# Hover explanations

Date: 2026-08-07
Part of `2026-08-07-ui-overhaul-umbrella-design.md` (piece 3 of 6, built after piece 0)

> **Note on ordering.** The schema and loader described here move to the front of **piece 2**, which
> needs a tooltip for its disabled checkboxes and is built first. This piece is then the sweep across
> the remaining controls. Piece 2 is the first consumer, not this one.

## The problem

The plugin has around forty interactive controls and almost no in-place explanation. Tooltips exist,
but only for one purpose: explaining why a control is *disabled*. There are nine copies of

```csharp
ImGui.SetTooltip("Another operation is in progress or requires recovery.");
```

and essentially nothing telling a user what a control *does*.

So a user meets "Protect & Skip All Blocking Mods", "Rollback Folder Cleanup" or "Re-read
organization.json" with no way to find out what they mean without leaving the game.

## Design

### One topic per control, referenced by id

Controls reference a topic id. They never carry explanation text inline.

```csharp
Help.Tooltip("sort.gear-detail");
```

`help-content.json` ships as an embedded resource, matching how `npc-name-list-seed.json` already
does:

```json
{
  "id": "sort.gear-detail",
  "title": "Split gear by equipment slot",
  "short": "Puts gear in Gear/Head, Gear/Feet and so on instead of one Gear folder.",
  "body": "..."
}
```

This piece defines the schema and the loader because it is the first consumer. The Help tab and the
guided first run add `body` and `step` to the same topics rather than starting their own stores. The
umbrella spec has the full rationale.

### The rules that keep it useful

- **`short` is one line, plain text, no formatting.** A tooltip that needs a paragraph is a signal
  the control's label is wrong. Fix the label instead.
- **`short` says what the control does, not what it is.** "Puts gear in Gear/Head, Gear/Feet..." not
  "Toggles detailed gear sorting."
- **An id referenced from code that is missing from the resource fails a test**, listing the missing
  ids. This is a **test**, not a build-time failure; the umbrella previously claimed the latter and
  has been corrected. To make it meaningful, **`Tooltip` and `Short` take a typed `HelpTopic`, not a
  `string`**. Otherwise `Help.Tooltip("sort.gear-detial")` compiles, ships, and passes every test in
  this spec and the two that follow it.
- **Topics unused by *any* consumer are allowed**, but a topic that carries content nothing renders
  is not. Specifically: every topic with a `body` must appear in some Help section list, and every
  topic with a `step` must appear in the tutorial's step list. Without this, adding a `step` to the
  resource does nothing at all — step *order* lives in code — with no test to catch it. An earlier
  draft's blanket "unused topics are allowed" removed the only defence pieces 4 and 5 have.

### Coexisting with the disabled-reason tooltips

The nine "Another operation is in progress" tooltips answer a different question, and it is a more
urgent one: the user just tried to click something and it did not respond.

When a control is both disabled and has a topic, the tooltip shows **both**, reason first:

```
Another operation is in progress or requires recovery.

Applies every proposed path change to Penumbra.
```

The disabled reason is not moved into `help-content.json`. It is state, not documentation.

**Correction to an earlier draft: `ActivityGates` does not produce these strings.** It is a
`readonly record struct` of eight `bool`s. The message is a hardcoded literal at **nine** call sites
in `MainWindow.cs`. This piece does not invent a generator for it; each call site keeps passing its
own reason.

**A single nullable reason is not sufficient, and a mechanical sweep would cause a regression.** Two
controls are disabled for more than one reason:

- `MainWindow.cs:988` — Apply, on `result.HasIssues || !gates.CanStartApply`
- `MainWindow.cs:1560` — Folder Cleanup, on `_selectedOrphans.Count == 0 || !gates.CanRunFolderCleanup`

The second carries an explicit three-line comment at `MainWindow.cs:1564-1566` warning that the
operation-in-progress message must **not** appear when the real reason is "nothing selected".
Replacing these with `Help.Tooltip(id, reason)` where `reason` is a single string reintroduces
exactly the bug that comment exists to prevent.

So the signature is `Tooltip(HelpTopic topic, string? disabledReason = null)`, and **the call site
decides which reason applies**, preserving the existing conditional. Those two sites are converted by
hand and reviewed individually, not swept.

**Call-site placement is a hard requirement, not a style note.** `Help.Tooltip` must be called
immediately after the widget, and outside any `BeginDisabled`/`EndDisabled` scope, following the
existing pattern at `MainWindow.cs:424-430`. It must pass `ImGuiHoveredFlags.AllowWhenDisabled`
internally. Get either wrong and tooltips silently never appear on disabled controls — the case this
feature most needs to work.

### Coverage

Every control that is not self-evident from its label gets a topic. Concretely:

- **Scan**: Refresh mod list, the event log
- **Protect**: both toggle-all buttons, the folder/mod distinction, the Heliosphere note
- **Sort**: the Group by dropdown, both split checkboxes, the Sort button, Import Workbook, manual
  assignment
- **Review Changes**: Apply, Export, Export Workbook and its destinations dropdown, Protect & Skip
  All Blocking Mods, Show Config File, Create Diagnostic Dump, Clean Up Selected Folders, Rollback
  Folder Cleanup, Re-read organization.json
- **History**: Create Backup, Restore, Delete snapshot
- **Search**: Build/Refresh Index, the filter boxes, the category and slot rows
- **The recovery panel** (`MainWindow.cs:268-307`): Keep Current State, Continue, Restore Previous
  State. These sit **above** the tab bar, so no tab entry covers them, and they are by a distance the
  most consequential and least self-explanatory controls in the plugin. An earlier draft omitted them.
- **Piece 1's scraped-NPC-list checkbox** (`sort.scraped-npc-list`). It is the one new control whose
  consequence is genuinely subtle, so it must not be the one control without an explanation.

Deliberately **not** covered: Cancel, Yes/No confirmation buttons, and anything inside a modal whose
own text already explains the choice. A tooltip on "Cancel" is noise.

### Where the code goes

`Windows/Help.cs` holds the loader and the two entry points:

```csharp
// A HelpTopic, not a string: a mistyped literal must not compile.
internal static class Help
{
    public static string Short(HelpTopic topic);
    public static void Tooltip(HelpTopic topic, string? disabledReason = null);
}
```

`Help` is a **static holder over a lazily-initialised loader**, not an injected instance. The resource
is embedded and immutable, there is exactly one of it, and threading an instance through every draw
method would be plumbing with no benefit. The initialiser parses once; a parse failure is a packaging
bug, not a runtime condition, and throws with a message naming the resource — the same treatment
`NpcNameListStore.Load` gives its bundled seed. Tests reach it through the same static surface.

**Tooltips wrap explicitly.** `ImGui.SetTooltip` does not wrap: it lays out one line as wide as it
needs, so a long `short` produces a tooltip wider than the viewport, and a disabled reason plus a
`short` compounds it. `Tooltip` pushes a fixed wrap position for the tooltip's lifetime.

**Text is static.** No substitution. One control changes meaning with state — Toggle protect all
protects everything or unprotects everything depending on current state — so its single `short` is
written to cover both directions rather than the schema growing a parameter mechanism.

## Testing

- **Every id referenced in code resolves.** Ids are `HelpTopic` constants collected by reflection, so
  the set is enumerable rather than scraped from source. Each must resolve. The typed parameter is
  what makes this exhaustive rather than best-effort.
- Every topic referenced **as a tooltip** has a non-empty `short`. (Not every topic: Help-only and
  tutorial-only topics legitimately have none.)
- Every topic carrying `body` appears in a Help section list; every topic carrying `step` appears in
  the tutorial's step list.
- No `short` contains a newline, and none exceeds a length cap.
- The disabled-reason path renders both strings, reason first, when both are present.
- The two multi-reason call sites (Apply, Folder Cleanup) show the correct reason for each condition,
  and specifically show the "nothing selected" reason rather than the operation-in-progress one.
- A missing resource, or malformed JSON, throws at initialisation with a message naming the resource.

## Out of scope

- Localisation. The schema does not preclude it, and nothing here assumes English, but no
  translation mechanism is built.
- Rewording the disabled-reason strings.
- Tooltips on tab headers themselves.
