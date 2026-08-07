# Sort control consolidation

Date: 2026-08-07
Part of `2026-08-07-ui-overhaul-umbrella-design.md` (piece 2 of 5, built first)

## The problem

The Sort tab presents **seven** buttons:

```
By Creator
By Mod Type                        By Mod Type Detailed
By Type Then Creator               By Type Then Creator (Detailed)
By Creator Then Type               By Creator Then Type (Detailed)
```

That is not seven strategies. It is **four strategies crossed with one modifier**, enumerated by
hand. `By Creator` has no Detailed form because it never uses the mod's category at all.

Enumerating a cross product as buttons has the usual costs: the shared structure is invisible, the
row grows every time either axis gains a member, and the naming has already drifted (`By Mod Type
Detailed` versus `By Type Then Creator (Detailed)`).

### What "Detailed" actually means today

Verified in `OrganizerState`: the non-detailed sorts route through `FlattenGearSubCategory`, which
discards the subcategory **only for Gear**:

```csharp
private static string? FlattenGearSubCategory(ModCategory? category, string? subCategory) =>
    category == ModCategory.Gear ? null : subCategory;
```

So today:

- **Detailed controls gear only.** It decides between `Gear` and `Gear/Head`, `Gear/Feet`, etc.
- **NPC subdivision is always on.** `NPC/NPCs`, `NPC/Enemies`, `NPC/Bosses` happen under every
  strategy, detailed or not, and there is **no way to turn it off**.
- Animation and VFX subcategories are likewise always on.

The label "Detailed" therefore promises more than it delivers, and the one subdivision users have
asked to control is the one they cannot reach.

## Design

### The control

Replacing the seven buttons:

```
Sort mods into folders

  Group by       [ Creator then type      v ]

  [x] Split gear by equipment slot
  [x] Split NPC mods by kind

              [ Sort 2,242 mods ]

  Nothing moves until you press Apply on Review Changes.
```

- **Group by** is a combo with the four existing strategies: Creator, Mod type, Type then creator,
  Creator then type.
- **Split gear by equipment slot** replaces "Detailed". Checked gives `Gear/Feet`; unchecked gives
  `Gear`.
- **Split NPC mods by kind** is new control over existing behaviour. Checked gives `NPC/Bosses`;
  unchecked gives `NPC`.
- **Sort** applies the current selection. It names the count so the button says what it will do.

Both checkboxes are **disabled when Group by is Creator**, with a tooltip explaining that grouping
by creator alone never uses the mod's type. This is honest about the existing gap rather than
silently ignoring the settings.

### Defaults, and why they are what they are

| Setting | Default | Reason |
|---|---|---|
| Group by | Type then creator | The current workbook dropdown already defaults to this |
| Split gear by slot | **off** | Matches `By Mod Type`, the non-detailed form, which is the behaviour a user gets today by pressing the plainly-named button |
| Split NPC by kind | **on** | Preserves today's behaviour exactly, since there is currently no way to turn it off |

The NPC default matters: defaulting it off would silently reorganise every existing user's NPC mods
the first time they sorted. Adding the control must not change what the control does by default.

Selections are held in memory for the session and are **not persisted**. A sort is an explicit act
whose result is visible on Review Changes before anything happens; remembering the last choice would
add config surface for no real gain. This is a deliberate scope decision, easy to revisit.

### An explicit Sort button, not sort-on-change

Changing the dropdown does **not** re-sort. Sorting overwrites every unprotected mod's proposed path,
including hand-assigned ones, and the plugin's whole contract is that nothing changes until you ask.
A dropdown that silently discarded staged work on selection would break that contract in the least
visible way possible.

### Behaviour mapping

Every existing button maps to a selection, and every selection maps to an existing sort method. No
sort logic changes.

| Old button | Group by | Gear split | NPC split |
|---|---|---|---|
| By Creator | Creator | n/a | n/a |
| By Mod Type | Mod type | off | on |
| By Mod Type Detailed | Mod type | on | on |
| By Type Then Creator | Type then creator | off | on |
| By Type Then Creator (Detailed) | Type then creator | on | on |
| By Creator Then Type | Creator then type | off | on |
| By Creator Then Type (Detailed) | Creator then type | on | on |

**Two combinations are new**: NPC split off, with gear split either way. Those need a sort path that
flattens NPC subcategories, which does not exist yet. It is the mirror of `FlattenGearSubCategory`
and is the only new sort logic in this piece.

`By Mod Type Detailed` also appears in the guide as its own strategy; the user guide is updated to
describe the dropdown and the two checkboxes instead.

### Where the code goes

The sort panel moves to its own file, `Windows/SortPanel.cs`, drawing into the Sort tab.
`MainWindow.cs` is around 2,000 lines and is the largest file in the project; four features are
landing on it in this overhaul. `MainWindow` keeps tab dispatch and shared state and calls
`SortPanel.Draw(...)`.

The selection itself is a small value type so it can be tested without ImGui:

```csharp
readonly record struct SortSelection(OrganizationStrategy Strategy, bool SplitGear, bool SplitNpc)
{
    public bool GearSplitApplies => Strategy != OrganizationStrategy.CreatorOnly;
    public bool NpcSplitApplies  => Strategy != OrganizationStrategy.CreatorOnly;
}
```

Dispatch from selection to the `OrganizerState` sort method is a pure function, tested exhaustively
against the mapping table above.

## Testing

- **Every row of the mapping table**: the selection dispatches to the sort method that button used to
  call. This is the regression guard for the whole change.
- **The two new combinations** produce `NPC` rather than `NPC/Bosses`, with gear split honoured
  independently.
- **Creator-only disables both checkboxes**, and a selection carrying them still sorts identically to
  the old `By Creator`.
- **Sorting is not triggered by changing the dropdown or a checkbox** — only by the button. Asserted
  through the pure selection type plus a panel-level test that no sort method is invoked on change.
- Gear split off leaves `Gear`; on gives `Gear/<slot>`. NPC split off leaves `NPC`; on gives
  `NPC/<kind>`. These are the two axes, tested independently.

## Out of scope

- Persisting the selection between sessions.
- Any change to what the four strategies compute.
- Any change to classification, including the short-name false positives covered in the NPC list
  spec.
- The Import Workbook, NPC refresh and manual assignment controls elsewhere on the Sort tab. They are
  untouched here; the tab's overall layout is revisited only as far as the sort block itself.
