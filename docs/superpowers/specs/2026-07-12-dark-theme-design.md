# Plugin dark theme design

## Goal

Give `PenumbraOrganizer.Plugin`'s ImGui window a deliberate visual identity instead of ImGui's
unstyled gray defaults, so it feels like the same product as the sibling `PenumbraOrganizer` desktop
app rather than a foreign, plain overlay. Scope: a dark-mode adaptation of the app's own design
system, applied uniformly across all four tabs (Scan, Protect, Sort, Review Changes). Not a new
custom-drawn UI framework — reskinning ImGui's own styling surface (`ImGuiCol`/`ImGuiStyleVar`), not
replacing it.

## Motivation

The desktop app's `PenumbraOrganizer.App/Themes/Theme.xaml` defines a light theme: indigo accent
(`#6366F1`), white surfaces, 6px rounded controls, underline-style active-tab indicators, zebra-striped
rows. The plugin currently uses ImGui's unstyled defaults, which reads as inconsistent with the app a
user coming from. Two alternative directions were considered and rejected:

- **Umbra XIV's fully custom-drawn UI** (`Una.Drawing`, a separate CSS-like templating/rendering
  library) — rejected as a disproportionate dependency for a Phase 1 utility plugin; it's a second UI
  framework, not a styling change.
- **The app's pre-modernization ("old WinForms-like") unstyled look** — considered, then rejected in
  favor of matching the app's *current* released design system instead of an older, superseded one.

The chosen direction: reproduce the app's *current* palette and interaction patterns as closely as
native ImGui styling allows, adapted to a dark background since the plugin is an in-game overlay
(unlike the app's light desktop window).

## Architecture

A new static class, `PenumbraOrganizer.Plugin.Windows.PluginTheme`, holds the palette as
`ImGuiCol`/`ImGuiStyleVar` push values and exposes a single `Push()` method returning an `IDisposable`
scope, matching the `ImRaii`-style pattern already used throughout `MainWindow`/`PathTreeView`.

Applied once, wrapping the entire body of `MainWindow.Draw()`:

```csharp
public override void Draw()
{
    using var theme = PluginTheme.Push();
    // ...existing tab bar + four tabs, unchanged
}
```

This is the only integration point. All four tabs, `PathTreeView`'s table, and the event-log child
inherit the pushed style automatically — ImGui style pushes apply to everything drawn while active, no
per-widget changes needed beyond `PathTreeView`'s table opting into `ImGuiTableFlags.RowBg` (already
present) picking up the new row-background colors.

## Color tokens

Sourced from `Theme.xaml`'s accent hue, adapted to a dark ground. Approved via an interactive mockup
(`Variant A — accent-forward`, chosen over a more accent-minimal alternative).

| Token | Hex | Role |
|---|---|---|
| `Accent` | `#6366F1` | primary action button, active tab fill, checkmarks |
| `AccentHover` | `#7A7DF5` | hover state on accent elements |
| `WindowBg` | `#1B1D24` | main window background |
| `Surface` | `#23262F` | child panels, frame backgrounds, table header row |
| `SurfaceAlt` | `#2A2E3A` | table zebra stripe (odd rows), hovered frame/button |
| `Border` | `#383C4A` | all frame/table/window borders |
| `Text` | `#E7E9F3` | primary text |
| `TextDim` | `#9CA3B8` | secondary text (mod counts, event log, column headers) |

Existing Dalamud semantic colors are **left untouched** — they are cross-plugin conventions, not part
of the app's accent system: `ImGuiColors.DalamudYellow` (protected mods), `HealerGreen`
(changed/success paths in Review), `DalamudRed` (violations/collisions/errors).

## Component mapping

All pushed once inside `PluginTheme.Push()`:

- `ImGuiCol.WindowBg` / `ChildBg` → `WindowBg` / `Surface`
- `ImGuiCol.Button` / `ButtonHovered` / `ButtonActive` → `Surface` / `SurfaceAlt` / `Accent` — buttons
  stay neutral at rest; only hover/press picks up `Accent` globally. The single exception is the Scan
  tab's "Refresh mod list" button, the plugin's one primary action, which renders filled with `Accent`
  at rest via a local `ImGui.PushStyleColor(ImGuiCol.Button, Accent)` around just that call, popped
  immediately after. Every other button (Toggle Heliosphere protection, By Creator, Assign, the
  disabled Apply button) stays neutral at rest, per the approved mockup.
- `ImGuiCol.FrameBg` / `FrameBgHovered` / `FrameBgActive` → `Surface` / `SurfaceAlt` / `Accent`
  (checkboxes, text inputs, radio buttons)
- `ImGuiCol.CheckMark` → `Accent`
- `ImGuiCol.Tab` / `TabHovered` / `TabActive` → `Surface` / `SurfaceAlt` / `Accent` — the active tab
  fills solid with the accent color. ImGui has no native underline-only tab style without a
  custom-drawn tab strip; a solid fill is the closest achievable match without building one (out of
  scope per "don't reinvent the UI").
- `ImGuiCol.TableHeaderBg` → `Surface`; `TableRowBg` / `TableRowBgAlt` → `Surface` / `SurfaceAlt`
- `ImGuiCol.Border` → `Border`; `Text` / `TextDisabled` → `Text` / `TextDim`
- `ImGuiStyleVar.FrameRounding` / `TabRounding` / `PopupRounding` / `ScrollbarRounding` /
  `GrabRounding` → `4.0f`
- `ImGuiStyleVar.FrameBorderSize` → `1.0f`

## Testing / verification

Same category as the plugin's other ImGui/UI-facing code (`MainWindow`, `PathTreeView`): not
unit-testable, since ImGui requires a live rendering context. Build-verify only, followed by manual
in-game verification — load the plugin, open `/porganizer`, and visually confirm each tab against the
approved mockup (screenshot comparison, not a formal checklist beyond "does it look like the mockup
and remain fully readable/usable").

## Out of scope

- Any underline-only or otherwise custom-drawn tab indicator (native filled-tab styling is accepted).
- Popup drop shadows (not natively supported by ImGui without custom draw-list work; the app's
  `Theme.xaml` uses them on its ComboBox popup, the plugin has no equivalent popup surface today).
- Reworking `PathTreeView`'s or `MainWindow`'s layout/structure — this is a color/style pass only, no
  behavior or layout changes.
- A light-mode variant — the plugin is always dark, matching in-game overlay convention; the app's
  light theme was the color *source*, not a mode to replicate literally.
