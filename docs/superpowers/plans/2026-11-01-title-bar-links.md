# Title-Bar Link Buttons Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add three icon buttons to the main window's title bar - GitHub, Discord and Ko-fi - each opening a page in the user's browser.

**Architecture:** Two new URL constants join the two already on `HelpTab`, so every outbound link in the plugin stays in one file. `HelpTab`'s existing `private static OpenUrl` is promoted to a shared `BrowserLink.Open` now that it has a second caller. `MainWindow`'s constructor then populates Dalamud's `Window.TitleBarButtons` with three entries that call it.

**Tech Stack:** C# / .NET 10, Dalamud.NET.Sdk 15.0.0, Dalamud's `Window` windowing API, ImGui via `Dalamud.Bindings.ImGui`, xUnit for tests.

**Spec:** `docs/superpowers/specs/2026-11-01-title-bar-links-design.md`

## Global Constraints

- The two new URL constants are **exact values, copied verbatim**: `https://github.com/monstersghost/PenumbraOrganizerPlugin` and `https://ko-fi.com/K3K47YKER`.
- Both new constants must be **unpinned** - they must never contain the plugin version string. This is the inverse of `GuideUrl`, which is pinned on purpose. `DiscordUrl`'s existing XML comment explains why; read it before editing that region.
- The Discord button **reuses `HelpTab.DiscordUrl`**. Do not add a second copy of that URL.
- Do not change, move or remove the Help tab's existing "Join the Discord for support" or "Open the full guide in your browser" buttons. This feature is purely additive.
- Links are compile-time `const`. Do not make them configurable.
- Button order in the title bar, left to right: GitHub, Discord, Ko-fi.
- Existing tests in `HelpTabTests` already use the name `TheGitHubLink_*` to mean the **guide** URL, not the repository URL. Name new tests `TheRepositoryLink_*` and `TheKofiLink_*` so the two never read as the same thing.
- All strings in this feature are ASCII. If a non-ASCII character is ever needed, write it as a `\uXXXX` escape rather than pasting the glyph - this repo has been bitten three times by a pasted codepoint reaching code as the wrong value while the test still passed.
- Build and test commands, run from the repository root:
  - `dotnet build PenumbraOrganizer.Plugin.sln`
  - `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj`

## File Structure

| File | Responsibility |
|---|---|
| `PenumbraOrganizer.Plugin/Windows/BrowserLink.cs` | **Create.** One job: open a URL in the system browser without letting a failure escape into the ImGui frame. |
| `PenumbraOrganizer.Plugin/Windows/HelpTab.cs` | **Modify.** Gains `GitHubUrl` and `KofiUrl` beside the existing constants; its `OpenUrl` body moves to `BrowserLink` and its three call sites become `BrowserLink.Open`. |
| `PenumbraOrganizer.Plugin/Windows/MainWindow.cs` | **Modify.** Constructor (currently lines 97-107) populates `TitleBarButtons`. |
| `PenumbraOrganizer.Plugin.Tests/Windows/HelpTabTests.cs` | **Modify.** Add URL-shape tests for the two new constants. |
| `docs/testing/` | **Modify.** Add the manual in-game checklist (Task 4). |

Three tasks produce code, one produces the manual checklist. They are ordered so each commit builds and tests green on its own.

---

### Task 1: Extract `BrowserLink` from `HelpTab.OpenUrl`

A pure refactor with no behaviour change, done first so Task 3 has something to call. `HelpTab.OpenUrl` is `private static` at `HelpTab.cs:203-215`, with a comment that is the reason this helper exists at all - carry it across verbatim.

**Files:**
- Create: `PenumbraOrganizer.Plugin/Windows/BrowserLink.cs`
- Modify: `PenumbraOrganizer.Plugin/Windows/HelpTab.cs:203-215` (delete `OpenUrl`), and its three call sites at `HelpTab.cs:150`, `HelpTab.cs:155`, and the one in the walkthrough/footer region - find them with `grep -n "OpenUrl" PenumbraOrganizer.Plugin/Windows/HelpTab.cs`
- Test: none of its own. This task's test is that the existing suite still passes, which is what makes it a safe refactor.

**Interfaces:**
- Consumes: nothing.
- Produces: `PenumbraOrganizer.Plugin.Windows.BrowserLink.Open(string url)`, `public static void`, returns nothing, never throws. Task 3 calls this.

- [ ] **Step 1: Confirm the current state before touching anything**

Run: `grep -n "OpenUrl" PenumbraOrganizer.Plugin/Windows/HelpTab.cs`

Expected: one `private static void OpenUrl(string url)` definition around line 203, plus two or three call sites. Note the exact call-site line numbers; you will replace each one.

