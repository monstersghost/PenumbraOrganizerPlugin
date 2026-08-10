# Pieces 3, 4 and 5: Hover Explanations, Help Tab and Guided First Run

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

> **Refreshed 2026-08-10 against `ef62b67`, after pieces 0, 1 and 2 shipped.** Every file path, line
> number and control label below was re-checked against the real code and corrected. The original
> warning said Tasks 1-2 were safe and only 3-6 had decayed; that was wrong — piece 0's split of
> `MainWindow.cs` into partials invalidated addresses in Task 2 and in the Global Constraints too.
> Corrections are marked **[refreshed]** where the original text would have sent you to the wrong
> place. Re-verify again if this sits unexecuted for another few pieces.

**Goal:** Explain the plugin in three depths from one content source: a tooltip on every non-obvious control, a Help tab, and a walkthrough on first run.

**Architecture:** One embedded `help-content.json`, created by piece 2, holds every explanation keyed by topic id. Tooltips render `short`, Help sections render `body`, tutorial steps render `step`. Code references typed `HelpTopic` constants, never string literals.

**Tech Stack:** C# / .NET 10, Dalamud plugin, Dear ImGui, xUnit.

**Specs:** `2026-08-07-hover-explanations-design.md`, `2026-08-07-help-tab-design.md`, `2026-08-07-guided-first-run-design.md`, and the umbrella for the shared schema.

## Global Constraints

- **`Help`, `HelpTopic`, `HelpTopics` and `help-content.json` already exist**, created by piece 2 Task 2. This plan extends them; it does not create them.
- **`id` and `title` are required; `short`, `body` and `step` are each optional and a topic must carry at least one.** Validation is by consumer: a tooltip reference needs `short`, a Help section needs `body`, a step needs `step`.
- **Both directions are enforced.** Every topic carrying `body` must appear in a section list; every topic carrying `step` must appear in the step list. Otherwise adding a `step` to the resource does nothing at all, since step order lives in code.
- **Call sites take `HelpTopic`, never `string`.** A mistyped literal must not compile.
- **`Help.Tooltip` is called immediately after its widget and outside any `BeginDisabled` scope**, and passes `ImGuiHoveredFlags.AllowWhenDisabled`. Get either wrong and tooltips silently never appear on disabled controls, which is the case they most need to work. `EndDisabled` submits no item of its own, so calling the tooltip straight after it still binds to the widget.
- **[refreshed] The disabled reason goes through `Help.Tooltip`'s `disabledReason` parameter, never a second `SetTooltip` on the same widget.** Piece 2 settled this: two tooltip calls against one item in one frame fight over the same window. Every control in `SortPanel` passes the reason as the parameter. The existing hand-written sites this task converts do it the old way — they predate the convention, and converting them means folding the literal into the parameter, not leaving it beside the call.
- **[refreshed] Two controls have more than one disabled reason** and are converted by hand, not swept: Apply (`result.HasIssues || !gates.CanStartApply`, **`MainWindow.ReviewTab.cs:99`**) and Folder Cleanup (`_selectedOrphans.Count == 0 || !gates.CanRunFolderCleanup`, **`MainWindow.ReviewTab.cs:222`**). The comment at **`MainWindow.ReviewTab.cs:225-228`** explains why the operation-in-progress message must not show when the real reason is "nothing selected". Both conditions are unchanged from the original plan; only the addresses moved when piece 0 split the file.
- **Every type this plan creates or tests against must be `public`.** This repo has **no `InternalsVisibleTo`** — verified, not assumed. `Help`, `HelpTopic`, `HelpTopics`, `HelpTopicUsage`, `HelpTab` and `FirstRunSteps` are all referenced from `PenumbraOrganizer.Plugin.Tests`, so `internal` on any of them is a CS0122 at build time, not a design preference.
- **Anything a test drives must live outside a `Window` subclass and call no ImGui.** The test project cannot construct a Dalamud `Window` or enter an ImGui frame. This is why Task 5 splits `FirstRunSteps` from `FirstRunWindow`.

