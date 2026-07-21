# Plugin UI Notes Batch Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement seven user-requested UI/operational notes from the 2026-07-19 session: a Protect-All toggle, plugin versioning, in-game (non-blocking) file dialogs replacing Windows Explorer dialogs, an Open Workbook button with a save-destination picker, a flat vs. detailed "By Mod Type" sort split, the plugin icon, and a `repo.json` for eventual self-hosted distribution.

**Architecture:** Each note is a small, independently testable change layered onto the existing `OrganizerState` / `Plugin` / `MainWindow` structure. Tasks 3 and 4 share infrastructure (Task 3 introduces `Dalamud.Interface.ImGuiFileDialog.FileDialogManager` and converts the existing Import Workbook flow to it; Task 4 builds Open Workbook + Export's save picker on top of that same instance). All other tasks are fully independent and can be done in any order.

**Tech Stack:** C# / .NET (Dalamud.NET.Sdk 15.0.0), ImGui via `Dalamud.Bindings.ImGui`, xUnit for tests, `Dalamud.Interface.ImGuiFileDialog.FileDialogManager` (Dalamud's own non-blocking in-game file dialog — the same one Penumbra itself uses, confirmed via `Penumbra/UI/Classes/FileDialogService.cs` and Dalamud's own `Dalamud/Interface/ImGuiFileDialog/FileDialogManager.cs` source).

## Global Constraints

- Never use `System.Windows.Forms.OpenFileDialog`/`SaveFileDialog` (or any other native Windows dialog) for file selection — it blocks the game's main thread while open and has caused real disconnects. Use `FileDialogManager` instead (user's explicit instruction, 2026-07-19).
- Follow existing code conventions exactly: `ImRaii`/`ImGuiColors` usage, try/catch-with-`_lastError` pattern for any `Plugin` call from the UI, TDD for anything in `Organizer/` (pure, testable code), no tests for `Plugin.cs`/`MainWindow.cs` methods that touch live IPC or file I/O directly (matches the existing, deliberate convention — see `RunScan`/`ApplyChanges`/`ExportReview`, none of which are unit tested either).
- Run `dotnet build PenumbraOrganizer.Plugin.sln` and `dotnet test PenumbraOrganizer.Plugin.Tests` after every task — both must be clean (0 warnings/errors, all tests passing) before moving on.
- Working directory for all commands: `C:\Repo\PenumbraOrganizer.Plugin`.

---

## Task 1: Protect All toggle button

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs`
- Modify: `PenumbraOrganizer.Plugin/Windows/MainWindow.cs:110-124` (`DrawProtectTab`)
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateTests.cs`

**Interfaces:**
- Produces: `OrganizerState.SetAllProtection(bool value)` — sets `Protected = value` on every mod currently loaded, regardless of Heliosphere status (mirrors the existing `SetHeliosphereProtection` pattern; the next `LoadScan` still re-forces Heliosphere protection per the existing, unchanged rule at `OrganizerState.cs:20-23`).

- [ ] **Step 1: Write the failing tests**

Add to `PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateTests.cs` (append near the other `SetProtected`/`SetHeliosphereProtection` tests — search the file for `SetHeliosphereProtection` to find the right neighborhood):

```csharp
[Fact]
public void SetAllProtection_True_ProtectsEveryMod()
{
    var state = new OrganizerState();
    state.LoadScan([MakeRow("a", "Apple"), MakeRow("b", "Banana")], new HashSet<string>());

    state.SetAllProtection(true);

    Assert.All(state.Mods, m => Assert.True(m.Protected));
}

[Fact]
public void SetAllProtection_False_UnprotectsEveryMod()
{
    var state = new OrganizerState();
    state.LoadScan([MakeRow("a", "Apple"), MakeRow("b", "Banana", heliosphere: true)], new HashSet<string>());
    state.SetAllProtection(true);

    state.SetAllProtection(false);

    Assert.All(state.Mods, m => Assert.False(m.Protected));
}

[Fact]
public void SetAllProtection_EmptyLibrary_DoesNotThrow()
{
    var state = new OrganizerState();
    state.LoadScan([], new HashSet<string>());

    state.SetAllProtection(true);

    Assert.Empty(state.Mods);
}
```

This file already has a `MakeRow(string id, string name, bool heliosphere = false)` helper near the top — reuse it, do not redefine it.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~SetAllProtection"`
Expected: FAIL — `'OrganizerState' does not contain a definition for 'SetAllProtection'` (compile error, which counts as red for this trivial a method).

- [ ] **Step 3: Implement `SetAllProtection`**

In `PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs`, add immediately after `SetHeliosphereProtection`:

```csharp
    public void SetAllProtection(bool value)
    {
        foreach (var row in _mods.Values)
            row.Protected = value;
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~OrganizerStateTests"`
Expected: PASS, all tests in the file (existing + 3 new).

- [ ] **Step 5: Wire the UI button**

In `PenumbraOrganizer.Plugin/Windows/MainWindow.cs`, `DrawProtectTab` currently reads:

```csharp
    private void DrawProtectTab()
    {
        using var tab = ImRaii.TabItem("Protect");
        if (!tab)
            return;

        if (ImGui.Button("Toggle Heliosphere protection"))
        {
            var allProtected = _plugin.OrganizerState.Mods
                .Where(m => m.HeliosphereManaged)
                .All(m => m.Protected);
            _plugin.OrganizerState.SetHeliosphereProtection(!allProtected);
            _plugin.SaveProtectionState();
        }

        ImGui.Spacing();
```

Replace with (adds the new button before the existing one, `SameLine`-joined):