- [ ] **Step 2: Create `BrowserLink.cs`**

The `try`/`catch` and its comment are moved, not rewritten. The comment is the whole point of the helper: an exception here runs inside the ImGui frame and would tear down the window.

```csharp
namespace PenumbraOrganizer.Plugin.Windows;

/// <summary>
/// Opens a URL in the user's default browser.
/// </summary>
/// <remarks>
/// Extracted from <see cref="HelpTab"/> once the title bar's link buttons became a second caller.
/// Both callers run inside an ImGui frame, which is why the failure handling below is not optional.
/// </remarks>
public static class BrowserLink
{
    public static void Open(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // A failed browser launch must not take the draw call with it - this runs inside the
            // frame, and an exception here would tear down the whole window.
            Plugin.Log.Warning(ex, $"Could not open {url} in a browser.");
        }
    }
}
```

- [ ] **Step 3: Delete `HelpTab.OpenUrl` and repoint its callers**

Delete the whole `private static void OpenUrl(string url) { ... }` method from `HelpTab.cs`.

Then replace each call site. At `HelpTab.cs:150`:

```csharp
            if (ImGui.Button("Join the Discord for support"))
                BrowserLink.Open(DiscordUrl);
```

At `HelpTab.cs:155`:

```csharp
        if (ImGui.Button("Open the full guide in your browser"))
            BrowserLink.Open(GuideUrl);
```

Repeat for any remaining `OpenUrl(` occurrence the Step 1 grep found, changing only the method name.

- [ ] **Step 4: Verify no caller was missed**

Run: `grep -n "OpenUrl" PenumbraOrganizer.Plugin/Windows/HelpTab.cs`

Expected: no output at all. Any remaining hit is a call site you did not repoint, and it will not compile.

- [ ] **Step 5: Build**

Run: `dotnet build PenumbraOrganizer.Plugin.sln`

Expected: build succeeded, 0 errors. A `CS0103 The name 'OpenUrl' does not exist` means Step 3 missed a call site.

- [ ] **Step 6: Run the full test suite**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj`

Expected: all tests pass, same count as before this task. This is the only check that the refactor preserved behaviour, so do not continue on a failure - fix it.

- [ ] **Step 7: Commit**

```bash
git add PenumbraOrganizer.Plugin/Windows/BrowserLink.cs PenumbraOrganizer.Plugin/Windows/HelpTab.cs
git commit -m "refactor: extract BrowserLink from HelpTab.OpenUrl

The title bar's link buttons need the same guarded browser launch, and
its try/catch exists specifically because the call runs inside an ImGui
frame. One copy rather than two.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 2: Add the two URL constants, test-first

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Windows/HelpTab.cs` (add constants after `DiscordUrl`, around line 57)
- Test: `PenumbraOrganizer.Plugin.Tests/Windows/HelpTabTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `HelpTab.GitHubUrl` and `HelpTab.KofiUrl`, both `public const string`. Task 3 reads both.

- [ ] **Step 1: Write the failing tests**

Append these to `HelpTabTests.cs`, inside the existing `HelpTabTests` class. They follow the file's established pattern: assert the shape, and assert the version string is absent the way `TheDiscordLink_IsAPermanentInvite_AndNotVersionPinned` already does.

```csharp
    [Fact]
    public void TheRepositoryLink_PointsAtThisRepoRoot_AndIsNotVersionPinned()
    {
        // Repo root, not /issues and not a deep link: the root is the front door and the issues
        // tab is one click from it. The version check is the inverse of GuideUrl's - pinning this
        // would be the bug, since an older build should still reach the live repository.
        Assert.Equal("https://github.com/monstersghost/PenumbraOrganizerPlugin", HelpTab.GitHubUrl);
        Assert.DoesNotContain("/blob/", HelpTab.GitHubUrl);
        Assert.DoesNotContain("/issues", HelpTab.GitHubUrl);

        var version = typeof(PenumbraOrganizer.Plugin.Plugin).Assembly.GetName().Version!.ToString(4);
        Assert.DoesNotContain(version, HelpTab.GitHubUrl);
    }

    [Fact]
    public void TheKofiLink_IsAbsoluteHttps_AndNotVersionPinned()
    {
        // Baked into every shipped binary, so it must stay valid for releases that can no longer
        // be changed - the same reasoning as the Discord invite.
        Assert.StartsWith("https://ko-fi.com/", HelpTab.KofiUrl);

        var version = typeof(PenumbraOrganizer.Plugin.Plugin).Assembly.GetName().Version!.ToString(4);
        Assert.DoesNotContain(version, HelpTab.KofiUrl);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~HelpTabTests"`

