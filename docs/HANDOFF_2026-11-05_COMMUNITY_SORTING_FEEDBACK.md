# Handoff: community feedback on gear, body and hair sorting (2026-11-05)

A research report, not an implementation. Nothing in the plugin was changed. It collects four
Discord threads from 0.6.3.0 testers, checks each against the code as of `0a5ddbd` (release
0.6.3.0), and recommends what to build. Read this first, then the files it points to.

Akako (the plugin author) has said more than once that they don't want to add more buttons or
options. Every recommendation below tries to fit within the controls that already exist.

## Priority order

1. **Fix the mixed `Gear/` level** when the slot and body splits are both on (section 1). This is a
   real layout bug that showed up on a real library.
2. **Zero-pad hair numbers to 3 digits** (section 3). Small change, with a one-time folder move.
3. **Split Body mods by part** (Skeletons / Physics / Hands / Feet / the rest) (section 4). Uses data
   the scan already has.
4. **Kerrigain's "Single Slot" folder** (section 2). It already works with a template; build it into
   the plugin only if section 1 doesn't make it unnecessary.
5. **Detect the preferred body automatically** (section 5). Nice to have.

Out of scope: writing Penumbra collection priorities or inheritance (section 6).

---

## 1. The `Gear/` folder mixes body folders, slot folders and loose mods

**Reported by:** Sopheline. Akako confirmed it on their own library ("these folders now are mixed in
with the body type stuff and it gets confusing").

### What the code does

`ModTypeFolders.GetFolder(category, subCategory, bodyType)`
(`PenumbraOrganizer.Plugin/Organizer/Classification/ModTypeClassifier.cs:35`) puts the body segment
directly after the category and above the slot. With both "Split gear by equipment slot" and "Split
gear by body type" on (`Windows/SortPanel.cs:244` and `:288`):

| Mod | Lands in |
|---|---|
| Single slot, body found | `Gear/LavaBod+/Legs/<mod>` |
| Single slot, no body found | `Gear/Legs/<mod>` |
| Several slots, body found | `Gear/LavaBod+/<mod>` |
| Several slots, no body found | `Gear/<mod>` |

So `Gear/` directly contains body folders (Artemis, Bibo+, …), slot folders (Ears, Feet, Hands, …) and
loose mods, all sorted alphabetically together.

The slot folders don't all mean the same thing either. `BodyTypeApplies` (`ModTypeClassifier.cs:202`,
using `BodyShapedSlots` at `:178`) only gives a body to gear that touches Top, Hands, Legs or Feet.
Head, Ears, Neck, Wrists and Rings never get one. So:

- `Gear/Head` holds **every** head-only mod.
- `Gear/Legs` holds only the legs mods where **no body was found**.

Akako's reply on Discord ("legs, hands, head and top are single-slot mods with no clear body shape")
missed this detail.

With only one of the two checkboxes on, the tree is clean. The problem is only the combination.

### Options

1. **Give the no-body gear its own folder (recommended).** Gear with no body found goes to
   `Gear/<bucket>/…`, so everything directly under `Gear/` is a body folder. Don't call it "Vanilla":
   no body found usually means the mod doesn't *say* which body it's for, not that it fits vanilla.
   Name ideas: `No body`, `Any body`, `Unspecified body`. Questions to settle:
   - Do the five non-body slots (Head/Ears/Neck/Wrists/Rings) go in the bucket too? They should, so
     the rule stays simple: anything directly under `Gear/` is a body.
   - Should the bucket only appear when *both* splits are on? With only the body split on, the loose
     no-body mods in `Gear/` mix with body folders too, so probably whenever the gear body split is on.
2. **Kerrigain's "Single Slot" folder** (section 2): `Gear/Single Slot/Legs` and
   `Gear/LavaBod+/Single Slot/Legs`. Less clutter, but body folders and loose sets would still be mixed.
3. **Number prefixes** (`00 …`, `01 …`), which Akako had floated. It sorts the folders but doesn't
   explain what they are, and renames lots of folders once.
4. **Sopheline's idea: sort gear by body only and let people filter by slot.** She says Penumbra can
   filter with `s:legs`. I haven't checked that; check Penumbra's mod filter before repeating it. The
   plugin's own Search tab already filters by equipment slot. The argument is strong: the body is the
   one thing a filter can't show you, and the slot is something it can. The minimum version is just
   changing the help text to recommend body split *or* slot split.

Recommended: option 1, plus the help-text change from option 4.

### Where the change goes

- `ModTypeFolders.GetFolder` (both overloads, `ModTypeClassifier.cs:35` and `:65`). It enumerates
  valid `(category, subCategory)` pairs explicitly and throws on anything else. Keep that.