```csharp
    private void DrawProtectTab()
    {
        using var tab = ImRaii.TabItem("Protect");
        if (!tab)
            return;

        if (ImGui.Button("Toggle protect all"))
        {
            var allProtected = _plugin.OrganizerState.Mods.All(m => m.Protected);
            _plugin.OrganizerState.SetAllProtection(!allProtected);
            _plugin.SaveProtectionState();
        }

        ImGui.SameLine();
        if (ImGui.Button("Toggle Heliosphere protection"))
        {
            var allProtected = _plugin.OrganizerState.Mods
                .Where(m => m.HeliosphereManaged)
                .All(m => m.Protected);
            _plugin.OrganizerState.SetHeliosphereProtection(!allProtected);
            _plugin.SaveProtectionState();
        }

        ImGui.Spacing();
```

(The rest of the method — the per-mod checkbox loop — is unchanged.)

- [ ] **Step 6: Build and full test run**

Run: `dotnet build PenumbraOrganizer.Plugin.sln` — expect 0 warnings/errors.
Run: `dotnet test PenumbraOrganizer.Plugin.Tests` — expect all green.

- [ ] **Step 7: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs PenumbraOrganizer.Plugin/Windows/MainWindow.cs PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateTests.cs
git commit -m "feat: add Protect All toggle button"
```

---

## Task 2: Plugin versioning (0.4.0) and version in the window title

**Files:**
- Modify: `PenumbraOrganizer.Plugin/PenumbraOrganizer.Plugin.csproj`
- Modify: `PenumbraOrganizer.Plugin/Windows/MainWindow.cs:42-44` (constructor)

**Interfaces:**
- Produces: `<Version>0.4.0.0</Version>` in the csproj (four-part, matching .NET `AssemblyVersion` conventions and the `0.4.0.x` scheme the user asked for — `0.4.0.0` is the release baseline; bump only the 4th component for test builds, e.g. `0.4.0.1`, `0.4.0.2`, never touch the first three until the next real release).

- [ ] **Step 1: Bump the version**

In `PenumbraOrganizer.Plugin/PenumbraOrganizer.Plugin.csproj`, change:

```xml
    <Version>0.1.0</Version>
```

to:

```xml
    <Version>0.4.0.0</Version>