Expected: FAIL at compile time, with `CS0117 'HelpTab' does not contain a definition for 'GitHubUrl'` and the same for `KofiUrl`. A compile failure is the correct red here - there is nothing to run yet.

- [ ] **Step 3: Add the constants**

Insert into `HelpTab.cs` directly after the `DiscordUrl` declaration (around line 57), keeping the existing XML-comment style of its neighbours:

```csharp
    /// <summary>
    /// The repository root, for the title bar's GitHub button. The front door, deliberately, rather
    /// than a deep link to /issues - the issues tab is one click from here.
    /// </summary>
    /// <remarks>
    /// Like <see cref="DiscordUrl"/> and unlike <see cref="GuideUrl"/>, this is NOT version-pinned
    /// and must not be: an older build reaching the live repository is exactly what you want.
    /// </remarks>
    public const string GitHubUrl = "https://github.com/monstersghost/PenumbraOrganizerPlugin";

    /// <summary>
    /// The tip jar, for the title bar's Ko-fi button.
    /// </summary>
    /// <remarks>
    /// Not version-pinned, for <see cref="DiscordUrl"/>'s reason. A dead link here cannot be fixed
    /// in a release that has already shipped.
    /// </remarks>
    public const string KofiUrl = "https://ko-fi.com/K3K47YKER";
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj --filter "FullyQualifiedName~HelpTabTests"`

Expected: PASS, with two more tests than the previous run.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Windows/HelpTab.cs PenumbraOrganizer.Plugin.Tests/Windows/HelpTabTests.cs
git commit -m "feat: add repository and Ko-fi URL constants

Beside the existing guide and Discord links, so every outbound link in
the plugin stays in one file. Both unpinned, with tests asserting the
version string is absent.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 3: Wire the three title-bar buttons

The buttons themselves cannot be covered by a unit test - they need a real ImGui frame and a real Dalamud window host - so this task's gate is a clean build plus the Task 4 manual checklist.

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Windows/MainWindow.cs:97-107` (the constructor)
- Test: none. See Task 4.

**Interfaces:**
- Consumes: `BrowserLink.Open(string)` from Task 1; `HelpTab.GitHubUrl` and `HelpTab.KofiUrl` from Task 2; `HelpTab.DiscordUrl`, which already exists.
- Produces: nothing other tasks depend on.

**API notes, confirmed against the installed `Dalamud.dll` (Hooks/dev) rather than assumed:**

- `Window.TitleBarButtons` exists as a settable collection of `Window.TitleBarButton`.
- `TitleBarButton` has settable `Icon`, `IconOffset`, `Click` and `ShowTooltip`.
- `Click` takes the clicked mouse button, typed `ImGuiMouseButton` - so the lambda needs a discarded parameter, `_ =>`, not `() =>`.
- `FontAwesomeIcon` spells the GitHub member **`GitHub`**, with a capital H. `Github` does not exist and will not compile. `Discord` and `MugHot` both exist.

If the enum member names still do not resolve, read `Dalamud.Interface.Windowing.Window` and `Dalamud.Interface.FontAwesomeIcon` in your IDE and match them; do not guess a second time.

- [ ] **Step 1: Add the `using` for the icon enum**

`MainWindow.cs` already has `using Dalamud.Interface.Windowing;` at line 6, which covers `Window` and the nested `TitleBarButton`. It does **not** yet have the icon enum. Add to the using block at the top of the file:

```csharp
using Dalamud.Interface;
```

- [ ] **Step 2: Populate `TitleBarButtons` in the constructor**

Replace the constructor body at `MainWindow.cs:97-107` with this. The `SizeConstraints` block and `_plugin` assignment are unchanged - only the middle is new.

```csharp
    public MainWindow(Plugin plugin)
        : base($"Penumbra Organizer v{PluginVersion}###PenumbraOrganizerPluginMain")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(900, 480),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        // Left to right, so the two support links lead and the tip jar sits closest to Dalamud's
        // own pin and gear icons. Every click hands off to the system browser - nothing here
        // renders a page in game.
        //
        // Each Click takes the clicked mouse button, hence `_ =>` rather than `() =>`. The URLs
        // live on HelpTab beside the guide link, which is the one place outbound links belong.
        TitleBarButtons =
        [
            new()
            {
                Icon = FontAwesomeIcon.GitHub,
                IconOffset = new Vector2(2, 1),
                Click = _ => BrowserLink.Open(HelpTab.GitHubUrl),
                ShowTooltip = () => ImGui.SetTooltip("Open the plugin's GitHub repository"),
            },
            new()
            {
                Icon = FontAwesomeIcon.Discord,
                IconOffset = new Vector2(2, 1),
                Click = _ => BrowserLink.Open(HelpTab.DiscordUrl),
                ShowTooltip = () => ImGui.SetTooltip("Join the Discord for support"),
            },
            new()
            {
                Icon = FontAwesomeIcon.MugHot,
                IconOffset = new Vector2(2, 1),
                Click = _ => BrowserLink.Open(HelpTab.KofiUrl),
                ShowTooltip = () => ImGui.SetTooltip("Support the plugin on Ko-fi"),
            },
        ];

        _plugin = plugin;
    }
