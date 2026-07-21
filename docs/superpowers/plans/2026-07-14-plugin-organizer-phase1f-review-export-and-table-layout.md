# Phase 1f: Review Export and Table Layout Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a full-state text export button to the Review Changes tab, and fix the Review Changes
table's column clipping, per the approved spec
`docs/superpowers/specs/2026-07-14-plugin-organizer-phase1f-review-export-and-table-layout-design.md`.

**Architecture:** A new pure, static class `OrganizerExportFormatter.Format(mods, validation) : string`
produces the full export text from data `OrganizerState` already exposes — no new data source. A new
`Plugin.ExportReview()` writes that text to a fixed file in the plugin's config directory and returns
the path; a new **Export** button on the Review Changes tab calls it. Separately, two small,
independent changes fix the table's column clipping: `PathTreeView`'s table gets proportional +
resizable sizing flags, and `MainWindow`'s minimum width increases.

**Tech Stack:** C# / .NET 10, `Dalamud.NET.Sdk/15.0.0`, xunit (existing test project
`PenumbraOrganizer.Plugin.Tests`).

## Global Constraints

- No change to `OrganizerState`, `CollisionDisambiguator`, or any sort strategy — both pieces of this
  plan are read-only consumers of already-computed state.
- No file-save dialog — fixed filename (`organizer-export.txt`) in `PluginInterface.ConfigDirectory`,
  overwritten each export, path shown in the UI after a successful export.
- No write IPC of any kind — this phase remains read-only; Apply stays disabled.
- The export format is exactly the labeled-block format in the spec's "Export format" section — do not
  invent a different structure (CSV, JSON, etc.).
- Build must stay at 0 warnings / 0 errors; all existing 113 tests must keep passing (this plan adds 6
  new tests, all in Task 1; treat the actual `dotnet test` summary line as ground truth over any
  arithmetic in this plan if they ever disagree).
- `docs/HANDOFF_ORGANIZER_AND_DARK_THEME.md`'s test-count line currently reads "112" — this is already
  stale (a regression test was added after that line was last updated, bringing the real pre-Phase-1f
  count to 113). Task 2's doc update corrects it to the final post-Phase-1f total, which supersedes
  both the stale "112" and the intermediate "113".
- Run all commands from the repo root `C:\Repo\PenumbraOrganizer.Plugin`.

---