- `SortFolderSelectors.Select` / `TypeFolder` (`Organizer/SortFolderSelectors.cs:34`, `:112`). This is
  the single place both the Sort tab and the template fallback use, so a change here reaches
  templates automatically.
- Template `folderLabels` keys written against today's `Gear/Legs` paths would stop matching moved
  folders. Mention it in the release notes.
- Tests: `PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateSortBodyTests.cs`,
  `ModTypeClassifierTests.cs`.
- Docs: `docs/HOW_SORTING_WORKS.md` (body type section and the strategy tables), the Help text
  (`Windows/HelpTopics.cs`), `docs/USER_GUIDE.md`.
- Existing users who already applied a sort will see every no-body gear mod move once.

---

## 2. Kerrigain: `Gear/Single Slot/<slot>`

**Request:** group the nine slot folders under one folder so a single box in the Protect tab can
hide all single-slot gear.

**Facts:** with the slot split on, a mod only gets a slot folder when exactly one slot resolved
(`ModTypeClassifier.EnrichGearSubCategory`, `:137`). Full sets, multi-slot mods and unreadable mods
stay loose in `Gear/`. So the slot folders already *are* the single-slot mods; the name is accurate.

**Already possible with no code.** A template's `folderLabels` renames the folders the sort produces
(`Organizer/Templates/TemplateFolderLabels.cs`, applied in `SortFolderSelectors.TypeFolder` before
the creator segment is added, so "Type then creator" works too). A template with no entries just runs
its fallback sort with the renames. Written against `TemplateCodec.Validate` but **not yet tested in
game**:

```json
{
  "formatVersion": 1,
  "name": "Single Slot gear",
  "fallbackStrategy": "TypeOnly",
  "fallbackSplitGear": true,
  "folderLabels": {
    "Gear/Head": "Gear/Single Slot/Head",   "Gear/Top": "Gear/Single Slot/Top",
    "Gear/Hands": "Gear/Single Slot/Hands", "Gear/Legs": "Gear/Single Slot/Legs",
    "Gear/Feet": "Gear/Single Slot/Feet",   "Gear/Ears": "Gear/Single Slot/Ears",
    "Gear/Neck": "Gear/Single Slot/Neck",   "Gear/Wrists": "Gear/Single Slot/Wrists",
    "Gear/Rings": "Gear/Single Slot/Rings"
  }
}
```

Limits of the template route:
- No in-plugin editor for `folderLabels`: `Windows/MainWindow.TemplatesExport.cs:400` passes an empty
  map on purpose. Users have to write the JSON by hand.
- With the body split on, the folders are `Gear/Bibo+/Hands`, and these keys won't match them.
- Face and Skin in Kerrigain's list aren't gear slots; they're their own top-level categories.

**Akako's Discord suggestions have a catch.** Dragging mods into hand-made folders, or editing a
workbook and importing it, only lasts until the next Sort, which moves them back to `Gear/Hands`
unless those folders are protected. A template can be applied again after every sort.

**If built in, no new button:** turn the "Split gear by equipment slot" checkbox into a three-way
dropdown (Off / By slot / By slot, grouped under "Single Slot"). The folder change is one arm of
`GetFolder`. The work is passing the new value through `SortFolderSelectors`, `SortPanel`,
`Configuration`, the template fallback (a new field next to `fallbackSplitGear` in
`Organizer/Templates/TemplateModels.cs`), and the tests. About the size of the hair split. The body
split composes naturally: `Gear/Bibo+/Single Slot/Hands`.

Decide section 1 first. If no-body gear moves into its own bucket, this may no longer be needed.

---

## 3. Sopheline: zero-pad hair numbers (`2` → `002`)

**Problem:** Penumbra sorts folder names as text, so `Hair/` lists `185, 2, 4, 53, 55`.

**Facts:**
- `IsHairNumber` (`ModTypeClassifier.cs:88`) rejects leading zeros **on purpose**. Its comment says
  this stops a `Hair/0115` folder appearing next to an existing `Hair/115`. Padding means changing
  that rule together with the format, in one place.
- `ModHairFileReader` accepts 1..9999 (`MaxHairNumber`, `ModHairFileReader.cs:19`).

**Recommendation:** pad to 3 digits.
- Numbers 100 and up don't change; only hairs 1–99 move, once, on the next sort and Apply.
- I believe vanilla hair numbers stay under 1000, but **check that**. A four-digit number would sort
  wrong again (`1000` before `999`). Either confirm none exist, or decide what to do with them.
- Format at folder-build time (`GetFolder` / `SortFolderSelectors`), not in the stored
  `SubCategory`, so the diagnostic dump and hair priority logic keep plain integers.

