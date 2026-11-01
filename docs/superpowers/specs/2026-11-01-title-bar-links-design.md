# Title-bar link buttons

Date: 2026-11-01
Status: approved, not yet implemented

## What this adds

Three icon buttons in the main window's title bar, to the left of Dalamud's own pin and gear
icons, each opening a page in the user's browser:

| Order | Icon | Destination | Tooltip |
|---|---|---|---|
| 1 | GitHub | `https://github.com/monstersghost/PenumbraOrganizerPlugin` | Open the plugin's GitHub repository |
| 2 | Discord | `https://discord.gg/NHssF8ux6B` | Join the Discord for support |
| 3 | Ko-fi (coffee cup) | `https://ko-fi.com/K3K47YKER` | Support the plugin on Ko-fi |

This is the convention other Dalamud plugins follow, and it puts support one click away from
anywhere in the plugin rather than only from the Help tab.

The Help tab's existing "Join the Discord for support" and "Open the full guide in your browser"
buttons stay exactly as they are. This is an addition, not a move: the Help tab is where someone
reading documentation looks, the title bar is where someone who is stuck looks, and they are not
the same moment.

## Where the URLs live

`HelpTab` already holds the plugin's outbound links and already has the right reasoning attached to
them, so the two new constants go beside the existing `GuideUrl` and `DiscordUrl`:

    public const string GitHubUrl = "https://github.com/monstersghost/PenumbraOrganizerPlugin";
    public const string KofiUrl = "https://ko-fi.com/K3K47YKER";

The Discord button reuses `DiscordUrl` directly - no second copy.

Both new constants must be **unpinned**, for the reason `DiscordUrl`'s own comment already gives:
an older build pointing at a still-valid destination is exactly what you want. This is the inverse
of `GuideUrl`, which is pinned to a release tag precisely so an old build does not serve a guide
describing controls it lacks. Repo root rather than `/issues` was the maintainer's choice
(2026-11-01): the root is the front door, and the issues tab is one click from it.

## Opening the link

`HelpTab.OpenUrl` already does the right thing - `ProcessStartInfo` with `UseShellExecute = true`,
wrapped in a `try`/`catch` that logs a warning, with a comment explaining that a failed browser
launch must not escape the draw call and tear down the window. That reasoning applies identically
to a title-bar click, which also runs inside the frame.

It is `private static` on `HelpTab`. Promote it to a small `Windows/BrowserLink.cs` with a single
`public static void Open(string url)`, and have `HelpTab` call that. The behaviour and the comment
move unchanged; this is only about having one copy once there are two callers.

## Wiring the buttons

In `MainWindow`'s constructor, after the existing `SizeConstraints` block, populate Dalamud's
`Window.TitleBarButtons`. Each entry sets an icon, a click handler calling
`BrowserLink.Open(...)`, and a tooltip handler drawing the text from the table above.

Two things implementation must confirm against the Dalamud SDK rather than assume:

1. **The exact `TitleBarButton` member names.** The shape used here is an icon, a click callback,
   and a tooltip callback. The real member names and the click callback's signature come from
   Dalamud 15's `Dalamud.Interface.Windowing.Window.TitleBarButton`; read it and match it.
2. **Whether the loaded icon font carries FontAwesome's *brand* glyphs.** `FontAwesomeIcon.Discord`
   and `FontAwesomeIcon.Github` are brand-style icons, and brands are a separate glyph range from
   the solid style the plugin already uses. If they do not render, fall back to solid-style icons -
   `Code` for GitHub, `Comments` for Discord - and keep `MugHot` for Ko-fi, which is solid already.
   A box glyph in the title bar is worse than a generic-but-legible one.

Order left to right is GitHub, Discord, Ko-fi, so the tip link sits closest to Dalamud's own icons
and the two support links lead.

## Testing

Unit tests in `PenumbraOrganizer.Plugin.Tests/Windows/HelpTabTests.cs`, following the pattern the
existing URL tests already establish there:

- `GitHubUrl` and `KofiUrl` are absolute `https` URLs.
- Neither contains the plugin version string - the inverse of `GuideUrl`'s pinning test, and the
  same assertion `DiscordUrl` already carries.
- `GitHubUrl` does not contain `/blob/` or `/issues`, so a later edit toward a deep link is a
  deliberate, visible change rather than drift.

The buttons themselves cannot be tested headlessly - they need a real ImGui frame and a real
Dalamud window host. So the following is a manual, in-game checklist, and it is the only thing that
can catch the icon-font question:

- Three icons appear in the title bar, left of the pin, and none renders as a box glyph.
- Each tooltip shows the right text on hover.
- Each click opens the right page in the default browser, and the game does not hitch or crash.
- The window still pins, collapses and resizes as before.

## Out of scope

- Moving or removing the Help tab's existing link buttons.
- A Patreon or second tip destination.
- Any in-game rendering of the pages themselves; every button hands off to the system browser.
- Making the links configurable. They are compile-time constants, like the two already there.