---

### Task 1: Fill the content resource for every covered control

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Resources/help-content.json`
- Modify: `PenumbraOrganizer.Plugin/Windows/HelpTopics.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Windows/HelpContentTests.cs`

**Interfaces:**
- Consumes: `HelpTopic`, `HelpTopics.All`, `Help.Short/Title/Body/Step` (piece 2 Task 2).
- Produces: a topic constant per control below. Task 2 wires them to widgets.

- [ ] **Step 1: Add a constant and a topic for every control in the coverage list**

Scan tab (`MainWindow.ScanTab.cs`): Refresh mod list, the event log.
Protect tab (`MainWindow.ProtectTab.cs`): both toggle-all buttons, the folder/mod distinction, the Heliosphere note.
Sort tab (`MainWindow.SortTab.cs`): **manual assignment only.** **[refreshed]** The original list also named Import Workbook, but piece 2 already gave it `HelpTopics.SortImportWorkbook` and wired it at `MainWindow.SortTab.cs:57`. Six Sort-tab topics exist and are wired: the four `SortPanel` controls, the scraped-list opt-in, and Import Workbook.
Review Changes (`MainWindow.ReviewTab.cs`): Apply, Export, Export Workbook and its destinations dropdown, Protect & Skip All Blocking Mods, Show Config File, Create Diagnostic Dump, Clean Up Selected Folders, Rollback Folder Cleanup, Re-read organization.json.
History (`MainWindow.HistoryTab.cs`): Create Backup, Restore, Delete snapshot.
Search (`MainWindow.SearchTab.cs`): Build/Refresh Index, the filter boxes, the category and slot rows.

**[refreshed] The recovery panel** is the whole of `MainWindow.Recovery.cs` (247 lines), not the
40-line range the original plan cited. It sits above the tab bar, so no tab entry covers it, and it
holds the most consequential controls in the plugin. It has **two mutually exclusive branches** and
both need topics:

*Single interrupted operation* (the `else` path, from `MainWindow.Recovery.cs:96`):

- Keep Current State (`:103`, disabled on `!CanResolveRecovery`)
- Continue (`:124`, disabled on `!CanContinueRecovery`)
- Restore Previous State (`:142`, disabled on `!CanRestorePreviousState`)

*Multiple blocked roots* (`IsBlockedByMultipleRoots`, from `MainWindow.Recovery.cs:23`) — **covered,
not skipped.** These are the most destructive controls in the plugin and the least self-explanatory:

- **The per-operation Keep Current State button** (`:53`, id `##multiroot-{operationId}`). It is
  rendered inside a `foreach` over blocked operations, so one topic is reused across every row and
  the `Help.Tooltip` call goes inside the loop, immediately after `ImGui.Button`. Note it is **not**
  wrapped in `BeginDisabled`, unlike its single-recovery namesake. Its `short` must say what the
  comment at `:25-28` says: clicking one row does not turn that operation into an ordinary
  recovery — it permanently abandons that operation, and one of the remaining ones may then become
  the ordinary single recovery.
- **Accept Current State and Close All Interrupted Operations** (`:71`). Abandons every interrupted
  operation at once. Its `short` must be explicit that nothing can be continued or rolled back
  afterwards.

Deliberately not covered: Cancel, Yes/No confirmations, anything inside a modal whose own text
explains the choice. That exempts every `BeginPopupModal` body in `MainWindow.Recovery.cs` — the
"Yes, Keep Current" / "Yes, Close All" / "Yes, Continue" / "Yes, Restore" buttons all sit under
wrapped text that already explains the consequence. The `Details` collapsing header (`:159`) is also
out of scope: it reveals diagnostics that label themselves.

- [ ] **Step 2: Write one `short` per topic**

One line, no newline, under the length cap. Write what the control *does to the user's library*, not what it does in code. "Applies every proposed path change to Penumbra" beats "Invokes the apply operation".