```

- [ ] **Step 2: Show the version in the window title**

In `PenumbraOrganizer.Plugin/Windows/MainWindow.cs`, the constructor currently reads:

```csharp
    public MainWindow(Plugin plugin)
        : base("Penumbra Organizer###PenumbraOrganizerPluginMain")
    {
```

The part after `###` is ImGui's stable window ID and must never change; the part before it is the freely-changeable displayed title. Replace with:

```csharp
    public MainWindow(Plugin plugin)
        : base($"Penumbra Organizer v{PluginVersion}###PenumbraOrganizerPluginMain")
    {
```

Add a `private static readonly string PluginVersion` field right above the constructor (after the `WorkbookStrategyOptions` array, before `public MainWindow`):

```csharp
    private static readonly string PluginVersion =
        typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "unknown";
```

`ToString(3)` renders the first three components (`0.4.0`), which is what a user wants to see — the 4th test-build component is deliberately hidden from the title to keep it clean; it's still visible via the assembly/DLL properties if ever needed.

- [ ] **Step 3: Build and verify**

Run: `dotnet build PenumbraOrganizer.Plugin.sln` — expect 0 warnings/errors.
Run: `dotnet test PenumbraOrganizer.Plugin.Tests` — expect all green (no test touches this).

There is no automated way to see the window title outside the game — verify visually next time you're in-game that the window reads "Penumbra Organizer v0.4.0".

- [ ] **Step 4: Commit**

```bash
git add PenumbraOrganizer.Plugin/PenumbraOrganizer.Plugin.csproj PenumbraOrganizer.Plugin/Windows/MainWindow.cs
git commit -m "feat: version the plugin starting at 0.4.0, show it in the window title"
```

---

## Task 3: In-game file dialogs (infrastructure) — replace Import Workbook's Windows dialog

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Windows/MainWindow.cs`
- Modify: `PenumbraOrganizer.Plugin/PenumbraOrganizer.Plugin.csproj` (remove `UseWindowsForms`, now unused)

**Interfaces:**
- Produces: a `private readonly FileDialogManager _fileDialogManager = new();` field on `MainWindow`, drawn every frame via `_fileDialogManager.Draw()` at the end of `Draw()`. Task 4 reuses this exact field — do not create a second instance.
- Ground truth for the API used here (verified directly against Dalamud's own source, `Dalamud/Interface/ImGuiFileDialog/FileDialogManager.cs`, not inferred): `OpenFileDialog(string title, string filters, Action<bool, List<string>> callback, int selectionCountMax, string? startPath = null, bool isModal = false)`. `filters` is a plain extension string like `".xlsx"` (confirmed against Dalamud's own internal usage in `DevPluginsSettingsEntry.cs`, which passes `".dll"` the same way — no braces, no ImGuiFileDialog-style filter-group syntax needed for a single extension).

- [ ] **Step 1: Add the `FileDialogManager` field and wire `Draw()`**

In `PenumbraOrganizer.Plugin/Windows/MainWindow.cs`, add the using directive at the top (alphabetical with the existing `Dalamud.*` usings):

```csharp
using Dalamud.Interface.ImGuiFileDialog;
```

Add the field, right after the existing `_npcRefreshResult` field declaration (end of the field block, before `WorkbookStrategyOptions`):

```csharp
    private readonly FileDialogManager _fileDialogManager = new();
```

In `Draw()`, currently:

```csharp
    public override void Draw()
    {
        using var theme = PluginTheme.Push();

        if (_lastError != null)
            ImGui.TextColored(ImGuiColors.DalamudRed, _lastError);

        using var tabBar = ImRaii.TabBar("MainTabs");
        if (!tabBar)
            return;

        DrawScanTab();
        DrawProtectTab();
        DrawSortTab();
        DrawReviewTab();
    }
```

Add the dialog draw call — it must run every frame regardless of the tab bar (a dialog opened from one tab should keep rendering even if `tabBar` fails for any reason), so place it after the tab bar block, not inside it:

```csharp
    public override void Draw()
    {
        using var theme = PluginTheme.Push();

        if (_lastError != null)
            ImGui.TextColored(ImGuiColors.DalamudRed, _lastError);

        using (var tabBar = ImRaii.TabBar("MainTabs"))
        {
            if (tabBar)
            {
                DrawScanTab();
                DrawProtectTab();
                DrawSortTab();
                DrawReviewTab();
            }
        }

        _fileDialogManager.Draw();
    }
```

(This changes the early `return` inside the `if (!tabBar) return;` into an `if (tabBar) { ... }` block, since the dialog draw call must still run when the tab bar itself fails to open — an edge case ImGui can hit on a malformed frame, and worth being defensive about since a stuck-open file dialog would otherwise become unclosable.)

- [ ] **Step 2: Convert the Import Workbook button**

In `DrawSortTab`, currently:

```csharp
        ImGui.SameLine();
        if (ImGui.Button("Import Workbook"))
        {
            using var dialog = new System.Windows.Forms.OpenFileDialog { Filter = "Excel Workbook (*.xlsx)|*.xlsx" };
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                ImportWorkbook(dialog.FileName);
        }
```

Replace with:

```csharp
        ImGui.SameLine();
        if (ImGui.Button("Import Workbook"))
        {
            _fileDialogManager.OpenFileDialog(
                "Import Workbook",
                ".xlsx",
                (success, paths) =>
                {
                    if (success && paths.Count > 0)
                        ImportWorkbook(paths[0]);
                },
                selectionCountMax: 1);
        }
```

- [ ] **Step 3: Remove the now-unused Windows Forms dependency**

Confirm nothing else in the project still references `System.Windows.Forms`:

Run: `grep -rn "System.Windows.Forms" PenumbraOrganizer.Plugin/`
Expected: no matches (Task 4 will add the Export save picker using the same `FileDialogManager`, not Windows Forms).

If clean, remove the now-unused SDK flag from `PenumbraOrganizer.Plugin/PenumbraOrganizer.Plugin.csproj`:

```xml
    <UseWindowsForms>true</UseWindowsForms>
```

Delete this line entirely.

- [ ] **Step 4: Build and verify**

Run: `dotnet build PenumbraOrganizer.Plugin.sln` — expect 0 warnings/errors. If removing `UseWindowsForms` causes a build error, put the line back and leave a one-line comment above it noting it's currently unused but required by an SDK quirk — do not spend more than one build cycle chasing this; it's a minor cleanup, not the point of this task.
Run: `dotnet test PenumbraOrganizer.Plugin.Tests` — expect all green.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/Windows/MainWindow.cs PenumbraOrganizer.Plugin/PenumbraOrganizer.Plugin.csproj
git commit -m "feat: replace Windows Explorer file dialog with in-game FileDialogManager"
```

---

## Task 4: Open Workbook button + save-destination picker on Export

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Plugin.cs:167,181-197` (`WorkbookFilePath`, `ExportWorkbook`)
- Modify: `PenumbraOrganizer.Plugin/Windows/MainWindow.cs` (`DrawReviewTab`'s Export Workbook button, `ExportWorkbook` wrapper)

**Interfaces:**
- Consumes: `_fileDialogManager` field from Task 3 (must be done first — this task's Export button uses `_fileDialogManager.SaveFileDialog`).
- Produces: `Plugin.ExportWorkbook(OrganizationStrategy strategy, string destinationPath)` — signature changes from the current single-argument form to take an explicit destination (the caller now always supplies it, via the save dialog). `Plugin.DefaultWorkbookFileName` (new `internal const string` — just the filename, no directory, used to seed the save dialog's default filename).

- [ ] **Step 1: Change `Plugin.ExportWorkbook` to accept a destination path**

In `PenumbraOrganizer.Plugin/Plugin.cs`, currently:

```csharp
    private string WorkbookFilePath => Path.Combine(PluginInterface.ConfigDirectory.FullName, "organizer-workbook.xlsx");
```

and:

```csharp
    internal string ExportWorkbook(OrganizationStrategy strategy)
    {
        var inventory = Organizer.WorkbookAdapter.ToScanInventory(OrganizerState, BuildInstallation());
        var proposals = Organizer.WorkbookAdapter.ToProposals(OrganizerState);
        var preferences = Organizer.WorkbookAdapter.ToOrganizationPreferences(strategy);

        // ClosedXML's SaveAs validates the file extension and rejects anything but
        // .xlsx/.xlsm/.xltx/.xltm, so the temp name must keep .xlsx as its actual extension -
        // "organizer-workbook.xlsx.tmp" fails, "organizer-workbook.tmp.xlsx" doesn't.
        var tempPath = Path.Combine(
            Path.GetDirectoryName(WorkbookFilePath)!,
            $"{Path.GetFileNameWithoutExtension(WorkbookFilePath)}.tmp{Path.GetExtension(WorkbookFilePath)}");
        var export = _workbookService.ExportAsync(inventory, proposals, preferences, tempPath, CancellationToken.None)
            .GetAwaiter().GetResult();
        File.Move(export.WorkbookPath, WorkbookFilePath, overwrite: true);
        return WorkbookFilePath;
    }
```

Replace both with:

```csharp
    internal const string DefaultWorkbookFileName = "organizer-workbook.xlsx";

    private string DefaultWorkbookFilePath => Path.Combine(PluginInterface.ConfigDirectory.FullName, DefaultWorkbookFileName);

    internal string ExportWorkbook(OrganizationStrategy strategy, string destinationPath)
    {
        var inventory = Organizer.WorkbookAdapter.ToScanInventory(OrganizerState, BuildInstallation());
        var proposals = Organizer.WorkbookAdapter.ToProposals(OrganizerState);
        var preferences = Organizer.WorkbookAdapter.ToOrganizationPreferences(strategy);

        // ClosedXML's SaveAs validates the file extension and rejects anything but
        // .xlsx/.xlsm/.xltx/.xltm, so the temp name must keep .xlsx as its actual extension -
        // "organizer-workbook.xlsx.tmp" fails, "organizer-workbook.tmp.xlsx" doesn't.
        var tempPath = Path.Combine(
            Path.GetDirectoryName(destinationPath)!,
            $"{Path.GetFileNameWithoutExtension(destinationPath)}.tmp{Path.GetExtension(destinationPath)}");
        var export = _workbookService.ExportAsync(inventory, proposals, preferences, tempPath, CancellationToken.None)
            .GetAwaiter().GetResult();
        File.Move(export.WorkbookPath, destinationPath, overwrite: true);
        return destinationPath;
    }
```

`DefaultWorkbookFilePath` keeps the plugin-config-directory default alive for the save dialog's start location; `DefaultWorkbookFileName` is exposed so `MainWindow` can seed the dialog's default filename without duplicating the literal string.

- [ ] **Step 2: Rewire the Export Workbook button and its wrapper in `MainWindow.cs`**

Currently (`DrawReviewTab`):

```csharp
        ImGui.SameLine();
        if (ImGui.Button("Export Workbook"))
            ExportWorkbook(WorkbookStrategyOptions[_workbookStrategyIndex].Strategy);

        if (_lastWorkbookExportPath is not null)
        {
            ImGui.SameLine();
            ImGui.TextUnformatted($"Workbook exported to: {_lastWorkbookExportPath}");
        }
```

Add an "Open Workbook" button right after the existing text, only enabled once a path is known:

```csharp
        ImGui.SameLine();
        if (ImGui.Button("Export Workbook"))
            ExportWorkbook(WorkbookStrategyOptions[_workbookStrategyIndex].Strategy);

        if (_lastWorkbookExportPath is not null)
        {
            ImGui.SameLine();
            ImGui.TextUnformatted($"Workbook exported to: {_lastWorkbookExportPath}");

            ImGui.SameLine();
            if (ImGui.Button("Open Workbook"))
                OpenWorkbookFile(_lastWorkbookExportPath);
        }
```

And the private `ExportWorkbook` wrapper, currently:

```csharp
    private void ExportWorkbook(PenumbraOrganizer.Core.Models.OrganizationStrategy strategy)
    {
        try
        {
            _lastWorkbookExportPath = _plugin.ExportWorkbook(strategy);
            _lastError = null;
        }
        catch (Exception ex)
        {
            _lastError = $"Workbook export failed: {ex.Message}";
        }
    }
```

Replace with (opens the save picker first, exports only once a destination is chosen):

```csharp
    private void ExportWorkbook(PenumbraOrganizer.Core.Models.OrganizationStrategy strategy)
    {
        _fileDialogManager.SaveFileDialog(
            "Save Workbook",
            ".xlsx",
            Plugin.DefaultWorkbookFileName,
            ".xlsx",
            (success, path) =>
            {
                if (!success)
                    return;
                try
                {
                    _lastWorkbookExportPath = _plugin.ExportWorkbook(strategy, path);
                    _lastError = null;
                }
                catch (Exception ex)
                {
                    _lastError = $"Workbook export failed: {ex.Message}";
                }
            });
    }

    private void OpenWorkbookFile(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _lastError = $"Could not open workbook: {ex.Message}";
        }
    }
