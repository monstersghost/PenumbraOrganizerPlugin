# Sort control consolidation

Date: 2026-08-07
Part of `2026-08-07-ui-overhaul-umbrella-design.md` (piece 2 of 6, built after piece 0)

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

**Every legacy combination maps exactly to the method its old button called.** That is the regression
boundary, and it is what the tests pin.

| Old button | Group by | Gear split | NPC split |
|---|---|---|---|
| By Creator | Creator | n/a | n/a |
| By Mod Type | Mod type | off | on |
| By Mod Type Detailed | Mod type | on | on |
| By Type Then Creator | Type then creator | off | on |
| By Type Then Creator (Detailed) | Type then creator | on | on |
| By Creator Then Type | Creator then type | off | on |
| By Creator Then Type (Detailed) | Creator then type | on | on |

### The new combinations, and the work they actually cost

An earlier draft of this spec said both "no sort logic changes" and "two combinations are new". Both
were wrong, and the second undercounted by a factor of three.

The selection space is **1 + (3 strategies × 2 gear × 2 NPC) = 13**. Seven exist as buttons today.
**Six are new**, all of them the NPC-split-off column:

| Group by | Gear split | NPC split | Exists? |
|---|---|---|---|
| Mod type | off / on | **off** | new (2) |
| Type then creator | off / on | **off** | new (2) |
| Creator then type | off / on | **off** | new (2) |

`OrganizerState` exposes exactly **seven** `SortBy*` methods, every one of them with NPC subdivision
hard-on. Delivering the design therefore requires one of:

- **six new public methods** (`SortByModTypeNpcFlat`, `SortByTypeThenCreatorNpcFlat`,
  `SortByCreatorThenTypeNpcFlat`, each in gear-flat and gear-detailed form), or
- **reparameterising the sort entry point** to take `(strategy, splitGear, splitNpc)` and collapsing
  the existing seven into it.

The second is cleaner and is what this spec chooses, but it is **a refactor of `OrganizerState`'s
public surface**, not "the mirror of `FlattenGearSubCategory`". Any plan written from this spec must
scope it as such. The NPC-flattening helper itself is trivial; the API change around it is not.

`By Mod Type Detailed` also appears in `docs/USER_GUIDE.md` as its own strategy, and the guide says
"Five buttons compute a proposed folder path". **This piece updates the guide**; leaving it stale
would make the shipped documentation wrong on the day of release.

### One claim about current behaviour, corrected

NPC subdivision is on for every strategy **except By Creator**. `SortByCreator` passes `null` as the
secondary and never calls `TypeFolder`, so it produces no `NPC/` folder at all. That is why the
mapping table reads `n/a` for that row and why both checkboxes are disabled when Group by is Creator.

### Where the code goes

The sort panel moves to `Windows/SortPanel.cs`, drawing into the Sort tab. `MainWindow` keeps tab
dispatch and shared state.

**The panel's signature is the load-bearing detail**, and an earlier draft left it out. It needs
everything the current sort block reaches for:

```csharp
internal sealed class SortPanel            // instance, not static: it owns cross-frame state
{
    private int _strategyIndex;            // ImGui.Combo needs a mutable int that survives frames
    private bool _splitGear;
    private bool _splitNpc;
    private bool _scrapedNpcList;          // piece 1's checkbox lives here too

    public void Draw(
        OrganizerState state,
        ActivityGates gates,               // must not be dropped: see below
        Func<string, string> canonicalizeCreator,
        FileDialogManager fileDialogs);    // Import Workbook still lives on this tab
}
```

**The gate must not be lost.** The current sort block is wrapped in
`ImGui.BeginDisabled(!gates.CanStageProposals)` with a trailing
`IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)` tooltip. Dropping it makes Sort clickable while
an Apply or Restore is in flight or recovery is pending, which is a correctness regression, not a
cosmetic one.

**Cross-frame state has an owner.** `ImGui.Combo(ref int)` and `Checkbox(ref bool)` need backing
storage that persists between frames. `SortPanel` is a single instance held by `MainWindow`, not a
static and not constructed per frame; a per-frame instance resets the selection every frame and the
control is unusable.

The selection itself is a value type so dispatch can be tested without ImGui:

```csharp
readonly record struct SortSelection(SortStrategy Strategy, bool SplitGear, bool SplitNpc)
{
    public bool SplitsApply => Strategy != SortStrategy.CreatorOnly;
}
```

**A new `SortStrategy` enum, not `OrganizationStrategy`.** The existing enum has seven members, three
of which (`StartManually`, `PreserveAndClean`, `Custom`) are meaningless here but representable, so a
"pure function tested exhaustively" would have no defined behaviour for a third of its input space.
It is also the workbook-export dropdown's type, which would couple two unrelated controls. A
four-member `SortStrategy` makes the illegal states unrepresentable.

**The combo index is not the strategy.** Mapping index to strategy through array position is the
exact hazard `MainWindow.cs:81-83` already carries a warning comment about. Dispatch goes through an
explicit switch on `SortStrategy`, and the combo's item order is derived from the enum rather than
duplicated.

**Import Workbook is restructured, not untouched.** It is the eighth element of the same
`DrawWrappingButtonRow` call as the seven sort buttons, sharing that row's `BeginDisabled` scope and
its trailing tooltip. Removing seven entries necessarily changes the control that draws it and which
widget `IsItemHovered` refers to. Its *behaviour* is unchanged; the claim that it is untouched is not
achievable and has been removed from the out-of-scope list.

### Two ImGui details that silently fail if missed

**The Sort button needs a stable ID.** A dynamic label like `Sort 2,242 mods` makes the widget ID a
function of the mod count, so if a background scan publishes between frames while the button is held,
the active ID no longer matches and the click is dropped with no error. The label carries an explicit
`##sort-mods` suffix. Separately, the count must be **unprotected** mods, since `Sort()` only touches
those; `Mods.Count` would overstate what the button does, which is the opposite of the intent.

**Disabled-checkbox tooltips need `AllowWhenDisabled`.** A plain `IsItemHovered()` returns false for
items inside `BeginDisabled`, so the tooltip explaining why the checkboxes are greyed out would never
appear — the one case it exists for. Every other disabled tooltip in `MainWindow.cs` already passes
`ImGuiHoveredFlags.AllowWhenDisabled`.

### The staleness the stateful control introduces

The seven buttons were stateless: Review Changes always corresponded to the last button pressed. The
new control has state, so a user can sort, then flip a checkbox, and the panel now describes a
selection that does **not** describe the staged proposals.

This is a real regression in a plugin whose contract is that you can trust what you are looking at.
The panel tracks the selection that was last sorted with, and when the current selection differs it
shows a single line under the Sort button: **"Selection changed since the last sort."** No colour
alarm, no auto-sort, no blocking. Cleared when Sort is pressed or the selection returns to match.

### Dependency on piece 3

The disabled-checkbox tooltip needs the shared tooltip mechanism, which piece 3 defines. Rather than
write an inline literal to be swept up later — with no test that would catch it being missed — **the
schema and loader from piece 3 move to the front of this piece**, and piece 3 becomes the sweep
across the remaining ~30 controls. The umbrella records this.

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