One control changes meaning with state: Toggle protect all protects everything if anything is unprotected and unprotects otherwise. Its single `short` covers both directions; the schema is deliberately parameter-free.

- [ ] **Step 3: Run the existing content tests**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests --filter "HelpContentTests"
```

Expected: pass. They already assert every constant resolves, every topic has a title, no `short` has a newline or exceeds the cap.

- [ ] **Step 4: Commit**

```bash
git add PenumbraOrganizer.Plugin/Resources/help-content.json PenumbraOrganizer.Plugin/Windows/HelpTopics.cs
git commit -m "feat: write tooltip content for every non-obvious control"
```

---

### Task 2: Wire tooltips to the widgets

**Files:**
- Modify: every `PenumbraOrganizer.Plugin/Windows/MainWindow.*.cs` file
- Test: `PenumbraOrganizer.Plugin.Tests/Windows/HelpContentTests.cs`

**Interfaces:**
- Consumes: the topics from Task 1.
- Produces: nothing later tasks depend on.

- [ ] **Step 1: Create the declared-usage list**

The test below reads `HelpTopicUsage.ReferencedByControls`, which no earlier task defines. Create it first, in `PenumbraOrganizer.Plugin/Windows/HelpTopicUsage.cs`. It is `public` because the test project reads it and there is no `InternalsVisibleTo`:

```csharp
namespace PenumbraOrganizer.Plugin.Windows;

/// <summary>
/// The topics a control is intended to show as a tooltip. Hand-maintained: adding a
/// <c>Help.Tooltip</c> call means adding the topic here too. See the comment on
/// <c>EveryTopicWithAShort_IsDeclaredAsUsedByAControl</c> for what this does and does not prove.
/// </summary>
public static class HelpTopicUsage
{
    public static IReadOnlyList<HelpTopic> ReferencedByControls { get; } =
    [
        // One entry per topic wired in Step 3, in tab order.
    ];
}
```

**[refreshed] This list does not start empty.** All six topics piece 2 created carry a `short` and
are already wired, so they must be in it from the first commit or Step 2's test fails immediately:
`SortGrouping`, `SortSplitGear`, `SortSplitNpc`, `SortButton`, `SortScrapedNpcList`,
`SortImportWorkbook`. Their call sites are `SortPanel.cs:80/87/93/107/122` and
`MainWindow.SortTab.cs:57`.

- [ ] **Step 2: Add the reverse-direction test**

```csharp
[Fact]
public void EveryTopicWithAShort_IsDeclaredAsUsedByAControl()
{
    // NAMED FOR WHAT IT PROVES. HelpTopicUsage.ReferencedByControls is a hand-maintained list,
    // so this is a DECLARED-USAGE CONSISTENCY check: it catches a topic whose tooltip text nobody
    // ever intends to show. It does NOT prove any control calls Help.Tooltip - a topic can appear
    // in help-content.json, in HelpTopics, and in this list while the widget has no call at all.
    //
    // Actual wiring is protected by three things, none of them this test: code review of the
    // call-site convention, the immediately-after-widget rule, and Step 5's manual in-game pass
    // over every control including disabled ones.
    var declared = HelpTopicUsage.ReferencedByControls;
    var withShort = HelpTopics.All.Where(t => Help.Short(t) is not null);
    Assert.Empty(withShort.Except(declared));
}

[Fact]
public void EveryDeclaredUsage_IsAKnownTopic()
{
    // [refreshed] The other direction, and cheap. Without it, renaming or deleting a topic leaves a
    // stale entry in the hand-maintained list that nothing ever flags.
    Assert.Empty(HelpTopicUsage.ReferencedByControls.Except(HelpTopics.All));
}
```

- [ ] **Step 3: Add `Help.Tooltip(...)` after each widget**

Placement is a hard requirement, not a style note.

**[refreshed] Follow `SortPanel.cs:76-123`, not the old hand-written sites.** The original plan
pointed at `MainWindow.cs:426-431` as the exemplar; that code is now `MainWindow.ScanTab.cs:24-31`
and it is an example of the pattern being *replaced*, not the one to copy — it ends in a bare
`ImGui.SetTooltip` with the gate literal inline. The shape to copy is:

```csharp
ImGui.BeginDisabled(!gates.CanScan);
if (ImGui.Button("Refresh mod list"))
    RunScan();