### Task 1: `OrganizerExportFormatter`

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/OrganizerExportFormatter.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerExportFormatterTests.cs`

**Interfaces:**
- Consumes: `OrganizerModRow` (existing), `ReviewResult` (existing, defined in `OrganizerState.cs`) —
  both in the `PenumbraOrganizer.Plugin.Organizer` namespace, same namespace this new class lives in,
  no new `using` needed for either.
- Produces: `static string OrganizerExportFormatter.Format(IReadOnlyList<OrganizerModRow> mods, ReviewResult validation)`
  — consumed by Task 2's `Plugin.ExportReview()`.

- [ ] **Step 1: Write the failing tests**

Create `PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerExportFormatterTests.cs`:

```csharp
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class OrganizerExportFormatterTests
{
    private static OrganizerModRow MakeRow(
        string id, string name, bool isProtected = false, bool heliosphere = false,
        ModCategory? category = null, string? subCategory = null) => new()
    {
        Identifier = id,
        Name = name,
        Author = "SomeAuthor",
        CurrentPath = $"Unsorted/{name}",
        ProposedPath = $"Unsorted/{name}",
        Protected = isProtected,
        HeliosphereManaged = heliosphere,
        Category = category,
        SubCategory = subCategory,
    };

    [Fact]
    public void Format_EmptyInput_ProducesZeroCountsAndNoSections()
    {
        var result = OrganizerExportFormatter.Format([], new ReviewResult([], new Dictionary<string, List<string>>()));

        Assert.Contains("Total mods: 0", result);
        Assert.Contains("Protected: 0", result);
        Assert.Contains("Collisions: 0", result);
        Assert.Contains("Protected violations: (none)", result);
        Assert.Contains("Path collisions: (none)", result);
    }

    [Fact]
    public void Format_FullyPopulatedMod_IncludesEveryField()
    {
        var row = MakeRow("a", "Cool Jacket", isProtected: true, heliosphere: true,
            category: ModCategory.Gear, subCategory: "Battle Animation");

        var result = OrganizerExportFormatter.Format([row], new ReviewResult([], new Dictionary<string, List<string>>()));

        Assert.Contains("Identifier: a", result);
        Assert.Contains("Name: Cool Jacket", result);
        Assert.Contains("Author: SomeAuthor", result);
        Assert.Contains("Category: Gear", result);
        Assert.Contains("SubCategory: Battle Animation", result);
        Assert.Contains("HeliosphereManaged: True", result);
        Assert.Contains("Protected: True", result);
        Assert.Contains("CurrentPath: Unsorted/Cool Jacket", result);
        Assert.Contains("ProposedPath: Unsorted/Cool Jacket", result);
    }

    [Fact]
    public void Format_NullCategoryAndSubCategory_RendersAsNone()
    {
        var row = MakeRow("a", "Mystery Mod");

        var result = OrganizerExportFormatter.Format([row], new ReviewResult([], new Dictionary<string, List<string>>()));

        Assert.Contains("Category: (none)", result);
        Assert.Contains("SubCategory: (none)", result);
    }

    [Fact]
    public void Format_ProtectedViolations_ListsIdentifiers()
    {
        var result = OrganizerExportFormatter.Format([], new ReviewResult(["a", "b"], new Dictionary<string, List<string>>()));

        Assert.Contains("Protected violations: a, b", result);
    }

    [Fact]
    public void Format_PathCollisions_ListsPathAndIdentifiers()
    {
        var collisions = new Dictionary<string, List<string>> { ["Shared/Same"] = ["a", "b"] };

        var result = OrganizerExportFormatter.Format([], new ReviewResult([], collisions));

        Assert.Contains("'Shared/Same': a, b", result);
    }

    [Fact]
    public void Format_CountsMatchInput()
    {
        var rows = new[]
        {
            MakeRow("a", "Apple", isProtected: true),
            MakeRow("b", "Banana"),
            MakeRow("c", "Cherry"),
        };
        var collisions = new Dictionary<string, List<string>> { ["Shared/Same"] = ["b", "c"] };

        var result = OrganizerExportFormatter.Format(rows, new ReviewResult([], collisions));

        Assert.Contains("Total mods: 3", result);
        Assert.Contains("Protected: 1", result);
        Assert.Contains("Collisions: 1", result);
    }
}
```

Note: no test asserts the exact `Generated:` timestamp line — only that the other counts/sections are
correct. The timestamp is real wall-clock time (`DateTime.Now`), not a parameter, matching the spec's
signature exactly; don't add a timestamp parameter to `Format` to make it "more testable" — that would
be a spec deviation.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~OrganizerExportFormatterTests"`
Expected: compilation failure — `OrganizerExportFormatter` does not exist.

- [ ] **Step 3: Write the implementation**

Create `PenumbraOrganizer.Plugin/Organizer/OrganizerExportFormatter.cs`:

```csharp
using System.Text;

namespace PenumbraOrganizer.Plugin.Organizer;

public static class OrganizerExportFormatter
{
    public static string Format(IReadOnlyList<OrganizerModRow> mods, ReviewResult validation)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== Penumbra Organizer Export ===");
        sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Total mods: {mods.Count}");
        sb.AppendLine($"Protected: {mods.Count(m => m.Protected)}");
        sb.AppendLine($"Collisions: {validation.PathCollisions.Count}");
        sb.AppendLine();
        sb.AppendLine("--- Mods ---");

        foreach (var mod in mods)
        {
            sb.AppendLine($"Identifier: {mod.Identifier}");
            sb.AppendLine($"Name: {mod.Name}");
            sb.AppendLine($"Author: {mod.Author}");
            sb.AppendLine($"Category: {(mod.Category is null ? "(none)" : mod.Category.Value.ToString())}");
            sb.AppendLine($"SubCategory: {mod.SubCategory ?? "(none)"}");
            sb.AppendLine($"HeliosphereManaged: {mod.HeliosphereManaged}");
            sb.AppendLine($"Protected: {mod.Protected}");
            sb.AppendLine($"CurrentPath: {mod.CurrentPath}");
            sb.AppendLine($"ProposedPath: {mod.ProposedPath}");
            sb.AppendLine();
        }

        sb.AppendLine("--- Validate() ---");
        sb.AppendLine($"Protected violations: {(validation.ProtectedViolations.Count == 0 ? "(none)" : string.Join(", ", validation.ProtectedViolations))}");

        if (validation.PathCollisions.Count == 0)
        {
            sb.AppendLine("Path collisions: (none)");
        }
        else
        {
            sb.AppendLine("Path collisions:");
            foreach (var (path, identifiers) in validation.PathCollisions)
                sb.AppendLine($"  '{path}': {string.Join(", ", identifiers)}");
        }

        return sb.ToString();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~OrganizerExportFormatterTests"`