**Knock-on effects (put them in the release notes):**
- Template `folderLabels` keys like `Hair/2` stop matching.
- Workbooks exported before the change use the old names.
- A hair mod protected at `Hair/2` stays there, next to the new `Hair/002`.

Akako also mentioned numbering other folders. Each folder renamed that way moves the same way, once.

---

## 4. Sazas: a "Core" layout for body mods

Sazas shared their own layout (Core Base −50, Core Shapes −45, Core Materials −40, Core Extensions
−35, Core Skeletons −30, Core Physics −25, Core Scaling −20, Subsurface Scattering −20; the numbers
are Penumbra mod priorities they set by hand), plus "keep body replacers away from gear" and
"Gear → Rue → Creator".

| Part | Status | Notes |
|---|---|---|
| Body replacers kept apart from gear | **Already done** | `Body/` is its own top-level category. |
| Gear → Rue → Creator | **Already done** | "Type then creator" + "Split gear by body type" gives `Gear/Rue+/<Creator>/<mod>`. |
| Skeletons | **High** | Keys look like `Customization: Au Ra Female Body (Skeleton) 1`. `ChangedItemKeyParser` already reads the `(Skeleton)` part into `Subtype` (`ChangedItemKeyParser.cs:52`, test at `ChangedItemKeyParserTests.cs:111`). `MapBodyPart` (`ModTypeClassifier.cs:359`) treats it all as `Body`. No disk read needed. |
| Physics | **High** | Same mechanism, with `(Physics)`. |
| Extensions (hands / feet) | **High** | A Body mod whose only bare-body item is "The Emperor's New Gloves" is hands, "Boots" is feet, both is hands and feet. Already in the changed items (`KnownEquipmentPlaceholders`, `:122`). |
| Scaling | **Medium** | Viera Minimum Size-style mods change racial scaling ("Rsp" entries in the mod config). `ModConfigFileWalker` already walks those. Unknown what changed items Penumbra reports for them; they may land in `Review/` today. |
| Base vs Shape | **Not detectable** | Bibo+ and Neolithe are both full-body replacers. Bibo+ is "Base" only because of how Sazas uses it. Use one bucket for full bodies. |
| Materials / SSS | **Low** | Material/texture-only body mods look just like skin texture mods. Leave them alone. |

**Before building:** no test fixture or diagnostic dump in the repo includes a real skeleton or physics
mod. Check YAS, Skelomae and Tiramisu in a real diagnostic dump first, to confirm the keys look as
expected.

**Design question:** `GetFolder` puts the body type straight after the category (`Body/Rue+`). A part
split has to choose an order. Likely `Body/Shapes/Rue+` and `Body/Skeletons`, with the body type only
meaningful for full bodies and hands/feet. To avoid another button, consider making this part of the
existing "Split body mods by body type" checkbox, or replacing it with a dropdown.

---

## 5. Detect the preferred body automatically

**Asked by:** Sopheline ("does it auto-set a preferred body? is that even possible?").

**Now:** the First-choice dropdown (`SortPanel.cs:396`) plus the ranked list below it only decide
between bodies that **one mod names** (a "Rue/YAB" mod goes to whichever ranks higher). A mod that
names one body isn't affected. Akako's Discord answer was right.

**Possible, not built:**
- The plugin never reads collections; it calls only `GetModList`, `SetModPath`, `GetModDirectory` and
  `RedrawAll` (`Organizer/Operations/PenumbraOperationsAdapter.cs`, `Plugin.cs`).
- Penumbra lets plugins *read* collection settings. The plugin could look at the active collection,
  take the enabled Body mods, and pick the matched body of the highest-priority one.
- Sazas's setup is a good test: Bibo+ at −50 under Neolithe at −45 should give Neolithe.
- Complications: several collections, inherited settings, body mods that match no body.
- It's read-only, so low risk.
- To avoid a new button: fill in the default First-choice body once (first run, or when the user has
  never changed it), and let the user override it.
- Check the exact Penumbra API calls for reading collection settings before planning this.

---

## 6. Out of scope: writing mod priorities or inheritance

Sazas's −50…−20 priorities are per-collection Penumbra settings. Writing them would change what the
game renders, would need the user to pick a collection, and isn't covered by Rollback (which only
knows mod paths). That would be a different product. The "01 - Core Base" numbering is just folder
names, which a template's `folderLabels` can already produce.

---

## Things to verify that I couldn't

- That the section 2 template JSON imports and sorts as expected (no .NET SDK was available).
- Whether Penumbra's mod filter really supports `s:legs` (section 1, option 4).
- Whether any vanilla hair number is 1000 or more (section 3).
- The changed-item keys of real skeleton, physics and scaling mods (section 4).
- The Penumbra API calls for reading collection settings (section 5).