ImGui.EndDisabled();
Help.Tooltip(HelpTopics.ScanRefresh,
    gates.CanScan ? null : "Another operation is in progress or requires recovery.");
```

There are 11 `ImGui.SetTooltip` calls in `Windows/`; one is `Help.Tooltip`'s own implementation and
one is in `PathTreeView.cs`, leaving roughly nine conversion sites. Most carry the identical
activity-gate literal, which becomes the `disabledReason` argument rather than a separate call.

Add each topic you wire to `HelpTopicUsage.ReferencedByControls` in the same edit; the Step 2 test
fails otherwise.

- [ ] **Step 4: Convert the two multi-reason sites by hand**

Apply and Folder Cleanup each have two disable conditions. The call site decides which reason applies:

```csharp
Help.Tooltip(
    HelpTopics.ReviewApply,
    result.HasIssues ? "Fix the errors listed above first."
    : !gates.CanStartApply ? "Another operation is in progress or requires recovery."
    : null);
```

Do the same for Folder Cleanup, preserving the distinction the comment at
**`MainWindow.ReviewTab.cs:225-228`** documents — with nothing selected, the reason is "nothing
chosen yet" and the operation message must not appear. The existing guard is
`_selectedOrphans.Count > 0 && !gates.CanRunFolderCleanup`, so the converted form is:

```csharp
Help.Tooltip(
    HelpTopics.ReviewCleanUpFolders,
    _selectedOrphans.Count == 0 ? "Choose at least one folder above first."
    : !gates.CanRunFolderCleanup ? "Another operation is in progress or requires recovery."
    : null);
```

Note this is a deliberate improvement, not a straight port: today the no-selection case shows
*nothing*. Giving it its own reason is the point of the exercise, and it is why these two are
converted by hand. Review both individually.

- [ ] **Step 5: In-game pass**

Hover every control that gained a topic, including disabled ones. A tooltip that never appears on a disabled control means `AllowWhenDisabled` was missed.

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/Windows/ PenumbraOrganizer.Plugin.Tests/Windows/
git commit -m "feat: show hover explanations on every non-obvious control"
```

---

### Task 3: The Help tab

**Files:**
- Create: `PenumbraOrganizer.Plugin/Windows/HelpTab.cs`
- Modify: `PenumbraOrganizer.Plugin/Windows/MainWindow.cs` (tab dispatch only)
- Modify: `PenumbraOrganizer.Plugin/Resources/help-content.json`
- Modify: `docs/USER_GUIDE.md`
- Test: `PenumbraOrganizer.Plugin.Tests/Windows/HelpTabTests.cs`

**Interfaces:**
- Consumes: `Help.Body`, `Help.Title`.
- Produces: `HelpTab.Draw()`, and a **third button slot** reserved for piece 5's "Show the walkthrough".

- [ ] **Step 1: Add section topics and the section model**

Sections are an **ordered list of topic ids**, not a flat lookup. A section renders its own `body` first, then the `body` of each control topic it covers. Without composition a flat schema cannot express "the Sort section is these five controls in this order".

New section topics with `body` and no `short`: `help.what-this-does`, `help.safety-rules`, `help.when-things-go-wrong`, `help.where-your-files-are`, plus one per tab.

- [ ] **Step 2: Write the tests**

```csharp
[Fact]
public void EverySectionTopicResolves_AndHasABody() { }

[Fact]
public void EveryTopicWithABody_AppearsInSomeSection()
{
    // The other half of the both-directions rule.
}

[Fact]
public void TheGitHubLink_CarriesAVersionTag_NotMain()
{
    // Pointing at main serves the newest guide to someone on an older build.
    Assert.DoesNotContain("/blob/main/", HelpTab.GuideUrl);
}
```