Expected: all 6 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/OrganizerExportFormatter.cs PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerExportFormatterTests.cs
git commit -m "feat(1f): add OrganizerExportFormatter"
```

---

### Task 2: Export button, table layout fix, docs

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Plugin.cs` (add `ExportReview()`)
- Modify: `PenumbraOrganizer.Plugin/Windows/MainWindow.cs:12-31` (new field, `MinimumSize`) and `:163-187` (`DrawReviewTab`)
- Modify: `PenumbraOrganizer.Plugin/Windows/PathTreeView.cs:13-15` (table flags)
- Modify: `docs/HANDOFF_ORGANIZER_AND_DARK_THEME.md`
- Modify: `docs/ROADMAP.md`

**Interfaces:**
- Consumes: `OrganizerExportFormatter.Format(IReadOnlyList<OrganizerModRow>, ReviewResult)` from Task 1.
- Produces: `internal string Plugin.ExportReview()`. No new test — this task has no unit-testable logic
  (file I/O needs a running plugin config directory; the table/window changes are pure ImGui layout).
  Verified in-game only, per the plan's Global Constraints.

- [ ] **Step 1: Add `Plugin.ExportReview()`**

In `PenumbraOrganizer.Plugin/Plugin.cs`, add below `SaveProtectionState` (still inside the `Plugin`
class, before the closing brace):

```csharp
    internal string ExportReview()
    {
        var content = Organizer.OrganizerExportFormatter.Format(OrganizerState.Mods, OrganizerState.Validate());
        var path = Path.Combine(PluginInterface.ConfigDirectory.FullName, "organizer-export.txt");
        Directory.CreateDirectory(PluginInterface.ConfigDirectory.FullName);
        File.WriteAllText(path, content);
        return path;
    }
```

`Organizer.OrganizerExportFormatter` (not a bare `OrganizerExportFormatter`) matches this file's
existing convention of referencing types in the `Organizer` namespace via the `Organizer.` prefix
rather than adding a `using` (see `Organizer.OrganizerState`, `Organizer.OrganizerModRow`,
`Organizer.HeliosphereDetector` elsewhere in this same file). `Path`/`Directory`/`File` need no new
`using` — `System.IO` is already implicitly available in this project (the removed Phase 1c spike
button used the same three types with no explicit import).

- [ ] **Step 2: Add the Export button and path display**

In `PenumbraOrganizer.Plugin/Windows/MainWindow.cs`, add a new field alongside the existing ones
(after `private string? _selectedManualModIdentifier;`):

```csharp
    private string? _lastExportPath;
```

Replace `DrawReviewTab()`'s body from `ImGui.Spacing();` (the one immediately after
`PathTreeView.Draw(...)`) through the end of the method:

```csharp
        ImGui.Spacing();
        if (ImGui.Button("Export"))
            _lastExportPath = _plugin.ExportReview();

        if (_lastExportPath is not null)
        {
            ImGui.SameLine();
            ImGui.TextUnformatted($"Exported to: {_lastExportPath}");
        }

        ImGui.Spacing();
        ImGui.BeginDisabled();
        ImGui.Button("Apply (disabled in Phase 1)");
        ImGui.EndDisabled();
    }
```

- [ ] **Step 3: Fix the table sizing**

In `PenumbraOrganizer.Plugin/Windows/PathTreeView.cs`, replace the table-creation lines:

```csharp
        using var table = ImRaii.Table("PathTreeView", columnCount,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY,
            new System.Numerics.Vector2(0, 300));
```

with:

```csharp
        using var table = ImRaii.Table("PathTreeView", columnCount,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY
                | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp,
            new System.Numerics.Vector2(0, 300));
```

- [ ] **Step 4: Widen the window's minimum size**

In `PenumbraOrganizer.Plugin/Windows/MainWindow.cs`, change:

```csharp
            MinimumSize = new Vector2(640, 480),
```

to:

```csharp
            MinimumSize = new Vector2(900, 480),
```

- [ ] **Step 5: Build and run the full test suite**

Run: `dotnet build && dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)` and `Passed! - Failed: 0, Passed: 119` (113
pre-existing + 6 from Task 1 — recount from the actual test-runner summary line if it differs).

- [ ] **Step 6: Update the handoff doc**

