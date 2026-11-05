# Manual check: tool-managed mods (Heliosphere and Proteus modes)

Build, load in game (dev plugin path: the MAIN checkout's bin\Debug), scan. You need at least one
Proteus-made mod, or hand-edit a test mod's meta.json to `"Author": "Proteus"` (exact case) and
rescan. The managed mod needs a mod in a directory named `Proteus` with that author.

## Defaults and detection
- [ ] On a fresh config both tools are Protected: Review shows no moves for their mods after Sort.
- [ ] The Protect tab shows "Heliosphere mods (N)" and "Proteus mods (N)" dropdowns only while that
      tool has mods, each defaulting to Protected, and a "(Proteus)" / "(Heliosphere)" note beside a
      protected tool mod.
- [ ] A mod whose author is `proteus` (lower case) is NOT treated as a Proteus mod.
- [ ] The export dump has a `ManagedBy:` line per mod and the diagnostic header counts each tool.

## Modes
- [ ] Proteus -> "Sort inside their folder", Sort: a mod in `Proteus Upscales` proposes
      `Proteus Upscales/Gear/.../Name`; a Proteus mod already in `Gear` proposes the normal path.
- [ ] Proteus -> "Treat as normal": Proteus mods sort like any other mod.
- [ ] Restart the game: the chosen modes persist. Rescan: the modes persist.
- [ ] Untick one protected Proteus mod: it is unprotected until the next scan, which protects it again
      (the mode is unchanged).
- [ ] Heliosphere -> "Sort inside their folder" with a HS mod in a folder of its own: it stays under
      that folder. HS mods already in Gear/Body/NPC sort normally.

## The managed mod
- [ ] The `Proteus` managed mod is protected whichever mode Proteus mods use.
- [ ] Tick "Place the Proteus managed mod at the root", Sort, Review: it proposes the top level
      (just `Proteus`). Untick it: protected again.
- [ ] After applying that move, Proteus still works (overlays still apply).

## Heliosphere and Proteus keep working
- [ ] After sorting Proteus mods inside their folder, make a new refit in Proteus: it still files into
      its usual folder and everything keeps working.
- [ ] After sorting an HS mod inside a folder, run an HS update on it: the update works and the mod
      keeps the path it was filed at.