- [ ] **Step 3: Implement**

`HelpTab` is `public` (no `InternalsVisibleTo`; the tests above read `HelpTab.GuideUrl`). Define that constant here — it is the one member the tests touch:

```csharp
/// Pinned to a release tag, never to main: main serves the newest guide to someone on an older build.
public const string GuideUrl =
    "https://github.com/monstersghost/PenumbraOrganizerPlugin/blob/0.6.0/docs/USER_GUIDE.md";
```

**[refreshed] This link 404s until the `0.6.0` tag exists, and Task 6 Step 3 forbids tagging.** The
repo's tags are plain versions (`0.5.1.0` … `0.5.3.1`), so the URL shape is right, but the tag is
created at publish time by the maintainer. The `TheGitHubLink_CarriesAVersionTag_NotMain` test only
asserts the URL is not `/blob/main/`, so it passes against a tag that does not exist — the test
cannot catch this. **Add "push the `0.6.0` tag before announcing" to the release checklist in Task
6**, and be aware that anyone running a dev build between this task and the tag gets a dead link.

`HelpTab` is a **tab-drawing type inside `MainWindow`'s tab bar, not a `Window`**. It touches nothing outside the plugin: no Penumbra IPC, no mod-library read or write, no file write. Piece 5's walkthrough button is compatible with that and is not a violation.

It is a standalone type rather than a `MainWindow.HelpTab.cs` partial, deliberately breaking piece 0's one-partial-per-tab convention: this tab's content is the only tab content worth unit-testing, and a partial of `MainWindow` cannot be reached from a test without constructing `MainWindow`. Note the deviation in the commit message so a later reader does not "restore consistency".

`MainWindow` gains one tab entry after Search and one dispatch call. Tab dispatch is the one place all these features converge and stays in `MainWindow.cs`.

- [ ] **Step 4: Update the guide**

`docs/USER_GUIDE.md` says "The six tabs" at line 20. It is seven now. Fix that and add the Help tab.

- [ ] **Step 5: Run, in-game check, commit**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests
git add PenumbraOrganizer.Plugin/Windows/HelpTab.cs PenumbraOrganizer.Plugin/Windows/MainWindow.cs PenumbraOrganizer.Plugin/Resources/help-content.json docs/USER_GUIDE.md PenumbraOrganizer.Plugin.Tests/Windows/HelpTabTests.cs
git commit -m "feat: add an in-game Help tab"
```

---

### Task 4: First-run config migration

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Configuration.cs`
- Modify: `PenumbraOrganizer.Plugin/Plugin.cs:68`
- Test: `PenumbraOrganizer.Plugin.Tests/ConfigurationTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `Configuration.FirstRunTutorialSeen` as `bool?`, and `Configuration.ResolveFirstRunFlag(Configuration, bool)` returning `bool`.

**This is the task most likely to produce a user-visible bug**, and it is why it is its own task rather than a step inside Task 5. A plain `bool` cannot distinguish "old config predating this field" from "new config with the default" — both deserialise to `false`, and every existing user gets a tutorial on upgrade.

**The resolver lives on `Configuration`, not on `Plugin`.** An earlier draft of this plan put it on `Plugin` as `internal static` and had the tests call `Plugin.ResolveFirstRunFlag`. That does not compile: there is no `InternalsVisibleTo` in this repo, so the test project cannot see an `internal` member — and even `public` on `Plugin` would make the test project load a type whose every member is a Dalamud service. `Configuration` is already `public sealed` and already has a test file. Put it there.

- [ ] **Step 1: Write the tests**

In the existing `PenumbraOrganizer.Plugin.Tests/ConfigurationTests.cs`:

```csharp
[Fact]
public void PreExistingConfig_WithoutTheField_DefaultsToSeen()
{
    var loaded = new Configuration();          // property absent -> null
    Assert.True(Configuration.ResolveFirstRunFlag(loaded, configExisted: true));
}