In `docs/HANDOFF_ORGANIZER_AND_DARK_THEME.md`, the test-count line currently reads (note: it says
"112", already stale — see this plan's Global Constraints):

```markdown
112 tests pass (`dotnet test PenumbraOrganizer.Plugin.Tests`), build is clean.
```

Change it to:

```markdown
119 tests pass (`dotnet test PenumbraOrganizer.Plugin.Tests`), build is clean.
```

(Use the actual test-runner total from Step 5 if it differs from 119.)

Then, in the same file's `## Known limitations, not fixed here` section, add a new bullet after the
Phase 1e bullet (which currently ends with `...phase1e-combined-sort-strategies-design.md`.`) and
before the `**The window's title bar...` bullet:

```markdown
- **Phase 1f (Review export + table layout) is implemented.** The Review Changes tab has an "Export"
  button that writes a full-state text snapshot (every mod field, plus the current `Validate()`
  result) to `organizer-export.txt` in the plugin's config directory, overwritten each time. The
  Review Changes table now uses proportional + resizable column sizing, and the window's minimum
  width increased to 900px, fixing long `ProposedPath` values being clipped with no way to see them.
  Design: `docs/superpowers/specs/2026-07-14-plugin-organizer-phase1f-review-export-and-table-layout-design.md`.
```

- [ ] **Step 7: Update the roadmap**

In `docs/ROADMAP.md`, the `## Where we are` section currently ends with the Phase 1e bullet
(`...under one \`Review/{Name}\` rule.`). Add immediately after it:

```markdown
- **Phase 1f (Review export + table layout) — shipped.** Adds an Export button (full-state text
  snapshot) to the Review Changes tab; fixes the table's column clipping.
```

Then, still in `docs/ROADMAP.md`, the `## Phase 1e — done` section is immediately followed by
`## Phase 2 — Apply ...`. Insert a new section between them:

```markdown
## Phase 1f — done

Shipped: `OrganizerExportFormatter` (`PenumbraOrganizer.Plugin/Organizer/OrganizerExportFormatter.cs`)
plus an Export button on the Review Changes tab, writing a full-state snapshot to
`organizer-export.txt` in the plugin config directory. Also fixed `PathTreeView`'s table column
clipping (proportional + resizable sizing) and widened `MainWindow`'s minimum width to 900px. Design:
`docs/superpowers/specs/2026-07-14-plugin-organizer-phase1f-review-export-and-table-layout-design.md`.
Plan: `docs/superpowers/plans/2026-07-14-plugin-organizer-phase1f-review-export-and-table-layout.md`.
```

- [ ] **Step 8: Commit**

```bash
git add PenumbraOrganizer.Plugin/Plugin.cs PenumbraOrganizer.Plugin/Windows/MainWindow.cs PenumbraOrganizer.Plugin/Windows/PathTreeView.cs docs/HANDOFF_ORGANIZER_AND_DARK_THEME.md docs/ROADMAP.md
git commit -m "feat(1f): add Export button and fix Review Changes table clipping"
```

- [ ] **Step 9: In-game verification (user-assisted, cannot be automated)**

Ask the user to load the rebuilt dev plugin and confirm, in order:
1. **Scan tab** → "Refresh mod list" completes without error (no IPC/scan behavior changed by this
   plan — this just confirms nothing broke).
2. **Review Changes tab** → click **Export**. Confirm the displayed path exists on disk, and its
   contents match the format from Task 1's tests: a header with correct `Total mods`/`Protected`/
   `Collisions` counts, one labeled block per mod, and a `--- Validate() ---` section.
3. Resize the main window narrower and wider — confirm the four-column table redistributes width
   proportionally instead of clipping, and that dragging a column border resizes it.
4. Confirm a long `ProposedPath` (e.g. an `Animation and VFX/Emotes/...` entry) is now either fully
   visible or resizable into full visibility, unlike before this plan.

Expected: all four checks pass.

---

## Self-review notes

- **Spec coverage:** export format (every field, summary counts, `Validate()` section) — Task 1's
  implementation and tests. Fixed-filename/overwrite/shown-path convention — Task 2 Step 1-2. Table
  proportional+resizable sizing and window width — Task 2 Steps 3-4. Docs — Task 2 Steps 6-7. Non-goals
  (no file dialog, no tooltips, no column restructuring, no `OrganizerState`/`CollisionDisambiguator`
  change) are honored by construction — nothing in either task touches those.
- **Type consistency check:** `OrganizerExportFormatter.Format(IReadOnlyList<OrganizerModRow> mods, ReviewResult validation) : string`
  — same signature in Task 1's implementation and Task 2's one call site (`Plugin.ExportReview()`).
  `Plugin.ExportReview() : string` — return type matches what `MainWindow._lastExportPath` (a
  `string?`) is assigned from.
- **No placeholders:** every step has complete, runnable code; doc-update steps show exact before/after
  text.
