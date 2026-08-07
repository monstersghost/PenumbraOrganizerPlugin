# Guided first run

Date: 2026-08-07
Part of `2026-08-07-ui-overhaul-umbrella-design.md` (piece 5 of 6, built last, depends on piece 4)

## The problem

A new user opens the plugin to seven tabs, a mod list they have to populate, and a set of operations
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

**It opens on the first time `MainWindow` is opened**, not on plugin load. Opening a window over
someone's gameplay because a plugin loaded is hostile; the tutorial is about the plugin's window, so
it waits until they look at it.

#### The flag, and how the distinction is actually made

A plain `bool` **cannot** distinguish "old config that predates this field" from "new config with the
default", because both deserialise to `false`. An earlier draft specified the outcome and not the
mechanism, which is exactly how every existing user ends up seeing a tutorial on upgrade.

The mechanism:

```csharp
// Nullable on purpose. null means "this config predates the field".
public bool? FirstRunTutorialSeen { get; set; }
```

and at the single place the config is loaded (`Plugin.cs:68`):

```csharp
var loaded = PluginInterface.GetPluginConfig() as Configuration;
Config = loaded ?? new Configuration();

// The null check IS the signal, and it exists only here. Resolve it immediately.
Config.FirstRunTutorialSeen ??= loaded is not null;   // pre-existing config -> seen
PluginInterface.SavePluginConfig(Config);
```

So: property present, respect it. Property absent but a config file existed, treat as **seen**.
Genuinely first run, **unseen**. The value is written back at once so the distinction never has to be
re-derived.

`Configuration.Version` is currently `1` with no migration path in the repo. This piece does not
introduce one; if a general migration mechanism is added later, this resolves into it.

The flag is set to `true` when the user reaches the last step **or** presses Skip **or** closes the
window. All three are a decision, and the most likely exit is the close button — a Dalamud `Window`
renders one by default. An earlier draft wrote the flag only on Next-from-last-step and Skip, which
would hand step 1 to anyone who closed it, every session, forever.

**Progress is not resumed.** Closing at step 3 and reopening later starts at step 1. Tracking a
position is state nobody asked for, and the walkthrough is short enough that restarting costs
nothing.

It is always reachable afterwards from a **Show the walkthrough** button on the Help tab, which is
also how anyone testing it avoids editing config by hand. **This piece adds that button**; the Help
tab spec reserves space for it.

#### If Penumbra is not running

Every step from "press Refresh mod list" onwards describes results the user will not see, and a
first-run user is the one most likely to have Penumbra disabled. When Penumbra's IPC is unavailable,
the window opens on a single step saying so and offering Close, and **does not set the flag** — this
is the one case where the user has not made a decision. It appears again next time.

#### Adding steps in a later release

A single `bool` cannot offer new steps to someone who completed the old walkthrough. That is
**accepted, not overlooked**: the tutorial is orientation for new users, not a changelog. Anyone can
reopen it from Help. If a future release genuinely needs to re-onboard existing users, that needs a
step-set version and is out of scope here.

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
and not a popup. It is the one piece of this overhaul that does **not** add UI to `MainWindow`; its
only integration point there is the Help tab button, which it adds.

**Both windows are placed, not left to Dalamud.** Two windows defaulting to centre would overlap,
which defeats a side-by-side walkthrough entirely. `FirstRunWindow` gets a default size and an
initial position offset from `MainWindow`, set with `ImGuiCond.FirstUseEver` so the user's own
placement survives. Its body pushes a wrap position; long step text otherwise sets the window width.

Step content is data, so step navigation is a pure index over a list and is testable without ImGui.

**Step text names tabs and control labels**, which is deliberate — it is what compensates for not
highlighting live controls — but it means renaming a button in `MainWindow.cs` silently makes a step
wrong while every id still resolves and every test passes. The mitigation is that the labels a step
references are listed in the spec alongside the step, so a rename has one place to check. Piece 2's
"press Sort" and "choose how to group" refer to controls that do not exist until piece 2 lands, which
is why this piece is built last.

## Testing

- Step navigation: Next past the last step closes and marks seen; Back from the first is a no-op;
  Skip at any step marks seen.
- **Closing the window marks seen**, and the next construction does not reopen it.
- The flag is written once.
- **A pre-existing config defaults to seen; a fresh config defaults to unseen.** This is the test
  that matters most, because getting it wrong spams every existing user. It is testable because the
  resolution happens at one seam: a loader that returns `null` versus one that returns a config with
  `FirstRunTutorialSeen == null`. Both cases go through `Configuration` directly, matching how
  `ConfigurationTests` already constructs it.
- **A config with `FirstRunTutorialSeen == false` explicitly set still shows the tutorial.** This is
  what distinguishes "absent" from "present and false" and is the case a non-nullable `bool` loses.
- With Penumbra unavailable, the window shows the single explanatory step and **does not** set the
  flag.
- Every step id resolves to a topic with a non-empty `step`, and every topic carrying `step` appears
  in the step list — both directions, per piece 3's rule.
- Show the walkthrough reopens it regardless of the flag, and doing so does not clear the flag.

## Out of scope

- Highlighting or pointing at live controls.
- Detecting whether the user completed a step.
- Any per-tab first-visit hints. That was a considered alternative, not an addition to this.
- Localisation.