```

`Process.Start(..., UseShellExecute = true)` launches the file via the OS's normal file association (Excel, or whatever's registered for `.xlsx`) — this is a fire-and-forget external-process launch, not a blocking Explorer dialog, so it does not reintroduce the game-pausing problem Task 3 fixed. It's a distinct mechanism from `OpenFileDialog`/`SaveFileDialog`; do not confuse the two.

- [ ] **Step 3: Build and verify**

Run: `dotnet build PenumbraOrganizer.Plugin.sln` — expect 0 warnings/errors.
Run: `dotnet test PenumbraOrganizer.Plugin.Tests` — expect all green (this task touches no `Organizer/` pure code, matches the existing untested-IPC-surface convention).

- [ ] **Step 4: Commit**

```bash
git add PenumbraOrganizer.Plugin/Plugin.cs PenumbraOrganizer.Plugin/Windows/MainWindow.cs
git commit -m "feat: add Open Workbook button and a save-destination picker for Export Workbook"
```

---

## Task 5: Split "By Mod Type" into flat and detailed variants

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs:66-82` (current `SortByModType`)
- Modify: `PenumbraOrganizer.Plugin/Windows/MainWindow.cs:144-153` (Sort tab buttons)
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateTests.cs`

**Interfaces:**
- Produces: `OrganizerState.SortByModType()` — NEW behavior: Gear mods always land in a flat `Gear/` folder (no `Head`/`Top`/`Feet`/etc. subfolders); every other category keeps its existing subfolder behavior unchanged (e.g. `Animation and VFX/Emotes`, `NPC/NPCs`). `OrganizerState.SortByModTypeDetailed()` — the OLD `SortByModType()` behavior, renamed verbatim, unchanged: Gear mods use their resolved `SubCategory` subfolder (`Gear/Feet`, etc.) when available.
- This works by reusing the existing `ModTypeFolders.GetFolder(ModCategory, string?)` unmodified — passing `subCategory: null` for a Gear row already makes it return the bare `"Gear"` folder (see its own switch arm `(_, null) => category.ToString()`), so no changes are needed in `ModTypeFolders` itself, only in what gets passed to it from each of the two `OrganizerState` methods.

- [ ] **Step 1: Write the failing tests**

Add to `PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateTests.cs`. Check the file first for its existing `MakeCategorizedRow` helper signature (`MakeCategorizedRow(string id, string name, ModCategory? category, string? subCategory = null, bool isProtected = false)`) and reuse it — do not redefine it.

```csharp
[Fact]
public void SortByModType_GearWithSubCategory_GoesToFlatGearFolderNotSubfolder()
{
    var state = new OrganizerState();
    state.LoadScan([MakeCategorizedRow("a", "Boots", ModCategory.Gear, "Feet")], new HashSet<string>());

    state.SortByModType();

    Assert.Equal("Gear/Boots", state.Mods.Single().ProposedPath);
}

