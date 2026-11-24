# Heliosphere/Proteus new-mod notice and Sort tab status line - in-game checklist

Build: `feat/tool-mode-notices`. Keep a copy of `pluginConfigs/PenumbraOrganizer.Plugin.json` before starting. Nothing here has been seen in game yet: every item below is unverified until ticked. The texts and decisions are unit-tested; the popup, buttons, colours and tab switching are not.

Background: Heliosphere installs new mods at the library root. With its Protect tab mode at Protected (the default) the old build showed nothing at all (only a log line). No default changes in this build: everything follows the Protect tab dropdowns.

New Heliosphere mod, Protected (default)
- [ ] Protect tab: Heliosphere mods = Protected. Install one mod through Heliosphere. After a few seconds a popup titled "New tool-managed mods" says: "1 new Heliosphere mod added. Heliosphere mods are Protected, so it stays where it is. You can change this on the Protect tab." (yellow), with **Open Protect tab**, **OK** and **Don't show this popup**; there is no Sort now.
- [ ] **Open Protect tab** closes the popup and opens the main window (if it was closed) on the Protect tab.
- [ ] Install another: **OK** closes it; the window's X closes it too. Nothing moved in Penumbra.
- [ ] Install two Heliosphere mods close together: one popup says "2 new Heliosphere mods added ... so they stay where they are."
- [ ] The Dalamud log line now ends "(N Heliosphere and 0 Proteus kept in place by their Protected mode)."

Mixed batch
- [ ] Add one ordinary mod (drag a .pmp into Penumbra) and install one Heliosphere mod within a couple of seconds of each other. One popup: "1 new mod is loose in the root. Sort it?" plus the yellow line "Also added: 1 Heliosphere mod. It is Protected, so it stays where it is. You can change this on the Protect tab." and an extra **Open Protect tab** button beside Not now. No second popup after answering.
- [ ] **Open Protect tab** there switches the main window to Protect and leaves the question open.

Other modes
- [ ] Protect tab: Heliosphere mods = Treat as normal. Install a Heliosphere mod: the usual "Sort it?" popup appears (no tool notice). **Sort now**: the confirm window lists it with a grey "(Heliosphere)" tag after its name. Cancel.
- [ ] Same with **Sort inside their folder** (a mod at the root sorts normally).
- [ ] Proteus: make a new Proteus mod with Proteus mods = Protected: the notice names Proteus. (Skip if Proteus does not create root mods for you; note where it puts them.)

Silenced
- [ ] Tick **Don't show this popup** on the notice: it closes and does not come back for later Heliosphere installs. Turn **Show a popup when new mods are found** back on in Sort > Auto sort.
- [ ] Sort > Auto sort: new mods = Do nothing. Install a Heliosphere mod: no notice. Set it back.
- [ ] Automatic sorting on, one ordinary + one Heliosphere mod: the ordinary one is sorted (chat line), then the notice about the Heliosphere one appears.

Sort tab status line
- [ ] With Heliosphere mods in the library and both modes Protected: at the top of the Sort tab's scrolling area, on Recommended AND Advanced, a yellow line "N Heliosphere mods: Protected (not sorted). M Proteus mods: Protected (not sorted). Change this on the Protect tab." with an **Open Protect tab** button (Proteus part only if you have Proteus mods). The counts match the Protect tab dropdown labels.
- [ ] **Open Protect tab** switches to the Protect tab.
- [ ] Set Heliosphere to Treat as normal (and Proteus too, or have none): the line turns grey and reads "N Heliosphere mods: Treat as normal. ..."
- [ ] A library with no Heliosphere or Proteus mods and both modes Protected: no line at all.
- [ ] Narrow the window to its minimum width: the line wraps and the button stays visible.
