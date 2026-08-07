# Guided first run

Date: 2026-08-07
Part of `2026-08-07-ui-overhaul-umbrella-design.md` (piece 5 of 5, built last)

## The problem

A new user opens the plugin to six tabs, a mod list they have to populate, and a set of operations
that rearrange their Penumbra library. Nothing indicates where to start, and the two things that
would reassure them — that nothing moves until Apply, and that every operation is undoable — are not
visible anywhere.

The most likely failure is not a user doing damage. It is a user closing the window and not coming
back.

## Design

### A self-contained window

A separate walkthrough window with its own Next / Back / Skip, describing each step. The user reads a
step, does it in the main window, and advances.

This was chosen over an overlay highlighting live controls. An overlay teaches better, but it has to
track real plugin state (has a scan finished, are there proposals), survive the user going
off-script, and cope with a scan failing mid-tutorial. That is substantially more work and the most
likely of the options to feel broken — and a tutorial that feels broken is worse than none, because
it is the user's first impression.

The trade is real and worth stating: **the user has to map instructions onto controls themselves.**
Each step therefore names its tab and the exact control label, matching what is on screen.

### Steps

Seven, deliberately short. Each is a topic's `step` field in `help-content.json`, the same resource
tooltips and Help sections use.

1. **What this plugin does** — reorganises Penumbra's folders, never touches your mod files.
2. **Nothing moves until you press Apply.** Stated before anything is asked of them.
3. **Scan tab: press Refresh mod list.** Nothing else works until this happens.
4. **Protect tab: anything you check here is never moved.** Framed as the escape hatch it is.
5. **Sort tab: choose how to group, then press Sort.** Points at the dropdown from piece 2.
6. **Review Changes: look before you Apply.** Current and proposed paths side by side.
7. **History: every Apply saves a snapshot first.** Ends on the safety net rather than the action.

Steps 1, 2, 4 and 7 are reassurance rather than instruction. That balance is intentional: the risk
is abandonment, not incompetence.

### When it appears

On first run only, detected by a config flag:

```csharp
public bool FirstRunTutorialSeen { get; set; }
```

The flag is set when the user reaches the last step **or** presses Skip. Either way they have made a
decision and it is not offered again unasked.

It is always reachable afterwards from a **Show the walkthrough** button on the Help tab, which is
also how anyone testing it avoids editing config by hand.

**It does not open automatically on upgrade.** An existing user who has been using the plugin for
months should not be handed a tutorial because they updated. The flag defaults to *seen* for any
config that already exists on disk, and to *unseen* only for a genuinely fresh config.

That distinction is the one piece of real logic here and it is easy to get wrong: a naive default of
`false` would show the tutorial to every existing user exactly once, which is precisely the outcome
to avoid.

### What it does not do

- **It never drives the plugin.** No step performs an action on the user's behalf. It describes;
  they act. This keeps the window free of every failure mode that comes with acting on someone
  else's library.
- **It does not track progress.** Step 3 says to press Refresh mod list; it does not detect whether
  they did. Detecting completion is the overlay design's problem, and it is the part that breaks.
- **It does not block the main window.** Not modal. The user can act while it is open, which is the
  entire point of a side-by-side walkthrough.

### Where the code goes

`Windows/FirstRunWindow.cs`, a second Dalamud `Window` registered alongside `MainWindow`, not a tab
and not a popup. `MainWindow.cs` is around 2,000 lines and three other pieces of this overhaul touch
it; the tutorial does not add to it.

Step content is data, so step navigation is a pure index over a list and is testable without ImGui.

## Testing

- Step navigation: Next past the last step closes and marks seen; Back from the first is a no-op;
  Skip at any step marks seen.
- The flag is written once and the window does not reopen on the next construction.
- **A pre-existing config defaults to seen; a fresh config defaults to unseen.** This is the test
  that matters most, because getting it wrong spams every existing user.
- Every step id resolves to a topic with a non-empty `step`, same failure mode as the tooltip and
  Help section tests.
- Show the walkthrough reopens it regardless of the flag, and doing so does not clear the flag.

## Out of scope

- Highlighting or pointing at live controls.
- Detecting whether the user completed a step.
- Any per-tab first-visit hints. That was a considered alternative, not an addition to this.
- Localisation.