[Fact]
public void SortByModType_NonGearWithSubCategory_KeepsSubfolder()
{
    // Only Gear is flattened - every other category's subfolder behavior is unchanged.
    var state = new OrganizerState();
    state.LoadScan([MakeCategorizedRow("a", "Wave", ModCategory.Animation, "Emotes")], new HashSet<string>());

    state.SortByModType();

    Assert.Equal("Animation and VFX/Emotes/Wave", state.Mods.Single().ProposedPath);
}

[Fact]
public void SortByModTypeDetailed_GearWithSubCategory_UsesSubfolder()
{
    var state = new OrganizerState();
    state.LoadScan([MakeCategorizedRow("a", "Boots", ModCategory.Gear, "Feet")], new HashSet<string>());

    state.SortByModTypeDetailed();

    Assert.Equal("Gear/Feet/Boots", state.Mods.Single().ProposedPath);
}

[Fact]
public void SortByModTypeDetailed_GearWithoutSubCategory_GoesToBareGearFolder()
{
    var state = new OrganizerState();
    state.LoadScan([MakeCategorizedRow("a", "Cloak", ModCategory.Gear, null)], new HashSet<string>());

    state.SortByModTypeDetailed();

    Assert.Equal("Gear/Cloak", state.Mods.Single().ProposedPath);
}
```

(Uses `PenumbraOrganizer.Core.Classification.ModCategory` — already imported at the top of this test file via `using PenumbraOrganizer.Core.Classification;`.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~SortByModType"`
Expected: FAIL — `SortByModTypeDetailed` doesn't exist yet (compile error), and the flat-Gear assertions fail against the current subfolder-including behavior.

- [ ] **Step 3: Implement the split**

In `PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs`, the current method reads:

```csharp
    public int SortByModType()
    {
        var count = 0;
        var touched = new List<OrganizerModRow>();
        foreach (var row in _mods.Values.Where(m => !m.Protected))
        {
            var typeFolder = row.Category is null
                ? null
                : KnownFolder(ModTypeFolders.GetFolder(row.Category.Value, row.SubCategory));
            row.ProposedPath = BuildPath(typeFolder, null, row.Name);
            touched.Add(row);
            count++;
        }

        FinishProposals(touched);
        return count;
    }
```

Replace with two methods — `SortByModTypeDetailed` keeps the body verbatim (renamed only), and the new `SortByModType` flattens Gear's subcategory before calling `GetFolder`:

```csharp
    public int SortByModType()
    {
        var count = 0;
        var touched = new List<OrganizerModRow>();
        foreach (var row in _mods.Values.Where(m => !m.Protected))
        {
            // Gear only: always the flat folder, ignoring any resolved slot subcategory.
            // Every other category keeps its normal subfolder behavior via GetFolder unchanged.
            var subCategory = row.Category == ModCategory.Gear ? null : row.SubCategory;
            var typeFolder = row.Category is null
                ? null
                : KnownFolder(ModTypeFolders.GetFolder(row.Category.Value, subCategory));
            row.ProposedPath = BuildPath(typeFolder, null, row.Name);
            touched.Add(row);
            count++;
        }

        FinishProposals(touched);
        return count;
    }

    public int SortByModTypeDetailed()
    {
        var count = 0;
        var touched = new List<OrganizerModRow>();
        foreach (var row in _mods.Values.Where(m => !m.Protected))
        {
            var typeFolder = row.Category is null
                ? null
                : KnownFolder(ModTypeFolders.GetFolder(row.Category.Value, row.SubCategory));
            row.ProposedPath = BuildPath(typeFolder, null, row.Name);
            touched.Add(row);
            count++;
        }

        FinishProposals(touched);
        return count;
    }
```

Add the missing `using PenumbraOrganizer.Core.Classification;` at the top of `OrganizerState.cs` if `ModCategory` isn't already resolvable there — check first:

Run: `grep -n "^using" PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs`

