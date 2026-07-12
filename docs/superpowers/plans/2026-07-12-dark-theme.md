# Plugin Dark Theme Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give `PenumbraOrganizer.Plugin`'s ImGui window the app's own visual identity — a dark
adaptation of `PenumbraOrganizer.App/Themes/Theme.xaml`'s indigo-accent palette — via native ImGui
style pushes, applied once at the top of `MainWindow.Draw()`.

**Architecture:** A new static class, `PluginTheme`, holds the palette as pre-converted ImGui color
`uint`s and exposes two `IDisposable`-returning scopes: `Push()` (the whole-window theme, wraps
`MainWindow.Draw()`) and `PrimaryButton()` (a one-button override for the Scan tab's "Refresh mod
list", the plugin's only accent-at-rest button). Both use plain `ImGui.PushStyleColor`/
`PushStyleVar`/`PopStyleColor`/`PopStyleVar` — no custom draw-list widgets, no new dependency.

**Tech Stack:** C#, Dalamud.NET.Sdk 15.0.0 (net10.0-windows7.0), Dalamud.Bindings.ImGui.

## Global Constraints

- Reskin only — no new custom-drawn UI framework or third-party rendering/templating dependency (per
  spec's rejection of the `Una.Drawing`-style approach).
- One integration point: `PluginTheme.Push()` wraps `MainWindow.Draw()`'s entire body; no per-tab or
  per-widget style pushes beyond that and the one `PrimaryButton()` exception.
- Colors are the approved token table (source: `Theme.xaml`'s `#6366F1` accent, dark-adapted, Variant
  A "accent-forward").
- Existing Dalamud semantic colors (`ImGuiColors.DalamudYellow`/`HealerGreen`/`DalamudRed`) are left
  untouched — they are cross-plugin conventions, not part of this palette.
- Only the Scan tab's "Refresh mod list" button renders accent-filled at rest; every other button
  (Toggle Heliosphere protection, By Creator, Assign, the disabled Apply button) stays neutral at rest
  and only picks up the accent on hover/press via the global `Push()` mapping.
- No layout or behavior changes — color/style pass only.

---

## Task 1: `PluginTheme` — palette and push/pop scopes

Not unit-testable (ImGui requires a live rendering context, same category as `PathTreeView`/
`MainWindow`). Build-verify only; manual in-game verification happens in Task 3.

**Files:**
- Create: `C:\Repo\PenumbraOrganizer.Plugin\PenumbraOrganizer.Plugin\Windows\PluginTheme.cs`

**Interfaces:**
- Produces: `PluginTheme.Push() -> IDisposable` (whole-window theme scope), `PluginTheme.PrimaryButton()
  -> IDisposable` (single-button accent-at-rest override).

- [ ] **Step 1: Implement**

ImGui's `uint` color overload packs channels as `0xAABBGGRR` (alpha, blue, green, red — the reverse
byte order of a standard `#RRGGBB` web hex value), not `0xAARRGGBB`. Each constant below is the
approved token's hex value repacked into that format; the comment on each line is the original
`#RRGGBB` source value for traceability back to the design spec.

Create `C:\Repo\PenumbraOrganizer.Plugin\PenumbraOrganizer.Plugin\Windows\PluginTheme.cs`:

```csharp
using Dalamud.Bindings.ImGui;

namespace PenumbraOrganizer.Plugin.Windows;

public static class PluginTheme
{
    private const uint Accent = 0xFFF16663;      // #6366F1
    private const uint AccentHover = 0xFFF57D7A; // #7A7DF5
    private const uint WindowBg = 0xFF241D1B;    // #1B1D24
    private const uint Surface = 0xFF2F2623;     // #23262F
    private const uint SurfaceAlt = 0xFF3A2E2A;  // #2A2E3A
    private const uint Border = 0xFF4A3C38;      // #383C4A
    private const uint Text = 0xFFF3E9E7;        // #E7E9F3
    private const uint TextDim = 0xFFB8A39C;     // #9CA3B8

    private const int ColorCount = 18;
    private const int StyleVarCount = 6;

    public static IDisposable Push()
    {
        ImGui.PushStyleColor(ImGuiCol.WindowBg, WindowBg);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Surface);
        ImGui.PushStyleColor(ImGuiCol.Button, Surface);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, SurfaceAlt);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Accent);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Surface);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, SurfaceAlt);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Accent);
        ImGui.PushStyleColor(ImGuiCol.CheckMark, Accent);
        ImGui.PushStyleColor(ImGuiCol.Tab, Surface);
        ImGui.PushStyleColor(ImGuiCol.TabHovered, SurfaceAlt);
        ImGui.PushStyleColor(ImGuiCol.TabActive, Accent);
        ImGui.PushStyleColor(ImGuiCol.TableHeaderBg, Surface);
        ImGui.PushStyleColor(ImGuiCol.TableRowBg, Surface);
        ImGui.PushStyleColor(ImGuiCol.TableRowBgAlt, SurfaceAlt);
        ImGui.PushStyleColor(ImGuiCol.Border, Border);
        ImGui.PushStyleColor(ImGuiCol.Text, Text);
        ImGui.PushStyleColor(ImGuiCol.TextDisabled, TextDim);

        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.TabRounding, 4.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 4.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarRounding, 4.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.GrabRounding, 4.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1.0f);

        return new PopScope(ColorCount, StyleVarCount);
    }

    public static IDisposable PrimaryButton()
    {
        ImGui.PushStyleColor(ImGuiCol.Button, Accent);
        return new PopScope(colors: 1);
    }

    private sealed class PopScope(int colors, int vars = 0) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            if (colors > 0)
                ImGui.PopStyleColor(colors);
            if (vars > 0)
                ImGui.PopStyleVar(vars);

            _disposed = true;
        }
    }
}
```

`AccentHover` is defined for completeness with the design spec's token table but has no push site in
this task — ImGui derives button/frame/tab hover color from the `*Hovered` `ImGuiCol` entries above,
which already use `SurfaceAlt` per the approved mapping (hover state = neutral highlight, not an
accent tint, matching the mockup). Leave the constant in place; a future task may use it directly.

- [ ] **Step 2: Build to verify**

Run: `cd C:\Repo\PenumbraOrganizer.Plugin && dotnet build PenumbraOrganizer.Plugin.sln`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 3: Commit**

```bash
cd C:\Repo\PenumbraOrganizer.Plugin
git add PenumbraOrganizer.Plugin/Windows/PluginTheme.cs
git commit -m "feat: add PluginTheme with the app's dark-adapted accent palette"
```

---

## Task 2: Wire `PluginTheme` into `MainWindow`

Not unit-testable. Build-verify only; manual in-game verification happens in Task 3.

**Files:**
- Modify: `C:\Repo\PenumbraOrganizer.Plugin\PenumbraOrganizer.Plugin\Windows\MainWindow.cs`

**Interfaces:**
- Consumes: `PluginTheme.Push()`, `PluginTheme.PrimaryButton()` (Task 1).

- [ ] **Step 1: Wrap `Draw()` in the theme scope**

In `C:\Repo\PenumbraOrganizer.Plugin\PenumbraOrganizer.Plugin\Windows\MainWindow.cs`, in
`public override void Draw()`, add the theme push as the very first line so every tab inherits it:

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

- [ ] **Step 2: Give the Scan tab's "Refresh mod list" button the primary accent treatment**

In `DrawScanTab()`, wrap only the `ImGui.Button("Refresh mod list")` call in the
`PluginTheme.PrimaryButton()` scope:

```csharp
    private void DrawScanTab()
    {
        using var tab = ImRaii.TabItem("Scan");
        if (!tab)
            return;

        using (PluginTheme.PrimaryButton())
        {
            if (ImGui.Button("Refresh mod list"))
                RunScan();
        }

        ImGui.SameLine();
        ImGui.Text($"{_plugin.OrganizerState.Mods.Count} mods loaded");
        ImGui.Spacing();

        PathTreeView.Draw(_plugin.OrganizerState.Mods, showProposedColumn: false);

        ImGui.Spacing();
        ImGui.Text("Live events (ModAdded / ModDeleted / ModMoved):");
        using (var child = ImRaii.Child("EventLog", new Vector2(0, 150), border: true))
        {
            if (child)
                foreach (var line in _eventLog)
                    ImGui.TextUnformatted(line);
        }
    }
```

Every other button in the file (`Toggle Heliosphere protection`, `By Creator`, `Assign`, the disabled
`Apply` button) is intentionally left untouched — they inherit the neutral `Push()` mapping from Step
1 and stay neutral at rest, per the approved mockup.

- [ ] **Step 3: Build to verify**

Run: `cd C:\Repo\PenumbraOrganizer.Plugin && dotnet build PenumbraOrganizer.Plugin.sln`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 4: Run the existing test suite to confirm no regression**

Run: `cd C:\Repo\PenumbraOrganizer.Plugin && dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: `Passed! - Failed: 0, Passed: 23, Skipped: 0` (this task touches no logic the tests cover —
this is a pure regression check, not new coverage)

- [ ] **Step 5: Commit**

```bash
cd C:\Repo\PenumbraOrganizer.Plugin
git add PenumbraOrganizer.Plugin/Windows/MainWindow.cs
git commit -m "feat: apply PluginTheme to MainWindow, accent the primary Refresh button"
```

---

## Task 3: Manual in-game verification

Not automatable — same manual dev-plugin verification convention as the rest of this project (see
`README.md` and the Phase 1a/1b plan's Task 14).

- [ ] **Step 1: Build and load**

Run: `cd C:\Repo\PenumbraOrganizer.Plugin && dotnet build PenumbraOrganizer.Plugin.sln`

Reload the plugin via `/xlplugins` → Dev Tools, or restart the game if needed.

- [ ] **Step 2: Compare each tab against the approved mockup**

Open with `/porganizer`. For each of the four tabs (Scan, Protect, Sort, Review Changes), confirm:
- Window background, panel/table backgrounds, and borders read as dark slate (not ImGui's default
  gray), matching the approved mockup at
  `docs/superpowers/specs/2026-07-12-dark-theme-design.md`.
- The Scan tab's "Refresh mod list" button is filled with the indigo accent at rest; every other
  button (Toggle Heliosphere protection, By Creator, Assign, the disabled Apply button) is neutral at
  rest and only tints on hover/press.
- The active tab (Scan/Protect/Sort/Review Changes, whichever is selected) fills solid with the
  accent color.
- Checkboxes (Protect tab) and radio buttons (Sort tab) use the accent color when checked/selected.
- The mod table (`PathTreeView`, visible on Scan and Review Changes) shows alternating row shading
  (zebra stripe) between the two surface tones.
- Protected mods still render in Dalamud's yellow (`ImGuiColors.DalamudYellow`) — unaffected by this
  change, per the design spec's "left untouched" list.
- Text stays fully legible against every background (primary text bright, secondary/dim text visibly
  but not illegibly dimmer).

- [ ] **Step 3: Record results**

No commit for this task — it's verification, not a code change. If any check in Step 2 fails or looks
visibly wrong, stop and treat it as a bug against Task 1 or Task 2 rather than considering the theme
complete.
