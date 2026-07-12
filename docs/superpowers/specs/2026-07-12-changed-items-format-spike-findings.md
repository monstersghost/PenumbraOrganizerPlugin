# `GetChangedItems` format spike — findings

Gathered per Task 15 of `docs/superpowers/plans/2026-07-12-plugin-organizer-phase1a-1b.md`, via a
temporary `[SPIKE]` button on the Scan tab that called `GetChangedItems` for the first 10 mods
(sorted by name) and logged each mod's changed-item keys to the in-window event log.

**Client UI language at time of testing:** English. All observed strings (`Customization`, `Skin
Textures`, `Face`, `Player`, `Vest`, `Jacket`, `Shoes`, etc.) are English; no localization was
exercised by this spike.

**Transcription note:** the event log has no text wrap and its lines aren't selectable/copyable
(see UX note below), so capturing the full lines required stretching the window to full 4K width
and reading a very dense screenshot. The overall *shape* of each key (the `Customization: ...`
prefix pattern vs. bare item names, and the absence of any slot/comma structure) is unambiguous and
consistent across every line. Exact-duplicate-vs-slightly-different token lists across the three
near-identical "Akako's Files 3.1.1" entries could not be verified with full digit-level confidence
from the screenshot — flagged inline below where relevant. This does not affect the finding's
conclusion, which rests on the format shape, not the specific customization IDs.

## Raw key strings observed (10 mods, alphabetical by name)

1. **`,Thor - K.D.O`**: `Faerie Tale Prince's Vest | Street Jacket`
2. **`Air Force 1 - by Solona`**: `Calfskin Rider's Shoes`
3. **`Akako 4.0a`**: `Customization: Midlander Female Skin Textures | Customization: Miqo'te Female Face 101 | Customization: Miqo'te Female Skin Textures | Customization: Miqo'te Female Tail (Etc) 3 | Customization: Miqo'te Female Tail 3 | Customization: Unknown`
4. **`Akako 4.0a dt`**: `Customization: Midlander Female Skin Textures | Customization: Miqo'te Female Face 101 | Customization: Miqo'te Female Skin Textures | Customization: Miqo'te Female Tail (Etc) 3 | Customization: Miqo'te Female Tail 3 | Customization: Miqo'te Male Skin Textures | Customization: Unknown`
5. **`akako skin`**: `Customization: Midlander Female Skin Textures | Customization: Player Skin Textures`
6. **`Akako's Files 3.1.1`** (1st of 3 same-named installs): `Customization: Midlander Female Hair 157 | Customization: Midlander Female Skin Textures | Customization: Miqo'te Female Face (Etc) 1 | Customization: Miqo'te Female Face (Etc) 101 | Customization: Miqo'te Female Face (Iris) 101 | Customization: Miqo'te Female Face 101 | Customization: Miqo'te Female Hair 115 | Customization: Miqo'te Female Hair 157 | Customization: Miqo'te Female Skin Textures | Customization: Miqo'te Female Tail (Etc) 3 | Customization: Miqo'te Female Tail 3 | Customization: Player Skin Textures`
7. **`Akako's Files 3.1.1`** (2nd): same key *shape* as #6, with `Customization: Miqo'te Male Skin Textures` additionally present (low confidence on exact full list — see transcription note)
8. **`Akako's Files 3.1.1`** (3rd): same key shape as #6/#7 (low confidence on exact full list — see transcription note)
9. **`Akako's Glowy Eyes`**: `Customization: Miqo'te Female Face (Iris) 101 | Customization: Unknown`
10. **`Akako's Head`**: `Customization: Midlander Male Skin Textures | Customization: Miqo'te Female Face (Etc) 101 | Customization: Miqo'te Female Face (Iris) 101 | Customization: Miqo'te Female Face 101 | Customization: Miqo'te Female Skin Textures | Customization: Unknown`

User-provided context: the Akako mods are older, pre-Dawntrail-graphics-update mods, which the user
believes is why some of their customization keys resolve to `Unknown`. `Air Force 1 - by Solona` is
a Dawntrail-updated gear mod. This correlates with — but per this spike's data isn't fully separable
from — the more obvious split: **mod type** (equipment/gear vs. character customization) drives which
of the two key shapes below applies, independent of when the mod was authored.

## Did the `"{Slot}, {Item name}"` convention hold?

**No — none of the 10 mods produced a key matching that convention.** Two distinct shapes were
observed instead, and neither has a slot/comma structure:

- **Customization-type mods** (Akako's Head, Akako's Glowy Eyes, Akako 4.0a, Akako 4.0a dt, akako
  skin, Akako's Files 3.1.1 ×3 — 8 of 10 sampled mods): every key is prefixed `Customization: `,
  followed by a free-form description shaped roughly like `{Race} {Gender} {BodyPart}[ (Subtype)][
  Number]` (e.g. `Miqo'te Female Face (Iris) 101`), or literally the string `Unknown` when the game
  couldn't resolve a human-readable name for that customization slot.
- **Equipment/gear mods** (Air Force 1 - by Solona, `,Thor - K.D.O` — 2 of 10 sampled mods): keys
  are bare item display names with no prefix and no slot indicator at all (`Calfskin Rider's
  Shoes`, `Faerie Tale Prince's Vest`, `Street Jacket`).

No slot name appears anywhere as an explicit, separately-delimited token in either shape. Any
Phase 1c classifier keying off "slot" would need a different signal entirely (likely the equipped
item's own slot metadata via a different IPC call, not string-parsing this key). This 10-mod sample
also only covered two `ModCategory` buckets (customization/Face-adjacent, and generic Gear) — mounts,
minions, VFX, sound, and animation mods were not sampled and may introduce further shapes.

## Recommendation

**The format does not hold as the plan assumed — do not proceed with a Phase 1c parser built on the
`"{Slot}, {Item name}"` convention.** A different classification approach is needed (e.g. deriving
category from Penumbra's own item/slot metadata rather than string-parsing `GetChangedItems` keys,
or a lookup table keyed on the two observed shapes plus further sampling across untested
`ModCategory` buckets). Per this task's scope, no fallback approach is implemented here — that
needs a separate brainstorming pass before any Phase 1c plan is written.

## Related UX note (not part of this spike's scope)

While capturing this data, testing surfaced that the Scan tab's event log (`MainWindow.cs`
`DrawScanTab`, `ImRaii.Child("EventLog", ...)`) renders each line via `ImGui.TextUnformatted` with
no wrapping and no way to select/copy the text — long lines (like these spike results) can only be
read by manually stretching the whole plugin window. Worth revisiting if the event log is expected
to carry long diagnostic strings in the future; out of scope for this plan.