If `PenumbraOrganizer.Core.Classification` isn't listed, add it as the first using line.

- [ ] **Step 4: Update `SortByTypeThenCreator` and `SortByCreatorThenType` to call the detailed variant**

These two combined-sort methods currently build their `typeFolder` the same way the old (now-detailed) `SortByModType` did — they should keep the existing (detailed, subfolder-including) behavior unchanged, just routed through the explicit name so it's clear which one they mean. Search `OrganizerState.cs` for both methods; each has this identical line:

```csharp
            var typeFolder = row.Category is null
                ? null
                : KnownFolder(ModTypeFolders.GetFolder(row.Category.Value, row.SubCategory));
```

Leave both of these two occurrences exactly as they are — they already match `SortByModTypeDetailed`'s behavior, so no change is needed here. (This step is a verification step, not a code change: confirm via the grep above that exactly two more occurrences of this pattern exist beyond the two methods just edited, and that neither needs the `ModCategory.Gear` flattening applied — the user's note only asked for a standalone flat "By Mod Type" button, not flat behavior inside the combined sorts.)

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "FullyQualifiedName~OrganizerStateTests"`
Expected: PASS, all tests including the 4 new ones.

- [ ] **Step 6: Rename the button and add the new one in `MainWindow.cs`**

In `DrawSortTab`, currently:

```csharp
        if (ImGui.Button("By Creator"))
            _plugin.OrganizerState.SortByCreator(_creatorCanonicalizer.Canonicalize);

        ImGui.SameLine();
        if (ImGui.Button("By Mod Type"))
            _plugin.OrganizerState.SortByModType();

        ImGui.SameLine();
        if (ImGui.Button("By Type Then Creator"))
            _plugin.OrganizerState.SortByTypeThenCreator(_creatorCanonicalizer.Canonicalize);
```

Replace with:

```csharp
        if (ImGui.Button("By Creator"))
            _plugin.OrganizerState.SortByCreator(_creatorCanonicalizer.Canonicalize);

        ImGui.SameLine();
        if (ImGui.Button("By Mod Type"))
            _plugin.OrganizerState.SortByModType();

        ImGui.SameLine();
        if (ImGui.Button("By Mod Type Detailed"))
            _plugin.OrganizerState.SortByModTypeDetailed();

        ImGui.SameLine();
        if (ImGui.Button("By Type Then Creator"))
            _plugin.OrganizerState.SortByTypeThenCreator(_creatorCanonicalizer.Canonicalize);
```

(The "By Mod Type" button's label is unchanged — only its underlying behavior changed, per the user's note that it should keep the flat-Gear approach as the plain-named button. "By Mod Type Detailed" is the new button carrying the old subfolder behavior forward.)

- [ ] **Step 7: Build and full test run**

Run: `dotnet build PenumbraOrganizer.Plugin.sln` — expect 0 warnings/errors.
Run: `dotnet test PenumbraOrganizer.Plugin.Tests` — expect all green.

- [ ] **Step 8: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs PenumbraOrganizer.Plugin/Windows/MainWindow.cs PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateTests.cs
git commit -m "feat: split By Mod Type into flat (Gear as one folder) and Detailed variants"
```

---

## Task 6: Plugin icon

**Files:**
- Create: `PenumbraOrganizer.Plugin/images/icon.png`
- Modify: `PenumbraOrganizer.Plugin/PenumbraOrganizer.Plugin.csproj` (embed the icon as content, per Dalamud convention)

**Interfaces:** none (asset-only task).

- [ ] **Step 1: Locate and inspect the source asset**

The standalone app's icon assets live in `C:\Repo\PenumbraOrganizer\PenumbraOrganizer.App\Assets\`: `app.ico` (multi-resolution Windows icon) and `app-logo.png` (701×865 PNG — NOT square). Dalamud plugin icons must be square PNGs (512×512 is the safe, universally-accepted size for `images/icon.png` alongside a plugin's own manifest, matching how `Glamourer`'s `images/icon.png` — referenced by its `repo.json`'s `IconUrl` — is structured).

Since `app-logo.png` isn't square, it needs a square crop before use — do not stretch it (that would distort the logo).

Run this PowerShell to inspect the source image and produce a centered square crop at 512×512, saved to the new destination (creates the `images` directory if needed):

```powershell
Add-Type -AssemblyName System.Drawing
$src = [System.Drawing.Image]::FromFile("C:\Repo\PenumbraOrganizer\PenumbraOrganizer.App\Assets\app-logo.png")
$side = [Math]::Min($src.Width, $src.Height)
$offsetX = [Math]::Max(0, ($src.Width - $side) / 2)
$offsetY = [Math]::Max(0, ($src.Height - $side) / 2)
$cropRect = New-Object System.Drawing.Rectangle($offsetX, $offsetY, $side, $side)
$cropped = New-Object System.Drawing.Bitmap(512, 512)
$g = [System.Drawing.Graphics]::FromImage($cropped)
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.DrawImage($src, (New-Object System.Drawing.Rectangle(0, 0, 512, 512)), $cropRect, [System.Drawing.GraphicsUnit]::Pixel)
New-Item -ItemType Directory -Force -Path "C:\Repo\PenumbraOrganizer.Plugin\PenumbraOrganizer.Plugin\images" | Out-Null
$cropped.Save("C:\Repo\PenumbraOrganizer.Plugin\PenumbraOrganizer.Plugin\images\icon.png", [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $cropped.Dispose(); $src.Dispose()
```

- [ ] **Step 2: Visually confirm the crop looks right**

