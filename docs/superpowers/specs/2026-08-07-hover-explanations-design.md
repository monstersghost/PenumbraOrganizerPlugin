# Hover explanations

Date: 2026-08-07
Part of `2026-08-07-ui-overhaul-umbrella-design.md` (piece 3 of 5)

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
  ids. Without that, the indirection silently degrades to blank tooltips and nobody notices for a
  release or two. This test is the reason the indirection is affordable.
- **Unused topics are allowed** and are not a failure: the Help tab has sections with no
  corresponding control.

### Coexisting with the disabled-reason tooltips

The nine "Another operation is in progress" tooltips answer a different question, and it is a more
urgent one: the user just tried to click something and it did not respond.

When a control is both disabled and has a topic, the tooltip shows **both**, reason first:

```
Another operation is in progress or requires recovery.

Applies every proposed path change to Penumbra.
```

The disabled reason is not moved into `help-content.json`. It is state, not documentation, and it is
generated (`ActivityGates` already produces these strings).

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

Deliberately **not** covered: Cancel, Yes/No confirmation buttons, and anything inside a modal whose
own text already explains the choice. A tooltip on "Cancel" is noise.

### Where the code goes

`Windows/Help.cs` holds the loader and the two entry points:

```csharp
static string Short(string id);
static void Tooltip(string id, string? disabledReason = null);
```

The loader parses the embedded resource once at construction. A parse failure is a packaging bug, not
a runtime condition, and throws with a clear message — the same treatment
`NpcNameListStore.Load` already gives its bundled seed.

## Testing

- **Every id referenced in code resolves.** Ids are collected by reflection over a `HelpTopics`
  constants class, so the set is enumerable rather than scraped from source text. Each must resolve.
- Every topic has a non-empty `short`.
- No `short` contains a newline, and none exceeds a length cap (a soft guard against tooltips growing
  into paragraphs).
- The disabled-reason path renders both strings, reason first, when both are present.
- A missing resource, or malformed JSON, throws at construction with a message naming the resource.

## Out of scope

- Localisation. The schema does not preclude it, and nothing here assumes English, but no
  translation mechanism is built.
- Rewording the disabled-reason strings.
- Tooltips on tab headers themselves.