[Fact]
public void GenuinelyFreshConfig_DefaultsToUnseen()
{
    Assert.False(Configuration.ResolveFirstRunFlag(new Configuration(), configExisted: false));
}

[Fact]
public void ExplicitFalse_IsRespected_AndNotTreatedAsAbsent()
{
    // The case a non-nullable bool loses entirely.
    var loaded = new Configuration { FirstRunTutorialSeen = false };
    Assert.False(Configuration.ResolveFirstRunFlag(loaded, configExisted: true));
}

[Fact]
public void Resolving_WritesTheAnswerBack_SoTheNullIsNeverReDerived()
{
    var loaded = new Configuration();
    Configuration.ResolveFirstRunFlag(loaded, configExisted: true);
    Assert.True(loaded.FirstRunTutorialSeen);
}
```

- [ ] **Step 2: Implement**

In `Configuration.cs`:

```csharp
public bool? FirstRunTutorialSeen { get; set; }

// The null check IS the signal and it exists only here. Resolve it immediately and write back,
// so the distinction never has to be re-derived.
public static bool ResolveFirstRunFlag(Configuration config, bool configExisted) =>
    config.FirstRunTutorialSeen ??= configExisted;
```

Then replace the config load in the `Plugin` constructor. **[refreshed] `Plugin.cs:68` is still
exactly that line** — verified at `ef62b67`:

```csharp
var loaded = PluginInterface.GetPluginConfig() as Configuration;
Config = loaded ?? new Configuration();
Configuration.ResolveFirstRunFlag(Config, configExisted: loaded is not null);
SaveConfig();
```

**[refreshed] Two things about this edit region that did not exist when the plan was written:**

- Piece 1 inserted the `NpcNameListStore.MigrateLegacyList` call immediately after line 68. It does
  not read config, so there is no ordering dependency — but do not displace it, and keep it before
  `ScanWork`/`IndexWork` are constructed, which is the invariant it relies on.
- Piece 2 added `Plugin.SaveConfig()`, an internal one-liner over `PluginInterface.SavePluginConfig`.
  Use it rather than calling `SavePluginConfig` directly, matching `SortPanel`'s call site.

- [ ] **Step 3: Run and commit**

```bash
dotnet test PenumbraOrganizer.Plugin.Tests --filter "ConfigurationTests"
git commit -m "feat: distinguish a pre-existing config from a fresh one for first-run detection"
```

---

### Task 5: The guided first run window

**Files:**
- Create: `PenumbraOrganizer.Plugin/Windows/FirstRunSteps.cs`
- Create: `PenumbraOrganizer.Plugin/Windows/FirstRunWindow.cs`
- Modify: `PenumbraOrganizer.Plugin/Windows/HelpTab.cs` (the reserved third button)
- Modify: `PenumbraOrganizer.Plugin/Plugin.cs` (window registration)
- Modify: `PenumbraOrganizer.Plugin/Resources/help-content.json`
- Test: `PenumbraOrganizer.Plugin.Tests/Windows/FirstRunStepsTests.cs`

**Interfaces:**
- Consumes: `Configuration.ResolveFirstRunFlag` (Task 4), `Help.Step`, `HelpTab`'s third button slot.
- Produces: nothing.

- [ ] **Step 1: Define the testable half first**

Every test below drives navigation, and none of them can construct a Dalamud `Window` or enter an ImGui frame. So the state machine is its own `public` type with no ImGui and no Dalamud reference; `FirstRunWindow` owns one and draws it. Without this split the test file named in **Files** cannot be written at all.

```csharp
namespace PenumbraOrganizer.Plugin.Windows;

/// Navigation and completion for the walkthrough. Deliberately free of ImGui and Dalamud
/// so it can be tested; FirstRunWindow is the thin drawing shell around it.
public sealed class FirstRunSteps(IReadOnlyList<HelpTopic> steps, bool penumbraAvailable)
{
    public int Index { get; private set; }
    public bool IsFinished { get; private set; }