Open `C:\Repo\PenumbraOrganizer.Plugin\PenumbraOrganizer.Plugin\images\icon.png` (e.g. via the Read tool, which can render images) and check the logo isn't awkwardly cropped (a centered crop can clip a logo that isn't centered within its own canvas). If it looks wrong, stop and ask the user for a pre-cropped square asset instead of guessing further — this is a visual judgment call, not something to iterate on blindly.

- [ ] **Step 3: Embed it as content in the csproj**

In `PenumbraOrganizer.Plugin/PenumbraOrganizer.Plugin.csproj`, add a new `ItemGroup` (after the existing `EmbeddedResource` group for the NPC seed file):

```xml
  <ItemGroup>
    <None Include="images\icon.png" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```

This copies `images/icon.png` next to the built DLL, matching where Dalamud's dev-plugin loader and `repo.json`-based distribution both expect to find a plugin's icon relative to its manifest.

- [ ] **Step 4: Build and verify**

Run: `dotnet build PenumbraOrganizer.Plugin.sln` — expect 0 warnings/errors.
Run: `ls PenumbraOrganizer.Plugin/bin/Debug/images/icon.png` (or the PowerShell equivalent `Test-Path`) — confirm the file was copied to the output directory.

- [ ] **Step 5: Commit**

```bash
git add PenumbraOrganizer.Plugin/images/icon.png PenumbraOrganizer.Plugin/PenumbraOrganizer.Plugin.csproj
git commit -m "chore: add plugin icon (cropped from the standalone app's logo)"
```

---

## Task 7: `repo.json` for self-hosted Dalamud distribution

**Files:**
- Create: `repo.json` (repo root, `C:\Repo\PenumbraOrganizer.Plugin\repo.json`)

