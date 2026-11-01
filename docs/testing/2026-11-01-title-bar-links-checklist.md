# Manual check: title-bar link buttons

Build and load the plugin in game, then open the Penumbra Organizer window.

**Icons:** Dalamud's FontAwesome enum has no brand glyphs, so the buttons use solid stand-ins:
GitHub = `Code`, Discord = `Comments`, Ko-fi = `MugHot`. Judge them on screen, not by name.

## Rendering

- [ ] Three icons appear in the title bar, to the LEFT of Dalamud's pin and gear icons.
- [ ] None renders as an empty box or a question mark.
- [ ] The icons sit on roughly the same baseline as Dalamud's own icons. If not, adjust
      `IconOffset` in `MainWindow`'s constructor; it is cosmetic only.
- [ ] Left to right, the order is GitHub, Discord, Ko-fi.

## Tooltips

- [ ] First shows "Open the plugin's GitHub repository".
- [ ] Second shows "Join the Discord for support".
- [ ] Third shows "Support the plugin on Ko-fi".

## Clicks

- [ ] First opens `https://github.com/monstersghost/PenumbraOrganizerPlugin` in the default browser.
- [ ] Second opens the Discord invite and it resolves to a server, not "Invite Invalid".
- [ ] Third opens `https://ko-fi.com/K3K47YKER`.
- [ ] The game does not hitch, freeze or crash on any click.

## No regressions

- [ ] The window still pins, collapses, resizes and closes as before.
- [ ] The Help tab's "Join the Discord for support" and "Open the full guide in your browser"
      buttons still work.
