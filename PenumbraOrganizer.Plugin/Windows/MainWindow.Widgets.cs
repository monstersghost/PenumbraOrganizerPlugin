using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using PenumbraOrganizer.Core.Services;
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.LibrarySearch;

namespace PenumbraOrganizer.Plugin.Windows;

public sealed partial class MainWindow
{
    // Standard Dear ImGui button-wrapping idiom: draw each button, then only chain a SameLine if
    // the NEXT button (measured ahead of time via CalcTextSize) would still fit within the
    // window's visible content width - otherwise it naturally drops to a new line instead of
    // being clipped or spilling past the window edge on a narrower/unmaximized window.
    private static void DrawWrappingButtonRow(IReadOnlyList<(string Label, Action OnClick)> buttons)
    {
        var style = ImGui.GetStyle();
        var windowVisibleX2 = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X;
        for (var i = 0; i < buttons.Count; i++)
        {
            var (label, onClick) = buttons[i];
            if (ImGui.Button(label))
                onClick();

            if (i + 1 >= buttons.Count)
                continue;

            var nextButtonWidth = ImGui.CalcTextSize(buttons[i + 1].Label).X + style.FramePadding.X * 2;
            var lastButtonX2 = ImGui.GetItemRectMax().X;
            var nextButtonX2 = lastButtonX2 + style.ItemSpacing.X + nextButtonWidth;
            if (nextButtonX2 < windowVisibleX2)
                ImGui.SameLine();
        }
    }

    // Same wrapping idiom as DrawWrappingButtonRow above, adapted for a run of checkboxes - used
    // wherever a toggle row can have enough items to overflow a narrow window (Search tab: up to
    // 12 categories + Unknown, or 9 equipment slots + Unresolved, none of which fit on one line at
    // this window's 900px minimum width without wrapping).
    private static void DrawWrappingCheckboxRow(IReadOnlyList<(string Label, bool Checked, Action<bool> OnToggle)> items)
    {
        var style = ImGui.GetStyle();
        var windowVisibleX2 = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X;
        for (var i = 0; i < items.Count; i++)
        {
            var (label, isChecked, onToggle) = items[i];
            var value = isChecked;
            if (ImGui.Checkbox(label, ref value))
                onToggle(value);

            if (i + 1 >= items.Count)
                continue;

            var nextLabel = items[i + 1].Label;
            var nextItemWidth = ImGui.GetFrameHeight() + style.ItemInnerSpacing.X + ImGui.CalcTextSize(nextLabel).X;
            var lastItemX2 = ImGui.GetItemRectMax().X;
            var nextItemX2 = lastItemX2 + style.ItemSpacing.X + nextItemWidth;
            if (nextItemX2 < windowVisibleX2)
                ImGui.SameLine();
        }
    }

    // Target-based, not step-based: a cycle-breaking plan has more execution steps than recovery
    // targets (a temporary hop plus a final move both count as steps for one target), so a
    // step-based fraction misrepresents "how many mods are done" to a user whose mental model is
    // mods, not steps (design doc section 2). ProcessedTargets, not SuccessfulTargets, drives the
    // fraction - SuccessfulTargets is a subset of ProcessedTargets (attempted-and-succeeded, not
    // attempted), so a run with even one failure would otherwise leave the bar permanently short of
    // full even after the operation finishes processing everything. Completion (how much work is
    // done) and outcome (whether it succeeded) are separate concerns, shown on separate lines.
    //
    // onCancel is the plugin's real cancellation callback, passed by both Apply and Restore call
    // sites. Cancel is drawn here, not at each call site, so the "reserve width for the button
    // before the full-width progress bar claims it" math isn't duplicated for Apply and Restore
    // separately. cancelButtonId carries a distinct ##-suffix per call site (ImGui requires unique
    // widget IDs across the whole window, not just within one tab, matching this file's own
    // established per-row uniqueness convention documented in DrawHistoryTab).
    private static void DrawOperationProgress(Organizer.Operations.OperationStateSnapshot operationState, string verb, Action? onCancel, string cancelButtonId)
    {
        var fraction = operationState.TotalTargets > 0
            ? (float)operationState.ProcessedTargets / operationState.TotalTargets
            : 1f;

        var showCancel = onCancel is not null && operationState.CanRequestCancellation;
        var barWidth = -1f;
        var buttonWidth = 0f;
        if (showCancel)
        {
            buttonWidth = ImGui.CalcTextSize("Cancel").X + ImGui.GetStyle().FramePadding.X * 2;
            var spacing = ImGui.GetStyle().ItemSpacing.X;
            barWidth = MathF.Max(1f, ImGui.GetContentRegionAvail().X - buttonWidth - spacing);
        }

        ImGui.ProgressBar(fraction, new Vector2(barWidth, 0), $"{operationState.ProcessedTargets}/{operationState.TotalTargets} processed");
        if (showCancel)
        {
            ImGui.SameLine();
            if (ImGui.Button($"Cancel{cancelButtonId}", new Vector2(buttonWidth, 0)))
                onCancel!();
        }

        var failedTargets = operationState.ProcessedTargets - operationState.SuccessfulTargets;
        ImGui.TextDisabled(failedTargets > 0
            ? $"{operationState.SuccessfulTargets} succeeded, {failedTargets} failed"
            : $"{operationState.SuccessfulTargets} succeeded");
        if (operationState.LastProcessedDisplayName is { } name)
            ImGui.TextDisabled($"{verb}: {name}");
        ImGui.TextDisabled($"{operationState.ProcessedSteps}/{operationState.TotalSteps} steps ({operationState.Stage})");
    }

    // Progress bar plus a right-aligned Cancel, reserving the button's width before the bar claims
    // it - same layout approach as DrawOperationProgress, against the library work snapshot.
    private static void DrawLibraryWorkProgress(LibraryWork.LibraryWorkStateSnapshot state, Action onCancel)
    {
        if (!state.IsRunning)
            return;

        var fraction = state.TotalItems > 0 ? (float)state.ProcessedItems / state.TotalItems : 0f;
        var buttonWidth = ImGui.CalcTextSize("Cancel").X + ImGui.GetStyle().FramePadding.X * 2;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var barWidth = state.CanCancel
            ? MathF.Max(1f, ImGui.GetContentRegionAvail().X - buttonWidth - spacing)
            : -1f;

        ImGui.ProgressBar(fraction, new Vector2(barWidth, 0),
            $"{state.ProcessedItems}/{state.TotalItems} mods");
        if (state.CanCancel)
        {
            ImGui.SameLine();
            if (ImGui.Button($"Cancel##library-work-{state.JobDisplayName}", new Vector2(buttonWidth, 0)))
                onCancel();
        }

        ImGui.TextDisabled($"{state.JobDisplayName}: {state.Phase}");
    }

    private static void DrawLibraryWorkOutcome(LibraryWork.LibraryWorkStateSnapshot state)
    {
        if (state.IsRunning)
            return;

        switch (state.LastOutcome)
        {
            case LibraryWork.LibraryWorkOutcome.Failed:
                ImGui.TextColored(PluginTheme.CollisionBad, state.LastError ?? "The run failed.");
                break;
            case LibraryWork.LibraryWorkOutcome.StaleModList:
                ImGui.TextColored(ImGuiColors.DalamudYellow,
                    "The mod list changed while this was running, so nothing was applied. Run it again.");
                break;
            case LibraryWork.LibraryWorkOutcome.Cancelled:
                ImGui.TextDisabled("Cancelled. The previous results are unchanged.");
                break;
            case LibraryWork.LibraryWorkOutcome.Completed:
            case null:
                break;
        }
    }
}