**Interfaces:** none (static manifest, consumed only by Dalamud's own plugin-repo loader once the GitHub repo is public and a release exists).

**Precondition — do not implement until confirmed:** this needs Task 2 (versioning) done, an actual GitHub Release with a built `PenumbraOrganizer.Plugin.zip` asset, and the `monstersghost/PenumbraOrganizerPlugin` repo to be public. Per project memory, public distribution was explicitly called out as a scope decision needing fresh, explicit confirmation before shipping — a self-hosted `repo.json` is a smaller step than submitting to Dalamud's official plugin repo, but confirm with the user that "for when we go live" means now, not later, before creating the release/zip machinery this depends on. If not confirmed, skip this task for this pass.

- [ ] **Step 1: Confirm readiness**

Ask (or otherwise confirm with the user) that: (a) the repo should go public now or very soon, and (b) they want a self-hosted `repo.json` (distinct from, and much smaller in scope than, submitting to Dalamud's official curated plugin repository — see `[[dalamud-plugin-decision]]` project memory for why that larger step was explicitly ruled out). If not confirmed, stop here and leave this task pending.

- [ ] **Step 2: Create the manifest**

Modeled directly on `https://raw.githubusercontent.com/Ottermandias/Glamourer/main/repo.json` (fetched and inspected as the user's own reference example), create `C:\Repo\PenumbraOrganizer.Plugin\repo.json`:

```json
[
  {
    "Author": "Penumbra Organizer Contributors",
    "Name": "Penumbra Organizer",
    "Punchline": "Organize your Penumbra mod library by creator, type, or both.",
    "Description": "Scans your installed Penumbra mods and proposes an organized folder layout (by creator, mod type, or both), with review, protect, and workbook import/export support before anything is applied.",
    "Tags": [
      "penumbra",
      "organizer",
      "sorting"
    ],
    "InternalName": "PenumbraOrganizer.Plugin",
    "MinimumDalamudVersion": "15.0.0.0",
    "AssemblyVersion": "0.4.0.0",
    "RepoUrl": "https://github.com/monstersghost/PenumbraOrganizerPlugin",
    "ApplicableVersion": "any",
    "DalamudApiLevel": 15,
    "IsHide": "False",
    "DownloadLinkInstall": "https://github.com/monstersghost/PenumbraOrganizerPlugin/releases/download/0.4.0.0/PenumbraOrganizer.Plugin.zip",
    "DownloadLinkUpdate": "https://github.com/monstersghost/PenumbraOrganizerPlugin/releases/download/0.4.0.0/PenumbraOrganizer.Plugin.zip",
    "IconUrl": "https://raw.githubusercontent.com/monstersghost/PenumbraOrganizerPlugin/main/images/icon.png"
  }
]
```

`InternalName` must exactly match the plugin's actual internal/assembly name (`PenumbraOrganizer.Plugin`, confirmed against `PenumbraOrganizer.Plugin.csproj`'s implicit assembly name and `PenumbraOrganizer.Plugin.json`'s existing manifest) — Dalamud matches installed plugins to repo entries by this field. `IconUrl` points at the `images/icon.png` Task 6 adds, once pushed to the (by then public) `main` branch — this URL will 404 until both the repo is public and that path exists on `main`, which is expected and fine until go-live.

- [ ] **Step 3: Verify the JSON is well-formed**

Run: `pwsh -NoProfile -Command "Get-Content repo.json -Raw | ConvertFrom-Json | Out-Null; 'valid'"`
Expected: prints `valid` with no errors.

- [ ] **Step 4: Commit**

```bash
git add repo.json
git commit -m "chore: add repo.json for self-hosted Dalamud plugin distribution"
```

Do not push or make the repository public as part of this task — that's a separate, explicit action for the user to take when actually ready to go live.

---

## Task 8: Consolidate the five sort strategies into a shared `Sort` helper

**Added after Task 5's review** — the task reviewer correctly flagged that Task 5's new
`SortByModType`/`SortByModTypeDetailed` split duplicates the same loop/count/touched/
`FinishProposals` shape already repeated across all five sort methods. Rather than accept more
duplication, this task extracts the shared shape once, for all five methods together.

**Files:**
- Modify: `PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs` (all five `SortBy*` methods, plus a
  new private `Sort` helper and a new private `TypeFolder` helper)
- No test changes required — this is a pure refactor with identical output for every existing
  input; the existing `OrganizerStateTests.cs` suite (including Task 5's 4 new tests) is the
  regression net and must pass unchanged, with zero test edits.

**Interfaces:**
- Produces: `private int Sort(Func<OrganizerModRow, (string? Primary, string? Secondary)> folderSelector)`
  — the shared loop body every `SortBy*` method now delegates to.
- Produces: `private static string? TypeFolder(ModCategory? category, string? subCategory)` — the
  `category is null ? null : KnownFolder(ModTypeFolders.GetFolder(category.Value, subCategory))`
  pattern that was repeated in four of the five methods, extracted once.
- `KnownFolder`, `KnownSegment`, `BuildPath`, `FinishProposals` are unchanged (still used, now only
  from inside `Sort`/`TypeFolder`).

- [ ] **Step 1: Confirm the current file state matches this task's assumptions**

Read `PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs` in full. It must currently contain,
in this order: `SortByCreator`, `SortByModType`, `SortByModTypeDetailed`, `SortByTypeThenCreator`,
`SortByCreatorThenType`, then the private helpers `KnownFolder`, `KnownSegment`, `BuildPath`,
`FinishProposals`. If the file doesn't match this shape (e.g. Task 5 wasn't actually merged first),
stop and report BLOCKED — this task must run after Task 5, not before or in parallel with it.

- [ ] **Step 2: Replace all five sort methods and add the two new private helpers**

Replace the entire block from `public int SortByCreator(...)` through the end of
`SortByCreatorThenType(...)` (i.e. everything between `AssignManual` and the `KnownFolder` helper)
with:

```csharp
    public int SortByCreator(Func<string, string> canonicalizeCreator) =>
        Sort(row => (KnownSegment(canonicalizeCreator(row.Author)), null));

    public int SortByModType() =>
        Sort(row =>
        {
            // Gear only: always the flat folder, ignoring any resolved slot subcategory.
            // Every other category keeps its normal subfolder behavior via GetFolder unchanged.
            var subCategory = row.Category == ModCategory.Gear ? null : row.SubCategory;
            return (TypeFolder(row.Category, subCategory), null);
        });

    public int SortByModTypeDetailed() =>
        Sort(row => (TypeFolder(row.Category, row.SubCategory), null));

    public int SortByTypeThenCreator(Func<string, string> canonicalizeCreator) =>
        Sort(row => (TypeFolder(row.Category, row.SubCategory), KnownSegment(canonicalizeCreator(row.Author))));

    public int SortByCreatorThenType(Func<string, string> canonicalizeCreator) =>
        Sort(row => (KnownSegment(canonicalizeCreator(row.Author)), TypeFolder(row.Category, row.SubCategory)));

    // Shared shape of every sort strategy: compute this row's (primary, secondary) folder
    // segments, build its proposed path, then run the shared pin-and-disambiguate tail once
    // over every touched row. Each public SortBy* method supplies only what varies: which
    // folder segments go where.
    private int Sort(Func<OrganizerModRow, (string? Primary, string? Secondary)> folderSelector)
    {
        var count = 0;
        var touched = new List<OrganizerModRow>();
        foreach (var row in _mods.Values.Where(m => !m.Protected))
        {
            var (primary, secondary) = folderSelector(row);
            row.ProposedPath = BuildPath(primary, secondary, row.Name);
            touched.Add(row);
            count++;
        }

        FinishProposals(touched);
        return count;
    }

    private static string? TypeFolder(ModCategory? category, string? subCategory) =>
        category is null ? null : KnownFolder(ModTypeFolders.GetFolder(category.Value, subCategory));
```

Everything below this block (`KnownFolder`, `KnownSegment`, `BuildPath`, `FinishProposals`,
`Validate`, and the `ReviewResult` record) stays exactly as it already is — do not touch it.

- [ ] **Step 3: Build and run the full existing suite — must be byte-for-byte behavior preserving**

Run: `dotnet build PenumbraOrganizer.Plugin.sln` — expect 0 warnings/errors.
Run: `dotnet test PenumbraOrganizer.Plugin.Tests` — expect the exact same pass count as before this
task (check the count from the prior task's report), 0 failures. This refactor must not change a
single test's outcome — if anything fails, that means the extraction changed behavior somewhere;
do not "fix" a test to match new behavior, find and fix the refactor instead, since the whole point
of this task is that output is identical to before.

- [ ] **Step 4: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs
git commit -m "refactor: consolidate the five sort strategies into a shared Sort helper"
```

---

## Self-Review Notes

- **Spec coverage:** all 7 notes map 1:1 to Tasks 1–7 above.
- **Task 5 clarification baked in:** confirmed with the user directly — "By Mod Type" becomes flat-Gear-only (other categories' subfolders untouched), the current behavior is preserved verbatim under a new "By Mod Type Detailed" button.
- **Shared infrastructure ordering:** Task 4 explicitly depends on Task 3's `_fileDialogManager` field; do not execute Task 4 before Task 3.
- **Task 7 is gated** behind an explicit go-live confirmation per this plan's own Step 1 — do not treat its presence in this plan as pre-authorization to make the repo public or cut a release.
