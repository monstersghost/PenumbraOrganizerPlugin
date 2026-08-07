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
    private void DrawRecoveryPanelIfNeeded()
    {
        var operationState = _plugin.OperationController.State;
        if (!operationState.RequiresRecovery)
            return;

        ImGui.TextColored(PluginTheme.CollisionBad, "An interrupted organizer operation was found.");

        if (_plugin.OperationController.IsBlockedByMultipleRoots)
        {
            // Precise about what clicking one row actually does: it does NOT turn that operation into
            // an ordinary single recovery - it permanently marks it Keep Current, abandoning it, and
            // ONE OF THE REMAINING operations may then become the ordinary single recovery. Getting
            // this wrong in the copy would understate how destructive the per-row action is.
            ImGui.TextWrapped(
                "Multiple interrupted operations were found. You can resolve one at a time below by " +
                "keeping its current state - the recovery graph is then recalculated for what's left, " +
                "which may become a smaller blocked set, a different blocked set of the same size (if " +
                "the resolved operation's parent is promoted to a new leaf), a single recoverable " +
                "operation, or fully resolved. You can also abandon all of them at once and accept " +
                "whatever Penumbra currently has as correct - this does not undo or redo any moves for any of them, it " +
                "only stops the plugin from blocking further actions.");

            ImGui.Spacing();
            var blocked = _plugin.OperationController.GetBlockedOperations();
            foreach (var (operationId, journal) in blocked.OrderByDescending(b => b.Journal.UpdatedAt))
            {
                ImGui.TextUnformatted($"{journal.Type} - {journal.Stage} - interrupted {journal.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
                // Same wrapping idiom as DrawWrappingButtonRow - the row's text is unbounded
                // (Stage names vary widely in length), so only chain the button onto the same
                // line if it actually still fits at this window's current width; otherwise it
                // drops to its own line instead of spilling past the window edge.
                var buttonLabel = $"Keep Current State##multiroot-{operationId}";
                var buttonWidth = ImGui.CalcTextSize("Keep Current State").X + ImGui.GetStyle().FramePadding.X * 2;
                var textEndX = ImGui.GetItemRectMax().X;
                var windowVisibleX2 = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X;
                if (textEndX + ImGui.GetStyle().ItemSpacing.X + buttonWidth < windowVisibleX2)
                    ImGui.SameLine();
                if (ImGui.Button(buttonLabel))
                    ImGui.OpenPopup($"Keep current state for {operationId}?");

                ImGui.SetNextWindowSize(new Vector2(StandardPopupWidth, 0), ImGuiCond.Appearing);
                if (ImGui.BeginPopupModal($"Keep current state for {operationId}?"))
                {
                    ImGui.TextWrapped("This selected operation cannot later be continued or restored - it will be permanently abandoned.");
                    ImGui.TextWrapped("Any other interrupted operations found stay blocked until resolved separately.");
                    if (ImGui.Button("Yes, Keep Current") && ResolveOneMultiRoot(operationId))
                        ImGui.CloseCurrentPopup();
                    ImGui.SameLine();
                    if (ImGui.Button("Cancel"))
                        ImGui.CloseCurrentPopup();
                    ImGui.EndPopup();
                }
            }

            ImGui.Spacing();
            if (ImGui.Button("Accept Current State and Close All Interrupted Operations"))
                ImGui.OpenPopup("Close all interrupted operations?");

            ImGui.SetNextWindowSize(new Vector2(StandardPopupWidth, 0), ImGuiCond.Appearing);
            if (ImGui.BeginPopupModal("Close all interrupted operations?"))
            {
                TextColoredWrapped(ImGuiColors.DalamudYellow,
                    "This abandons every interrupted operation the plugin found. None of them can be " +
                    "continued or rolled back after this - only Keep Current's outcome is possible for all of them.");
                if (ImGui.Button("Yes, Close All"))
                {
                    _plugin.AcceptAllAndCloseInterruptedOperations();
                    ImGui.CloseCurrentPopup();
                }
                ImGui.SameLine();
                if (ImGui.Button("Cancel"))
                    ImGui.CloseCurrentPopup();
                ImGui.EndPopup();
            }

            ImGui.Spacing();
            ImGui.Separator();
            return;
        }

        ImGui.TextWrapped(
            "The plugin found a mod-organizing operation that didn't finish, likely from a crash or force-" +
            "quit mid-Apply or mid-Restore. You can accept whatever Penumbra currently has as the correct " +
            "state and move on, finish the interrupted operation from where it left off, or roll everything " +
            "back to how it was before the interrupted operation started.");

        ImGui.BeginDisabled(!operationState.CanResolveRecovery);
        if (ImGui.Button("Keep Current State"))
            ImGui.OpenPopup("Keep current state?");
        ImGui.EndDisabled();

        ImGui.SetNextWindowSize(new Vector2(StandardPopupWidth, 0), ImGuiCond.Appearing);
        if (ImGui.BeginPopupModal("Keep current state?"))
        {
            ImGui.TextWrapped("This will mark the interrupted operation as resolved and unblock the plugin.");
            if (ImGui.Button("Yes, Keep Current"))
            {
                _plugin.ResolveKeepCurrent();
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(!operationState.CanContinueRecovery);
        if (ImGui.Button("Continue"))
            ImGui.OpenPopup("Continue interrupted operation?");
        ImGui.EndDisabled();

        ImGui.SetNextWindowSize(new Vector2(StandardPopupWidth, 0), ImGuiCond.Appearing);
        if (ImGui.BeginPopupModal("Continue interrupted operation?"))
        {
            ImGui.TextWrapped("This will finish the interrupted operation from where it left off.");
            if (ImGui.Button("Yes, Continue") && ContinueRecovery())
                ImGui.CloseCurrentPopup();
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(!operationState.CanRestorePreviousState);
        if (ImGui.Button("Restore Previous State"))
            ImGui.OpenPopup("Restore to state before the interrupted operation?");
        ImGui.EndDisabled();

        ImGui.SetNextWindowSize(new Vector2(StandardPopupWidth, 0), ImGuiCond.Appearing);
        if (ImGui.BeginPopupModal("Restore to state before the interrupted operation?"))
        {
            ImGui.TextWrapped("This will roll every mod back to how it was before the interrupted operation started.");
            if (ImGui.Button("Yes, Restore") && RestorePreviousState())
                ImGui.CloseCurrentPopup();
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }

        ImGui.Spacing();
        if (ImGui.CollapsingHeader("Details"))
        {
            var artifactStatus = _plugin.OperationController.GetPendingRecoveryArtifactStatus();
            if (artifactStatus is { } status)
            {
                DrawArtifactLine(status.Plan, "Interrupted plan", "Continue");
                DrawArtifactLine(status.Snapshot, "Snapshot", "Restore Previous State");

                var assessment = _plugin.OperationController.GetRecoveryAssessment();
                if (assessment is null)
                {
                    // GetRecoveryAssessment() returning null has two distinct causes needing distinct
                    // messages: classification genuinely hasn't settled yet (RecoveryClassificationPending
                    // true - correct to say "still checking"), or it permanently failed to settle (an
                    // invalid plan/live-read/provider per D2's own non-retryable settling design -
                    // RecoveryClassificationPending is false, and "still checking" would be permanently,
                    // silently wrong).
                    if (operationState.RecoveryClassificationPending)
                        ImGui.TextDisabled("Still checking live mod state...");
                    else
                        TextColoredWrapped(PluginTheme.CollisionBad, "Per-mod classification is unavailable - see the artifact status above.");
                }
                else if (assessment.Classifications.Count == 0)
                {
                    ImGui.TextDisabled("No mods to classify.");
                }
                else
                {
                    using var table = ImRaii.Table("RecoveryClassificationTable", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV);
                    if (table)
                    {
                        ImGui.TableSetupColumn("Mod");
                        ImGui.TableSetupColumn("State");
                        ImGui.TableHeadersRow();
                        foreach (var classification in assessment.Classifications.OrderBy(c => c.Identifier, StringComparer.Ordinal))
                        {
                            ImGui.TableNextRow();
                            ImGui.TableNextColumn();
                            // Wrapped at the column's own width, not clipped - a mod identifier
                            // can be long enough to otherwise get cut off in a narrow window.
                            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
                            ImGui.TextUnformatted(classification.Identifier);
                            ImGui.PopTextWrapPos();
                            ImGui.TableNextColumn();
                            var color = classification.State switch
                            {
                                Organizer.Operations.ItemRecoveryState.AtNeither or Organizer.Operations.ItemRecoveryState.MissingLive => PluginTheme.CollisionBad,
                                Organizer.Operations.ItemRecoveryState.AtIntended or Organizer.Operations.ItemRecoveryState.AtBoth => ImGuiColors.HealerGreen,
                                _ => ImGuiColors.DalamudYellow,
                            };
                            ImGui.TextColored(color, classification.State.ToString());
                        }
                    }
                }
            }
            else
            {
                // artifactStatus is null when this is a live in-session failure (checkpoint-write or
                // refresh/verify settlement failure set RequiresRecovery directly - see
                // OperationController), not a startup-discovered pending recovery. There's no
                // _pendingRecovery, so no artifacts were ever recorded and there is nothing to
                // classify - falling through to the classification-unavailable text would misleadingly
                // point the user at an artifact section that was never shown.
                TextDisabledWrapped("This operation failed during the current session; no interrupted-plan artifacts were recorded.");
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
    }

    private static void DrawArtifactLine(Organizer.Operations.ArtifactCheckStatus status, string artifactName, string unavailableAction)
    {
        switch (status)
        {
            case Organizer.Operations.ArtifactCheckStatus.Unchecked:
                ImGui.TextDisabled($"Checking {artifactName}...");
                break;
            case Organizer.Operations.ArtifactCheckStatus.Missing:
                ImGui.TextColored(PluginTheme.CollisionBad, $"{artifactName} is missing; {unavailableAction} is unavailable.");
                break;
            case Organizer.Operations.ArtifactCheckStatus.Invalid:
                ImGui.TextColored(PluginTheme.CollisionBad, $"{artifactName} is corrupt; {unavailableAction} is unavailable.");
                break;
            case Organizer.Operations.ArtifactCheckStatus.Valid:
                break; // nothing to report
        }
    }
}