    /// True when the run reached a real ending. False after the Penumbra-unavailable path,
    /// which makes no decision and so must not consume the first run.
    public bool ShouldMarkSeen { get; private set; }

    public HelpTopic Current => ...;   // the explanatory topic when !penumbraAvailable
    public void Next();                // past the last step -> IsFinished, ShouldMarkSeen = penumbraAvailable
    public void Back();                // no-op at Index 0
    public void Skip();                // IsFinished, ShouldMarkSeen = true
    public void Closed();              // same outcome as Skip: the X is the likeliest exit
}
```

- [ ] **Step 2: Write the navigation and lifecycle tests**

```csharp
[Fact] public void NextPastTheLastStep_ClosesAndMarksSeen() { }
[Fact] public void BackFromTheFirstStep_IsANoOp() { }
[Fact] public void SkipAtAnyStep_MarksSeen() { }

[Fact]
public void ClosingTheWindow_MarksSeen()
{
    // The most likely exit. A Dalamud Window renders an X by default; writing the flag only on
    // Next-from-last and Skip hands step 1 to that user every session, forever.
}

[Fact]
public void ProgressIsNotResumed_ReopeningStartsAtStepOne() { }

[Fact]
public void WithPenumbraUnavailable_ShowsOneExplanatoryStep_AndDoesNotMarkSeen()
{
    // Every step from "press Refresh mod list" onward describes results the user will not see,
    // and a first-run user is the likeliest to have Penumbra disabled. This is the one case where
    // no decision was made, so it appears again next time.
}

[Fact]
public void EveryStepIdResolves_AndEveryTopicWithAStepIsInTheStepList() { }
```

- [ ] **Step 3: Implement**

`FirstRunWindow` is a second Dalamud `Window`, and holds a `FirstRunSteps`. It contributes no logic of its own: it draws `Current`, routes the three buttons and the close to `Next`/`Back`/`Skip`/`Closed`, and on `IsFinished` writes the config flag **only if `ShouldMarkSeen`**. It **opens on the first time `MainWindow` opens**, not on plugin load — a window appearing over someone's gameplay because a plugin loaded is hostile.

Both windows are placed explicitly with `ImGuiCond.FirstUseEver`: a default size and an offset initial position so they do not both centre and overlap, which would defeat a side-by-side walkthrough. The body pushes a wrap position.

Step text names tabs and control labels, which is what compensates for not highlighting live controls. **List the labels each step references in a comment beside the step**, so a rename has one place to check.

- [ ] **Step 4: Add the Help tab button**

"Show the walkthrough" reopens it regardless of the flag and does **not** clear the flag.

- [ ] **Step 5: In-game pass**

Fresh config: the walkthrough appears on first opening the window. Existing config: it does not. Close midway, reopen the plugin: it does not reappear. Disable Penumbra, wipe the flag, reopen: one explanatory step, and it appears again next time.

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/Windows/FirstRunSteps.cs PenumbraOrganizer.Plugin/Windows/FirstRunWindow.cs PenumbraOrganizer.Plugin/Windows/HelpTab.cs PenumbraOrganizer.Plugin/Plugin.cs PenumbraOrganizer.Plugin/Resources/help-content.json PenumbraOrganizer.Plugin.Tests/Windows/FirstRunStepsTests.cs
git commit -m "feat: add a guided walkthrough on first run"
```

---

### Task 6: Release preparation for 0.6.0

**Files:**
- Modify: `PenumbraOrganizer.Plugin/PenumbraOrganizer.Plugin.csproj`
- Create: `docs/RELEASE_NOTES_0.6.0.md`

**Interfaces:** none.

- [ ] **Step 1: Bump the version to `0.6.0`**

- [ ] **Step 2: Write the notes**

Cover: the sort control, the NPC list change, tooltips, the Help tab, the walkthrough.

**Wording constraints, both non-negotiable.** The NPC change **must not be called a crash fix**: what is established is that resetting the oversized list stops the observed crash and that the mechanism is unknown. And the scraped-list toggle ships **disabled**, which the notes should say plainly rather than leaving users to find a greyed-out checkbox.