```

`IconOffset` nudges the glyph so it sits on the same baseline as Dalamud's own title-bar icons. If the icons look visibly misaligned in Task 4, this is the value to adjust - it is cosmetic and affects nothing else.

- [ ] **Step 3: Build**

Run: `dotnet build PenumbraOrganizer.Plugin.sln`

Expected: build succeeded, 0 errors.

Two likely failures and their fixes:
- `CS0117 'FontAwesomeIcon' does not contain a definition for 'GitHub'` - read the enum and use the real member name.
- `CS1593` or a delegate-mismatch error on `Click` - the parameter count is wrong; `Click` takes one argument, so use `_ =>`.

- [ ] **Step 4: Run the full test suite**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests/PenumbraOrganizer.Plugin.Tests.csproj`

Expected: all tests pass. Nothing here is covered by a new test, but `SmokeTests` and `LinkedFilesTests` will catch a construction-time break.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Windows/MainWindow.cs
git commit -m "feat: add GitHub, Discord and Ko-fi title-bar buttons

Three icon buttons left of Dalamud's pin, each handing off to the system
browser. Puts support one click from anywhere in the plugin rather than
only from the Help tab, which keeps its own buttons unchanged.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 4: Manual in-game checklist

The icon-font question cannot be settled by any automated test: whether Dalamud's loaded icon font carries FontAwesome's **brand** glyph range (`GitHub`, `Discord`) is only visible on screen. This task writes the checklist down so it is not carried in someone's head, and then runs it.

**Files:**
- Create: `docs/testing/2026-11-01-title-bar-links-checklist.md`
- Test: the checklist is the test.

**Interfaces:**
- Consumes: the built plugin from Tasks 1-3.
- Produces: nothing in code.

- [ ] **Step 1: Write the checklist**

```markdown
# Manual check: title-bar link buttons

Build and load the plugin in game, then open the Penumbra Organizer window.

## Rendering

- [ ] Three icons appear in the title bar, to the LEFT of Dalamud's pin and gear icons.
- [ ] None renders as an empty box or a question mark. A box means the loaded icon font does not
      carry FontAwesome's brand glyph range - see "If a glyph is missing" below.
- [ ] The icons sit on roughly the same baseline as Dalamud's own icons. If not, adjust
      `IconOffset` in `MainWindow`'s constructor; it is cosmetic only.
- [ ] Left to right, the order is GitHub, Discord, Ko-fi.

## Tooltips

- [ ] Hovering the first shows "Open the plugin's GitHub repository".
- [ ] Hovering the second shows "Join the Discord for support".
- [ ] Hovering the third shows "Support the plugin on Ko-fi".
- [ ] No two tooltips appear at once, and none flickers.

## Clicks

- [ ] First opens `https://github.com/monstersghost/PenumbraOrganizerPlugin` in the default browser.
- [ ] Second opens the Discord invite and it is still valid - it resolves to a server, not an
      "Invite Invalid" page.
- [ ] Third opens `https://ko-fi.com/K3K47YKER`.
- [ ] The game does not hitch, freeze or crash on any click.

## No regressions

- [ ] The window still pins, collapses, resizes and closes as before.
- [ ] The Help tab still has its own "Join the Discord for support" and "Open the full guide in
      your browser" buttons, and both still work.

## If a glyph is missing

Replace the brand icon with a solid-style one in `MainWindow`'s constructor and rebuild:
`FontAwesomeIcon.Code` for GitHub, `FontAwesomeIcon.Comments` for Discord. `MugHot` is already
solid, so Ko-fi needs no fallback. A generic but legible icon beats a box glyph.
```

- [ ] **Step 2: Commit the checklist**

```bash
git add docs/testing/2026-11-01-title-bar-links-checklist.md
git commit -m "docs: manual checklist for the title-bar link buttons

The brand-glyph question is only answerable on screen, so the check is
written down rather than carried in someone's head.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

- [ ] **Step 3: Run the checklist in game**

Build, load, and walk the list above. Report which boxes passed and which did not - do not report the feature complete on an unrun checklist. If a glyph was missing and you applied the fallback, say which icons you ended up with.