Mention that Split NPC can now be turned off, since that combination never existed before and its output (`NPC` rather than `NPC/Bosses`) will surprise anyone who does not read the notes.

- [ ] **Step 3: Do not touch `repo.json`, do not tag, do not publish**

The maintainer publishes explicitly, after reviewing the notes. Preparing them is in scope; shipping them is not.

**[refreshed] Record the tag dependency in the notes for the maintainer.** `HelpTab.GuideUrl`
(Task 3) points at `/blob/0.6.0/docs/USER_GUIDE.md`, which does not resolve until a `0.6.0` tag is
pushed. The Help tab's "read the full guide" link is dead until then. The publish sequence is:
review notes → tag `0.6.0` → release → update `repo.json`. No test covers this, so it lives here or
it is discovered by a user.

**[refreshed] Also state in the notes that Split NPC defaults to on**, so an upgrading user's output
is unchanged unless they deliberately turn it off. The plan already requires mentioning the new
combination; saying it is opt-in is what stops the note reading as a breaking change.

- [ ] **Step 4: Commit**

```bash
git add PenumbraOrganizer.Plugin/PenumbraOrganizer.Plugin.csproj docs/RELEASE_NOTES_0.6.0.md
git commit -m "docs: prepare 0.6.0 notes and bump the version"
```

---

## Self-review notes

- **Spec coverage:** tooltip content and wiring including the recovery panel and the two multi-reason sites (Tasks 1-2); the Help tab with section composition, the both-directions rule and the version-pinned link (Task 3); the config migration as its own task because it is the likeliest user-visible bug (Task 4); the window with close/quit, Penumbra-absent and placement (Task 5); release prep (Task 6).
- **The both-directions rule spans two tasks** — Task 2 asserts every `short` is referenced, Task 3 asserts every `body` is in a section, Task 5 asserts every `step` is in the step list. All three are needed for the rule to mean anything.
- **Deliberate deviation from strict TDD:** Tasks 1 and 3 are content-first; there is no useful failing test for "write a sentence". The tests that matter are structural and they exist.
- **This plan is the one to re-derive before executing.** Task 2 names call sites across files piece 0 creates, and Task 5's step text names controls piece 2 introduces. Check both against reality first.

## Refresh log — 2026-08-10, against `ef62b67`

What the re-check actually found, so the next reader knows how far the rot went rather than trusting
the original "Tasks 1-2 are safe" claim:

| Original reference | Corrected to | Consequence if followed as written |
|---|---|---|
| Apply, `MainWindow.cs:988` | `MainWindow.ReviewTab.cs:99` | `MainWindow.cs` is 719 lines — the address does not exist |
| Folder Cleanup, `MainWindow.cs:1560` | `MainWindow.ReviewTab.cs:222` | same |
| Distinction comment, `MainWindow.cs:1562-1565` | `MainWindow.ReviewTab.cs:225-228` | same |
| Recovery panel, `MainWindow.cs:268-307` | all of `MainWindow.Recovery.cs`, 247 lines | scope understated by roughly 6x, and the entire multi-root branch missed |
| Pattern exemplar, `MainWindow.cs:426-431` | `MainWindow.ScanTab.cs:24-31`, and it is now an anti-pattern | would have propagated the bare-`SetTooltip` shape piece 2 replaced |
| Sort tab: "Import Workbook, manual assignment" | manual assignment only | duplicate topic for an already-wired control |
| `Plugin.cs:68` | unchanged, verified | — |
| `USER_GUIDE.md:20` "The six tabs" | unchanged, verified | — |
| `ConfigurationTests.cs` exists | unchanged, verified | — |

Three things were added that the original plan did not contain at all: the multi-root recovery
controls, the `EveryDeclaredUsage_IsAKnownTopic` reverse test, and the `0.6.0` tag dependency that
makes `HelpTab.GuideUrl` resolve.
