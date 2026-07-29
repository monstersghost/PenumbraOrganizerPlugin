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

public sealed class MainWindow : Window, IDisposable
{
    private const int MaxEventLogLines = 200;
    private const int RecentOperationsCount = 20;
    private const float StandardPopupWidth = 420f;
    private const float DetailedPopupWidth = 560f;

    private readonly Plugin _plugin;
    private readonly CreatorCanonicalizer _creatorCanonicalizer = new();
    private readonly List<string> _eventLog = [];
    private string? _lastError;
    private string _protectFilter = string.Empty;
    private float _protectedFolderListHeight = 220f;
    private string _manualFolderInput = string.Empty;
    private readonly HashSet<string> _selectedManualModIdentifiers = new(StringComparer.Ordinal);
    private string _manualAssignFilter = string.Empty;
    private string? _lastManualAssignSummary;
    private string? _lastExportPath;
    private string? _lastDiagnosticDumpPath;
    private string? _lastWorkbookExportPath;
    private int _workbookStrategyIndex = 2; // "By Type Then Creator" default
    private Organizer.WorkbookImportResultView? _lastWorkbookImportResult;
    private long _lastConsumedCompletion;

    // Set by the completion consumer, consumed inside the Review Changes tab's own draw. The
    // OpenPopup call CANNOT move into the consumer: BeginTabBar pushes an ID override, so the
    // matching BeginPopupModal inside the tab resolves the popup name against a different ID stack
    // than Draw()'s root - a root-level OpenPopup would mark a popup open that the tab's
    // BeginPopupModal never sees. The flag carries the decision across that scope boundary.
    private bool _pendingApplyReminder;
    private string _createBackupLabelInput = string.Empty;
    private Guid? _pendingRestoreSnapshotId;
    private Organizer.RestorePlan? _pendingRestorePreview;
    private IReadOnlyList<Organizer.Operations.OperationJournal> _recentOperations = [];
    private string? _recentOperationsError;
    private bool _recentOperationsSectionWasOpen;
    private IReadOnlyList<Organizer.RollbackSnapshot>? _historyCache;
    private Organizer.FolderDetectionResult? _orphanedFolders;
    private DateTimeOffset? _organizationJsonLastReadAt;
    private readonly HashSet<string> _selectedOrphans = new(StringComparer.Ordinal);
    private bool _folderReloadRequired;
    private Organizer.FolderCleanupResult? _lastCleanupResult;
    private Organizer.FolderRollbackResult? _lastFolderRollbackResult;
    private Task? _npcRefreshTask;
    private Organizer.NpcNames.NpcNameRefreshResult? _npcRefreshResult;
    private readonly FileDialogManager _fileDialogManager = new();

    private string _librarySearchNameQuery = string.Empty;
    private string _librarySearchItemQuery = string.Empty;
    private readonly HashSet<ModCategory> _librarySearchCategories = new(SearchableCategories);
    private bool _librarySearchIncludeUnknown = true;
    private readonly HashSet<EquipmentSlot> _librarySearchSlots = new(Enum.GetValues<EquipmentSlot>());
    private bool _librarySearchIncludeUnresolved = true;
    private string? _librarySearchSelectedModIdentifier;

    private static readonly ModCategory[] SearchableCategories =
    [
        ModCategory.Gear, ModCategory.NPC, ModCategory.Mount, ModCategory.Minion,
        ModCategory.Animation, ModCategory.VFX, ModCategory.Furniture, ModCategory.Sound,
        ModCategory.Face, ModCategory.Hair, ModCategory.Body, ModCategory.Skin,
    ];

    private static readonly (string Label, PenumbraOrganizer.Core.Models.OrganizationStrategy Strategy)[] WorkbookStrategyOptions =
    [
        ("By Creator", PenumbraOrganizer.Core.Models.OrganizationStrategy.CreatorOnly),
        ("By Mod Type", PenumbraOrganizer.Core.Models.OrganizationStrategy.TypeOnly),
        ("By Type Then Creator", PenumbraOrganizer.Core.Models.OrganizationStrategy.TypeThenCreator),
        ("By Creator Then Type", PenumbraOrganizer.Core.Models.OrganizationStrategy.CreatorThenType),
    ];

    private static readonly string PluginVersion =
        typeof(MainWindow).Assembly.GetName().Version?.ToString(4) ?? "unknown";

    public MainWindow(Plugin plugin)
        : base($"Penumbra Organizer v{PluginVersion}###PenumbraOrganizerPluginMain")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(900, 480),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        _plugin = plugin;
    }

    public void Dispose()
    {
    }

    internal void LogEvent(string message)
    {
        _eventLog.Insert(0, $"{DateTime.Now:HH:mm:ss} {message}");
        if (_eventLog.Count > MaxEventLogLines)
            _eventLog.RemoveRange(MaxEventLogLines, _eventLog.Count - MaxEventLogLines);
    }

    public override void Draw()
    {
        using var theme = PluginTheme.Push();

        ConsumeCompletionIfNew();

        DrawRecoveryPanelIfNeeded();

        if (_lastError != null)
            ImGui.TextColored(PluginTheme.CollisionBad, _lastError);

        using (var tabBar = ImRaii.TabBar("MainTabs"))
        {
            if (tabBar)
            {
                DrawScanTab();
                DrawProtectTab();
                DrawSortTab();
                DrawReviewTab();
                DrawHistoryTab();
                DrawSearchTab();
            }
        }

        _fileDialogManager.Draw();
    }

    // The single place an operation completion turns into UI consequences. Guarded by a generation
    // comparison rather than a per-kind latch, so a terminal snapshot that stays published for many
    // frames is consumed exactly once, and recovery successors are consumed by the same code as
    // ordinary operations rather than needing their own polling.
    //
    // DELIBERATE BEHAVIOUR CHANGE, the one exception to this plan's behaviour-preserving rule: the
    // old latches lived inside tab draw methods that early-return when their tab is not selected,
    // so completion consequences waited until the user visited the right tab. This consumer fires
    // on the frame completion is first observed, whatever tab is visible. Deferred consumption was
    // itself a latent staleness bug (a completed Apply's RunScan would not happen until a tab
    // visit), so immediate consumption is adopted knowingly rather than reproduced.
    private void ConsumeCompletionIfNew()
    {
        var state = _plugin.OperationController.State;
        if (state.CompletionGeneration <= _lastConsumedCompletion)
            return;

        _lastConsumedCompletion = state.CompletionGeneration;

        // A completion may mean history moved - most operations append a pre-operation snapshot
        // before they start, but recovery successors write theirs only into the operation bundle.
        // Invalidating unconditionally avoids having to reason about which kinds mutate it.
        _historyCache = null;

        switch (state.Kind)
        {
            case Organizer.Operations.OperationType.Apply:
                // Penumbra's own tree is now stale relative to what was just written, and
                // OrganizerState's cached CurrentPath values are stale too - RunScan re-reads both.
                RunScan();
                if (state.SuccessfulTargets > 0)
                    _pendingApplyReminder = true;
                break;

            case Organizer.Operations.OperationType.Restore:
                RunScan(); // matches today's Restore completion block: cache null + RunScan, no popup
                break;
        }
    }

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

    private void DrawScanTab()
    {
        using var tab = ImRaii.TabItem("Scan");
        if (!tab)
            return;

        var scanOperationState = _plugin.OperationController.State;
        using (PluginTheme.PrimaryButton())
        {
            ImGui.BeginDisabled(!scanOperationState.CanScan);
            if (ImGui.Button("Refresh mod list"))
                RunScan();
            ImGui.EndDisabled();
        }
        if (!scanOperationState.CanScan && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Another operation is in progress or requires recovery.");

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

    private void DrawProtectTab()
    {
        using var tab = ImRaii.TabItem("Protect");
        if (!tab)
            return;

        if (ImGui.Button("Toggle protect all"))
        {
            var allProtected = _plugin.OrganizerState.Mods.All(m => m.Protected);
            _plugin.OrganizerState.SetAllProtection(!allProtected);
            SaveProtectionStateSafely();
        }

        ImGui.SameLine();
        if (ImGui.Button("Toggle Heliosphere protection"))
        {
            var allProtected = _plugin.OrganizerState.Mods
                .Where(m => m.HeliosphereManaged)
                .All(m => m.Protected);
            _plugin.OrganizerState.SetHeliosphereProtection(!allProtected);
            SaveProtectionStateSafely();
        }

        var heliosphereMods = _plugin.OrganizerState.Mods.Where(m => m.HeliosphereManaged).ToList();
        if (heliosphereMods.Count > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"({heliosphereMods.Count(m => m.Protected)}/{heliosphereMods.Count} Heliosphere mods protected)");
        }

        ImGui.Spacing();
        ImGui.InputText("Search mods and folders", ref _protectFilter, 256);
        ImGui.Spacing();

        var filter = _protectFilter.Trim();
        var protectedFolders = _plugin.OrganizerState.ProtectedFolders.ToHashSet(StringComparer.Ordinal);
        var knownFolders = _plugin.OrganizerState.KnownFolders.ToHashSet(StringComparer.Ordinal);
        var folderRows = knownFolders
            .Union(protectedFolders, StringComparer.Ordinal)
            .Where(f => filter.Length == 0 || f.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        ImGui.TextUnformatted("Folders");
        using (var folderChild = ImRaii.Child("ProtectedFolderList", new Vector2(0, _protectedFolderListHeight), border: true))
        {
            if (folderChild)
            {
                foreach (var folder in folderRows)
                {
                    var isExactlyProtected = protectedFolders.Contains(folder);
                    var label = knownFolders.Contains(folder) ? folder : $"{folder} (currently empty)";
                    var isChecked = isExactlyProtected;
                    if (ImGui.Checkbox($"{label}##protect-folder-{folder}", ref isChecked))
                    {
                        _plugin.OrganizerState.SetFolderProtected(folder, isChecked);
                        SaveProtectionStateSafely();
                    }

                    if (!isExactlyProtected)
                    {
                        var ancestor = protectedFolders.FirstOrDefault(f =>
                            !f.Equals(folder, StringComparison.Ordinal)
                            && folder.StartsWith(f + "/", StringComparison.Ordinal));
                        if (ancestor is not null)
                        {
                            ImGui.SameLine();
                            ImGui.TextDisabled($"(covered by protected folder \"{ancestor}\")");
                        }
                    }
                }
            }
        }

        // Drag-to-resize grip: a thin full-width button whose vertical drag delta adjusts the
        // child's height above (min/max clamped to keep it usable). ImGui has no built-in
        // resizable child, so this is the standard manual-splitter pattern.
        ImGui.Button("##protect-folder-list-resize", new Vector2(-1, 6));
        if (ImGui.IsItemActive())
            _protectedFolderListHeight = Math.Clamp(_protectedFolderListHeight + ImGui.GetIO().MouseDelta.Y, 80f, 600f);
        if (ImGui.IsItemHovered() || ImGui.IsItemActive())
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNs);

        ImGui.Spacing();
        ImGui.TextUnformatted("Mods");
        var explicitIdentifiers = _plugin.OrganizerState.ProtectedModIdentifiers.ToHashSet(StringComparer.Ordinal);
        // Fills whatever vertical space remains in the tab (height -1, ImGui's "leave 1px at the
        // bottom" convention) rather than a fixed height like the Folders list above - this is
        // the last section in the tab, so there's nothing below it to preserve room for, and this
        // way it adapts to the window size instead of needing its own manual resize handle.
        using (var modChild = ImRaii.Child("ProtectedModList", new Vector2(0, -1), border: true))
        {
            if (modChild)
            {
                // Heliosphere-managed mods first (stable within each group) - they're almost
                // always already protected and are the ones users check on most, per feedback.
                // Folders above are deliberately untouched by this ordering.
                foreach (var mod in _plugin.OrganizerState.Mods.OrderByDescending(m => m.HeliosphereManaged))
                {
                    if (filter.Length > 0
                        && !mod.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        && !mod.Identifier.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        && !mod.Author.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        && !mod.CurrentPath.Contains(filter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var isProtected = mod.Protected;
                    if (ImGui.Checkbox($"{mod.Name}##protect-{mod.Identifier}", ref isProtected))
                    {
                        _plugin.OrganizerState.SetProtected(mod.Identifier, isProtected);
                        SaveProtectionStateSafely();
                    }

                    if (mod.Protected && !explicitIdentifiers.Contains(mod.Identifier))
                    {
                        ImGui.SameLine();
                        if (mod.HeliosphereManaged)
                        {
                            ImGui.TextDisabled("(Heliosphere)");
                        }
                        else
                        {
                            var parent = Organizer.OrganizationCleanupPlanner.GetVirtualParent(mod.CurrentPath);
                            var coveringFolder = parent is null
                                ? null
                                : protectedFolders.FirstOrDefault(f =>
                                    parent.Equals(f, StringComparison.Ordinal) || parent.StartsWith(f + "/", StringComparison.Ordinal));
                            ImGui.TextDisabled(coveringFolder is not null ? $"(via folder: {coveringFolder})" : "(protected)");
                        }
                    }
                }
            }
        }
    }

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

    // Popups auto-size to their content by default, which makes a single long, unwrapped
    // TextColored sentence stretch the whole popup to match (a genuinely "too big" popup, often
    // wider than the main window's own minimum width). ImGui has no built-in colored+wrapped text
    // call, so this pairs PushStyleColor with TextWrapped, matching PluginTheme.cs's own
    // push-then-pop style for temporary color overrides.
    private static void TextColoredWrapped(Vector4 color, string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    // Same rationale as TextColoredWrapped above, for the dimmed/disabled text color -
    // ImGui.TextDisabled has no built-in wrapped variant either, and several long explanatory
    // lines in this file use it.
    private static void TextDisabledWrapped(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
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

    private void DrawSortTab()
    {
        using var tab = ImRaii.TabItem("Sort");
        if (!tab)
            return;

        DrawWrappingButtonRow(
        [
            ("By Creator", () => _plugin.OrganizerState.SortByCreator(_creatorCanonicalizer.Canonicalize)),
            ("By Mod Type", () => _plugin.OrganizerState.SortByModType()),
            ("By Mod Type Detailed", () => _plugin.OrganizerState.SortByModTypeDetailed()),
            ("By Type Then Creator", () => _plugin.OrganizerState.SortByTypeThenCreatorFlat(_creatorCanonicalizer.Canonicalize)),
            ("By Type Then Creator (Detailed)", () => _plugin.OrganizerState.SortByTypeThenCreator(_creatorCanonicalizer.Canonicalize)),
            ("By Creator Then Type", () => _plugin.OrganizerState.SortByCreatorThenTypeFlat(_creatorCanonicalizer.Canonicalize)),
            ("By Creator Then Type (Detailed)", () => _plugin.OrganizerState.SortByCreatorThenType(_creatorCanonicalizer.Canonicalize)),
            ("Import Workbook", () => _fileDialogManager.OpenFileDialog(
                "Import Workbook",
                ".xlsx",
                (success, paths) =>
                {
                    if (success && paths.Count > 0)
                        ImportWorkbook(paths[0]);
                },
                selectionCountMax: 1)),
        ]);

        if (_lastWorkbookImportResult is not null)
        {
            ImGui.TextWrapped(_lastWorkbookImportResult.Summary);
            foreach (var error in _lastWorkbookImportResult.Errors)
                TextColoredWrapped(PluginTheme.CollisionBad, $"  {error}");
            foreach (var warning in _lastWorkbookImportResult.Warnings)
                TextColoredWrapped(ImGuiColors.DalamudYellow, $"  {warning}");
        }

        ImGui.Spacing();
        var npcRefreshInFlight = _npcRefreshTask is { IsCompleted: false };
        ImGui.BeginDisabled(npcRefreshInFlight);
        if (ImGui.Button("Refresh NPC list from wiki"))
        {
            _npcRefreshResult = null;
            _lastError = null;
            _npcRefreshTask = RefreshNpcNamesAsync();
        }
        ImGui.EndDisabled();

        if (npcRefreshInFlight)
        {
            ImGui.SameLine();
            ImGui.TextUnformatted("Refreshing... (this can take a few minutes for a full scrape)");
        }

        if (_npcRefreshResult is not null)
        {
            if (_npcRefreshResult.RecoveredFromCorruption)
                TextColoredWrapped(ImGuiColors.DalamudYellow,
                    "The existing NPC name list was unreadable and has been reset from the bundled "
                    + "seed list; the old file was preserved alongside it as a timestamped backup.");

            foreach (var category in _npcRefreshResult.Categories)
            {
                if (category.FailureReason is not null)
                    TextColoredWrapped(PluginTheme.CollisionBad, $"  {category.CategoryName} failed: {category.FailureReason}");
                else
                    ImGui.TextUnformatted($"  {category.CategoryName}: +{category.AddedCount}");
            }
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Start Manually: check mods below, type a folder, click Assign.");

        ImGui.InputText("Search mods##manual-assign", ref _manualAssignFilter, 256);
        ImGui.InputText("Destination folder", ref _manualFolderInput, 256);

        ImGui.SameLine();
        if (ImGui.Button($"Assign {_selectedManualModIdentifiers.Count} selected mods")
            && _manualFolderInput.Length > 0 && _selectedManualModIdentifiers.Count > 0)
        {
            var batchResults = _plugin.OrganizerState.AssignManualBatch(_selectedManualModIdentifiers, _manualFolderInput);
            var succeeded = batchResults.Count(r => r.Success);
            _lastManualAssignSummary = $"{succeeded} assigned, {batchResults.Count - succeeded} skipped (no longer eligible)";
        }

        if (_lastManualAssignSummary is not null)
            ImGui.TextUnformatted(_lastManualAssignSummary);

        // Reconcile before rendering: drop any selected identifier that is no longer present or
        // has since become protected (by any source, including a folder rule toggled on the
        // Protect tab), so stale checkmarks never display and Assign never targets them.
        var eligibleIdentifiers = _plugin.OrganizerState.Mods
            .Where(m => !m.Protected)
            .Select(m => m.Identifier)
            .ToHashSet(StringComparer.Ordinal);
        _selectedManualModIdentifiers.IntersectWith(eligibleIdentifiers);

        ImGui.Spacing();
        var manualFilter = _manualAssignFilter.Trim();
        using (var child = ImRaii.Child("ManualModList", new Vector2(0, 300), border: true))
        {
            if (child)
            {
                foreach (var mod in _plugin.OrganizerState.Mods.Where(m => !m.Protected))
                {
                    if (manualFilter.Length > 0
                        && !mod.Name.Contains(manualFilter, StringComparison.OrdinalIgnoreCase)
                        && !mod.Identifier.Contains(manualFilter, StringComparison.OrdinalIgnoreCase)
                        && !mod.CurrentPath.Contains(manualFilter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var isSelected = _selectedManualModIdentifiers.Contains(mod.Identifier);
                    if (ImGui.Checkbox($"{mod.Name} ({mod.CurrentPath})##manual-{mod.Identifier}", ref isSelected))
                    {
                        if (isSelected)
                            _selectedManualModIdentifiers.Add(mod.Identifier);
                        else
                            _selectedManualModIdentifiers.Remove(mod.Identifier);
                    }
                }
            }
        }
    }

    private void DrawReviewTab()
    {
        using var tab = ImRaii.TabItem("Review Changes");
        if (!tab)
            return;

        var result = _plugin.OrganizerState.Validate();

        if (!result.HasIssues)
            ImGui.TextColored(PluginTheme.ChangedGood, "No issues found.");

        foreach (var identifier in result.ProtectedViolations)
            TextColoredWrapped(PluginTheme.CollisionBad, $"Protected mod changed: {identifier}");

        foreach (var (path, identifiers) in result.PathCollisions)
            TextColoredWrapped(PluginTheme.CollisionBad, $"Collision at '{path}': {string.Join(", ", identifiers)}");

        ImGui.Spacing();
        PathTreeView.Draw(_plugin.OrganizerState.Mods, showProposedColumn: true);

        ImGui.Spacing();
        if (ImGui.Button("Export"))
            _lastExportPath = _plugin.ExportReview();

        ImGui.SameLine();
        if (ImGui.Button("Show Config File"))
            OpenConfigFile();

        ImGui.SameLine();
        if (ImGui.Button("Create Diagnostic Dump"))
            _lastDiagnosticDumpPath = CreateDiagnosticDump();

        // File paths are unbounded in length (depend on the user's own Windows profile/folder
        // depth) - wrapped, and with any action button on its own line below rather than chained
        // via SameLine, so a long path can never push a button past the window edge.
        if (_lastExportPath is not null)
            ImGui.TextWrapped($"Exported to: {_lastExportPath}");

        if (_lastDiagnosticDumpPath is not null)
        {
            ImGui.TextWrapped($"Diagnostic dump written to: {_lastDiagnosticDumpPath}");
            if (ImGui.Button("Show Dump File"))
                OpenContainingFolder(_lastDiagnosticDumpPath);
        }

        ImGui.Spacing();
        var strategyLabels = WorkbookStrategyOptions.Select(o => o.Label).ToArray();
        ImGui.SetNextItemWidth(220);
        ImGui.Combo("Workbook suggestion strategy", ref _workbookStrategyIndex, strategyLabels, strategyLabels.Length);

        ImGui.SameLine();
        if (ImGui.Button("Export Workbook"))
            ExportWorkbook(WorkbookStrategyOptions[_workbookStrategyIndex].Strategy);

        if (_lastWorkbookExportPath is not null)
        {
            // Own line, wrapped, and the button below it rather than SameLine'd - same reasoning
            // as the Export/Diagnostic Dump paths above: the path itself is unbounded in length.
            ImGui.TextWrapped($"Workbook exported to: {_lastWorkbookExportPath}");
            if (ImGui.Button("Open Workbook"))
                OpenFileWithDefaultApp(_lastWorkbookExportPath);
        }

        ImGui.Spacing();
        if (result.HasIssues && ImGui.Button("Protect & Skip All Blocking Mods"))
        {
            _plugin.ProtectAndSkipBlockingMods();
            result = _plugin.OrganizerState.Validate();
        }

        ImGui.Spacing();
        var touchedCount = _plugin.OrganizerState.Mods
            .Count(m => !m.Protected && !string.Equals(m.ProposedPath, m.CurrentPath, StringComparison.OrdinalIgnoreCase));

        var operationState = _plugin.OperationController.State;
        if (_pendingApplyReminder)
        {
            _pendingApplyReminder = false;
            // In-scope with this tab's BeginPopupModal - see the field's comment for why the
            // consumer cannot call this itself.
            ImGui.OpenPopup("Apply complete - Rediscover Mods reminder");
        }

        ImGui.BeginDisabled(result.HasIssues || !operationState.CanStartApply);
        var applyClicked = ImGui.Button("Apply");
        ImGui.EndDisabled();
        if (applyClicked)
            ImGui.OpenPopup("Apply changes?");

        ImGui.SetNextWindowSize(new Vector2(StandardPopupWidth, 0), ImGuiCond.Appearing);
        if (ImGui.BeginPopupModal("Apply changes?"))
        {
            ImGui.TextUnformatted($"Apply changes to {touchedCount} mods?");
            if (ImGui.Button("Yes, Apply"))
            {
                ApplyChanges();
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }

        ImGui.SetNextWindowSize(new Vector2(480, 0), ImGuiCond.Appearing);
        ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        if (ImGui.BeginPopupModal("Apply complete - Rediscover Mods reminder"))
        {
            ImGui.TextUnformatted("Apply complete.");
            ImGui.TextWrapped(
                "Penumbra doesn't always write organization.json to disk immediately, so any folders "
                + "that are now empty may not be detected here yet, and may still show in Penumbra's own mod tree.");
            ImGui.TextWrapped(
                "Open Penumbra's Settings tab and click Rediscover Mods - this flushes the change to disk "
                + "and removes any stray empty folders from Penumbra's tree. Then Folder Cleanup below will "
                + "detect them accurately.");
            if (ImGui.Button("Understood"))
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }

        // Gated on Kind == Apply so an in-progress or just-completed Restore (sharing the same
        // OperationController) never renders here - CanStartApply/CanStartRestore are the same value
        // today, so Kind is the only field that actually distinguishes the two operations.
        if (operationState.Stage is not null && operationState.Kind == Organizer.Operations.OperationType.Apply)
        {
            if (!operationState.CanStartApply && !operationState.RequiresRecovery)
                DrawOperationProgress(operationState, "Applying", _plugin.RequestCancellation, "##cancel-apply");
            else if (operationState.RequiresRecovery)
                ImGui.TextColored(PluginTheme.CollisionBad, "Apply requires recovery - see the plugin log.");
            else
                ImGui.TextUnformatted($"Last Apply: {operationState.Stage} ({operationState.SuccessfulTargets}/{operationState.TotalTargets} succeeded).");
        }

        ImGui.Spacing();
        ImGui.Separator();
        DrawOrphanedFoldersSection();
    }

    private void RefreshRecentOperations()
    {
        try
        {
            _recentOperations = Organizer.Operations.OperationBundleDiscovery.LoadRecentCompletedJournals(_plugin.OperationsRoot, take: RecentOperationsCount);
            _recentOperationsError = null;
        }
        catch (Exception ex)
        {
            _recentOperations = [];
            _recentOperationsError = $"Could not load recent operations: {ex.Message}";
            Plugin.Log.Warning(ex, "Loading recent operations failed.");
        }
    }

    private void DrawHistoryTab()
    {
        using var tab = ImRaii.TabItem("History");
        if (!tab)
            return;

        var operationState = _plugin.OperationController.State;

        ImGui.InputText("Label (optional)", ref _createBackupLabelInput, 200);
        ImGui.SameLine();
        ImGui.BeginDisabled(!operationState.CanCreateBackup);
        if (ImGui.Button("Create Backup"))
        {
            var label = _createBackupLabelInput.Trim();
            CreateBackup(label.Length > 0 ? label : null);
            _createBackupLabelInput = string.Empty;
        }
        ImGui.EndDisabled();
        if (!operationState.CanCreateBackup && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Another operation is in progress or requires recovery.");

        ImGui.Spacing();
        ImGui.Separator();

        _historyCache ??= _plugin.LoadHistory();
        var history = _historyCache
            .OrderByDescending(s => s.CreatedAt)
            .ToList();

        if (history.Count == 0)
        {
            TextDisabledWrapped("No backups yet. Backups are created automatically before every Apply and Restore.");
        }

        foreach (var snapshot in history)
        {
            // Per-row widget uniqueness follows this codebase's existing convention (see
            // DrawProtectTab's "{mod.Name}##protect-{mod.Identifier}") rather than ImRaii.PushId,
            // whose exact signature in this Dalamud version wasn't worth gambling on.
            var title = snapshot.Label is { Length: > 0 } label
                ? $"{snapshot.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} — {label}"
                : $"{snapshot.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} — {snapshot.AutoDescription}";
            // Wrapped, and NOT SameLine'd with the buttons below - the label is user-supplied
            // free text up to 200 characters (see the "Label (optional)" input above), unbounded
            // enough that chaining it with the Restore/Delete buttons could push them past the
            // window edge.
            ImGui.TextWrapped($"{title} ({snapshot.ModPaths.Count} mods)");

            ImGui.BeginDisabled(!operationState.CanStartRestore);
            var restoreButtonClicked = ImGui.Button($"Restore##restore-{snapshot.Id}");
            ImGui.EndDisabled();
            if (!operationState.CanStartRestore && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Another operation is in progress or requires recovery.");
            if (restoreButtonClicked)
            {
                _pendingRestoreSnapshotId = snapshot.Id;
                // Compute the preview once, here, rather than every frame the popup is drawn -
                // PreviewRestore does a disk read (RollbackHistory.Load) and a Penumbra IPC call,
                // which is too expensive to repeat per-frame for large mod libraries.
                _pendingRestorePreview = _plugin.PreviewRestore(snapshot.Id);
                ImGui.OpenPopup("Restore snapshot?");
            }

            ImGui.SameLine();
            ImGui.BeginDisabled(!operationState.CanCreateBackup);
            var deleteButtonClicked = ImGui.Button($"Delete##delete-{snapshot.Id}");
            ImGui.EndDisabled();
            if (!operationState.CanCreateBackup && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Another operation is in progress or requires recovery.");
            if (deleteButtonClicked)
            {
                DeleteHistorySnapshot(snapshot.Id);
            }

            if (_pendingRestoreSnapshotId == snapshot.Id)
                ImGui.SetNextWindowSize(new Vector2(DetailedPopupWidth, 0), ImGuiCond.Appearing);
            if (_pendingRestoreSnapshotId == snapshot.Id && ImGui.BeginPopupModal("Restore snapshot?"))
            {
                // Exact preview via PreviewRestore, not an ad-hoc estimate: the previous
                // "Up to N mods... may move" counts only checked snapshot membership, not whether
                // the historical path actually differs from the current one - this replaces that
                // with the real plan the Restore button will execute.
                // Cached at popup-open time (see the Restore button above) rather than recomputed
                // here - this body runs every frame the popup is visible, and PreviewRestore does a
                // disk read plus a Penumbra IPC call.
                if (_pendingRestorePreview is not { } preview)
                {
                    ImGui.EndPopup();
                    continue;
                }
                // OrganizerState.Mods reflects the last Scan, while preview.Moves comes from a
                // fresh IPC read taken when the popup opened - these could disagree if Penumbra
                // state changed since the last Scan, but that's an acceptable edge case here.
                var modsByIdentifier = _plugin.OrganizerState.Mods.ToDictionary(m => m.Identifier, m => m);
                var protectedMovingCount = preview.Moves.Count(move =>
                    modsByIdentifier.TryGetValue(move.Identifier, out var mod) && mod.Protected);
                var heliosphereMovingCount = preview.Moves.Count(move =>
                    modsByIdentifier.TryGetValue(move.Identifier, out var mod) && mod.HeliosphereManaged);

                ImGui.TextWrapped($"Restore to: {title}");
                ImGui.TextWrapped($"{preview.Moves.Count} mods will move to their snapshot path.");
                ImGui.TextWrapped($"{preview.UnchangedIdentifiers.Count} mods are already at their snapshot path.");
                ImGui.TextWrapped($"{preview.RootRelocatedIdentifiers.Count} mods installed since this snapshot will be moved to the Penumbra root.");
                ImGui.TextWrapped($"{preview.SkippedUninstalledIdentifiers.Count} mods from this snapshot are no longer installed and will be skipped.");
                TextColoredWrapped(ImGuiColors.DalamudYellow,
                    "Exact Restore: this reproduces the snapshot's historical paths, including for mods that are "
                    + "currently protected or Heliosphere-managed.");
                if (protectedMovingCount > 0 || heliosphereMovingCount > 0)
                    TextColoredWrapped(ImGuiColors.DalamudYellow,
                        $"{protectedMovingCount} currently protected and {heliosphereMovingCount} Heliosphere-managed "
                        + "mod(s) among these will move despite their current protection status.");

                if (ImGui.Button("Yes, Restore"))
                {
                    RestoreSnapshot(snapshot.Id);
                    _pendingRestoreSnapshotId = null;
                    _pendingRestorePreview = null;
                    ImGui.CloseCurrentPopup();
                }
                ImGui.SameLine();
                if (ImGui.Button("Cancel"))
                {
                    _pendingRestoreSnapshotId = null;
                    _pendingRestorePreview = null;
                    ImGui.CloseCurrentPopup();
                }
                ImGui.EndPopup();
            }
        }

        if (operationState.Kind == Organizer.Operations.OperationType.Restore && !operationState.CanStartRestore && !operationState.RequiresRecovery)
            DrawOperationProgress(operationState, "Restoring", _plugin.RequestCancellation, "##cancel-restore");
        else if (operationState.Kind == Organizer.Operations.OperationType.Restore && operationState.RequiresRecovery)
            ImGui.TextColored(PluginTheme.CollisionBad, "Restore requires recovery - see the plugin log.");

        ImGui.Spacing();
        ImGui.Separator();
        var recentOperationsOpen = ImGui.CollapsingHeader("Recent Operations");
        // Reload only on a collapsed -> expanded transition (or the very first expansion), not every
        // frame the header stays open - the naive "load every frame it's expanded" version is exactly
        // the per-frame-disk-read pattern this section must avoid.
        if (recentOperationsOpen && !_recentOperationsSectionWasOpen)
            RefreshRecentOperations();
        _recentOperationsSectionWasOpen = recentOperationsOpen;

        if (recentOperationsOpen)
        {
            if (ImGui.Button("Refresh##recent-operations"))
                RefreshRecentOperations();

            if (_recentOperationsError is { } error)
            {
                ImGui.TextColored(PluginTheme.CollisionBad, error);
            }
            else if (_recentOperations.Count == 0)
            {
                ImGui.TextDisabled("No completed operations yet.");
            }
            else
            {
                using var table = ImRaii.Table("RecentOperationsTable", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV);
                if (table)
                {
                    ImGui.TableSetupColumn("When");
                    ImGui.TableSetupColumn("Type");
                    ImGui.TableSetupColumn("Stage");
                    ImGui.TableSetupColumn("Resolution");
                    ImGui.TableHeadersRow();
                    foreach (var journal in _recentOperations)
                    {
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(journal.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(journal.Type.ToString());
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(journal.Stage.ToString());
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(journal.Resolution.ToString());
                    }
                }
            }
        }
    }

    private void DrawSearchTab()
    {
        using var tab = ImRaii.TabItem("Search");
        if (!tab)
            return;

        ImGui.TextWrapped(
            "Since Penumbra 1.7, its own Mods tab supports a native search syntax (c:[item], t:[tag], "
            + "a:[author], etc.) that covers much of what this tab does. This tab stays available for "
            + "now - it may be retired later if Penumbra's own filtering fully supersedes it.");
        ImGui.Spacing();

        using (PluginTheme.PrimaryButton())
        {
            if (ImGui.Button("Build/Refresh Index"))
                _plugin.BuildChangedItemIndex();
        }

        if (_plugin.LibraryIndexError is { } error)
            ImGui.TextColored(PluginTheme.CollisionBad, error);

        if (_plugin.LibraryIndex is not { } index)
        {
            ImGui.TextUnformatted("Click Build/Refresh Index to search your mod library.");
            return;
        }

        ImGui.TextWrapped(ChangedItemIndexSummary.Describe(index));
        ImGui.Text($"Index built at {index.BuiltAt:HH:mm:ss}");
        ImGui.Spacing();

        ImGui.InputText("Mod name contains", ref _librarySearchNameQuery, 256);
        ImGui.InputText("Item contains", ref _librarySearchItemQuery, 256);
        ImGui.Spacing();

        ImGui.TextUnformatted("Categories:");
        var categoryToggles = SearchableCategories
            .Select(category => ($"{category}##search-category-{category}", _librarySearchCategories.Contains(category), (Action<bool>)(isChecked =>
            {
                if (isChecked)
                    _librarySearchCategories.Add(category);
                else
                    _librarySearchCategories.Remove(category);
            })))
            .Append(("Unknown##search-category-unknown", _librarySearchIncludeUnknown, isChecked => _librarySearchIncludeUnknown = isChecked))
            .ToList();
        DrawWrappingCheckboxRow(categoryToggles);

        if (_librarySearchCategories.Contains(ModCategory.Gear))
        {
            ImGui.Spacing();
            ImGui.TextUnformatted("Slots:");
            var slotToggles = Enum.GetValues<EquipmentSlot>()
                .Select(slot => ($"{SlotLabel(slot)}##search-slot-{slot}", _librarySearchSlots.Contains(slot), (Action<bool>)(isChecked =>
                {
                    if (isChecked)
                        _librarySearchSlots.Add(slot);
                    else
                        _librarySearchSlots.Remove(slot);
                })))
                .Append(("Unresolved##slot-unresolved", _librarySearchIncludeUnresolved, isChecked => _librarySearchIncludeUnresolved = isChecked))
                .ToList();
            DrawWrappingCheckboxRow(slotToggles);
        }

        ImGui.Spacing();

        var filter = new LibrarySearchFilter(
            _librarySearchCategories, _librarySearchIncludeUnknown,
            _librarySearchSlots, _librarySearchIncludeUnresolved,
            _librarySearchNameQuery, _librarySearchItemQuery);

        var matches = index.Mods.Where(mod => LibrarySearchEngine.Matches(mod, filter)).ToList();

        // Same flag combination as PathTreeView.cs (the only other table in this codebase) --
        // Resizable | SizingStretchProp, no per-column width flags, for proportional stretch.
        using var columns = ImRaii.Table("SearchColumns", 2,
            ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp, new Vector2(0, 420));
        if (!columns)
            return;

        ImGui.TableSetupColumn("Mods");
        ImGui.TableSetupColumn("Changed items");
        ImGui.TableNextRow();
        ImGui.TableNextColumn();

        using (var left = ImRaii.Child("SearchModList", new Vector2(0, 400), border: true))
        {
            if (left)
            {
                if (matches.Count == 0)
                {
                    ImGui.TextUnformatted("No mods found.");
                }
                else
                {
                    foreach (var mod in matches)
                    {
                        var isSelected = mod.Identifier == _librarySearchSelectedModIdentifier;
                        if (ImGui.Selectable($"{mod.Name} ({mod.Author})##search-{mod.Identifier}", isSelected))
                            _librarySearchSelectedModIdentifier = mod.Identifier;
                    }
                }
            }
        }

        ImGui.TableNextColumn();
        using (var right = ImRaii.Child("SearchItemList", new Vector2(0, 400), border: true))
        {
            if (right)
            {
                var selectedMod = matches.FirstOrDefault(m => m.Identifier == _librarySearchSelectedModIdentifier);
                if (selectedMod is null)
                {
                    ImGui.TextUnformatted("Select a mod to see its changed items.");
                }
                else
                {
                    var (items, matchedByNameOnly) = LibrarySearchEngine.DisplayedItems(selectedMod, filter);
                    if (matchedByNameOnly)
                        ImGui.TextColored(PluginTheme.CollisionBad, "Matched by mod name, not by item.");
                    foreach (var item in items)
                        ImGui.TextUnformatted(item.Key);
                }
            }
        }
    }

    private static string SlotLabel(EquipmentSlot slot) => slot switch
    {
        EquipmentSlot.Head => "Hats",
        EquipmentSlot.Top => "Tops",
        EquipmentSlot.Hands => "Hands",
        EquipmentSlot.Legs => "Bottoms",
        EquipmentSlot.Feet => "Feet",
        EquipmentSlot.Ears => "Earrings",
        EquipmentSlot.Neck => "Necklaces",
        EquipmentSlot.Wrists => "Bracelets",
        EquipmentSlot.Rings => "Rings",
        _ => slot.ToString(),
    };

    private void RestoreSnapshot(Guid snapshotId)
    {
        try
        {
            _plugin.StartRestoreOperation(snapshotId);
            _lastError = null;
            _historyCache = null; // the pre-operation snapshot was just appended
        }
        catch (Exception ex)
        {
            _lastError = $"Restore failed: {ex.Message}";
            Plugin.Log.Error(ex, "Restore failed.");
        }
    }

    private bool ContinueRecovery()
    {
        try
        {
            _plugin.ResolveContinue();
            _lastError = null;
            return true;
        }
        catch (Exception ex)
        {
            _lastError = $"Continue failed: {ex.Message}";
            Plugin.Log.Error(ex, "Continue failed.");
            return false;
        }
    }

    private bool RestorePreviousState()
    {
        try
        {
            _plugin.ResolveRestorePreviousState();
            _lastError = null;
            return true;
        }
        catch (Exception ex)
        {
            _lastError = $"Restore Previous State failed: {ex.Message}";
            Plugin.Log.Error(ex, "Restore Previous State failed.");
            return false;
        }
    }

    private bool ResolveOneMultiRoot(Guid operationId)
    {
        try
        {
            _plugin.ResolveOneMultiRootOperation(operationId);
            _lastError = null;
            return true;
        }
        catch (Exception ex)
        {
            _lastError = $"Keep Current State failed: {ex.Message}";
            Plugin.Log.Error(ex, "Keep Current State (multi-root) failed.");
            return false;
        }
    }

    private void CreateBackup(string? label)
    {
        try
        {
            _plugin.CreateBackup(label);
            _lastError = null;
            Plugin.Log.Information($"Manual backup created{(label is { Length: > 0 } ? $" ({label})" : "")}.");
        }
        catch (Exception ex)
        {
            _lastError = $"Create backup failed: {ex.Message}";
            Plugin.Log.Error(ex, "Create backup failed.");
        }

        _historyCache = null;
    }

    private void DeleteHistorySnapshot(Guid id)
    {
        try
        {
            _plugin.DeleteHistorySnapshot(id);
            _lastError = null;
            Plugin.Log.Information($"History snapshot deleted: {id}.");
        }
        catch (Exception ex)
        {
            _lastError = $"Delete backup failed: {ex.Message}";
            Plugin.Log.Error(ex, "Delete backup failed.");
        }

        _historyCache = null;
    }

    private void DrawOrphanedFoldersSection()
    {
        var detection = _orphanedFolders;
        var operationState = _plugin.OperationController.State;
        if (detection is null || detection.Status == Organizer.FolderDetectionStatus.NotScanned)
            return; // nothing meaningful before the first scan

        if (detection.Status is Organizer.FolderDetectionStatus.UnsupportedVersion
            or Organizer.FolderDetectionStatus.MalformedJson)
        {
            TextColoredWrapped(ImGuiColors.DalamudYellow,
                "organization.json couldn't be read — folder cleanup unavailable "
                + (detection.Status == Organizer.FolderDetectionStatus.UnsupportedVersion
                    ? "(unsupported version)."
                    : "(unreadable file)."));
            return;
        }

        if (detection.Status == Organizer.FolderDetectionStatus.FileMissing)
        {
            // Ordinary state, but say so instead of hiding the whole section — a fresh Penumbra
            // install has no organization.json until its tree first gains folders, and an
            // invisible section reads as "the feature is gone" (real user report, 2026-07-19).
            ImGui.TextUnformatted("Orphaned Folders");
            TextDisabledWrapped(
                "Penumbra hasn't created organization.json yet — it appears once the mod tree has "
                + "folders (e.g. after your first Apply). Folder cleanup will activate then.");
            return;
        }

        var total = detection.PlainEmpty.Count + detection.CustomizedEmpty.Count;

        if (_folderReloadRequired)
            TextColoredWrapped(ImGuiColors.DalamudYellow,
                "Waiting on Rediscover Mods — the list below reflects organization.json on disk, "
                + "not Penumbra's confirmed live state. Click Rediscover Mods in Penumbra, then Scan here, to re-check.");

        ImGui.TextUnformatted($"Orphaned Folders ({total} detected)");

        if (ImGui.Button("Re-read organization.json##orphan-reread"))
            RefreshOrphanedFolders();

        ImGui.SameLine();
        ImGui.TextDisabled(_organizationJsonLastReadAt is { } readAt
            ? $"Last read: {readAt.ToLocalTime():HH:mm:ss} ({FormatElapsed(DateTimeOffset.Now - readAt)} ago)"
            : "Not yet read this session.");

        TextDisabledWrapped(
            "This reflects organization.json on disk as of the last read above, not Penumbra's live folder tree. "
            + "If Penumbra hasn't written a change to disk yet, re-reading won't show it — move a folder in "
            + "Penumbra's own UI (or use Rediscover Mods) to make Penumbra flush its tree, then re-read again.");

        if (total > 0)
        {
            ImGui.TextUnformatted($"Empty, no customization ({detection.PlainEmpty.Count}) — pre-checked");
            foreach (var path in detection.PlainEmpty)
                DrawOrphanCheckbox(path, path);

            if (detection.CustomizedEmpty.Count > 0)
            {
                ImGui.TextColored(ImGuiColors.DalamudYellow,
                    $"Empty but customized ({detection.CustomizedEmpty.Count}) — unchecked, review before pruning");
                foreach (var folder in detection.CustomizedEmpty)
                    DrawOrphanCheckbox(folder.Path, $"{folder.Path}  ({folder.Description})");
            }

            ImGui.Spacing();
            ImGui.BeginDisabled(_selectedOrphans.Count == 0 || !operationState.CanRunFolderCleanup);
            var cleanClicked = ImGui.Button("Clean Up Selected Folders");
            ImGui.EndDisabled();
            // Gated on _selectedOrphans.Count > 0 so this tooltip only claims the reason is "another
            // operation" when that's actually why the button is disabled - with no selection at all,
            // the button is disabled for an unrelated, pre-existing reason (nothing chosen yet), and
            // this tooltip must not claim an operation is blocking it when none is.
            if (_selectedOrphans.Count > 0 && !operationState.CanRunFolderCleanup && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Another operation is in progress or requires recovery.");
            if (cleanClicked)
                ImGui.OpenPopup("Clean up folders?");

            ImGui.SetNextWindowSize(new Vector2(DetailedPopupWidth, 0), ImGuiCond.Appearing);
            if (ImGui.BeginPopupModal("Clean up folders?"))
            {
                ImGui.TextUnformatted($"Remove {_selectedOrphans.Count} folder entries from Penumbra's organization.json?");
                // Height-capped and scrollable, not an unbounded list of TextUnformatted lines -
                // a real library has hit 229 orphaned folders in one run (see docs/HANDOFF_FOLDER_CLEANUP.md),
                // which would otherwise stretch this popup far past the screen's own height.
                using (var list = ImRaii.Child("CleanUpFoldersList", new Vector2(0, 300), border: true))
                {
                    if (list)
                        foreach (var path in _selectedOrphans.OrderBy(p => p, StringComparer.Ordinal))
                            ImGui.TextWrapped(path);
                }
                if (ImGui.Button("Yes, Clean Up"))
                {
                    CleanUpSelectedFolders();
                    ImGui.CloseCurrentPopup();
                }
                ImGui.SameLine();
                if (ImGui.Button("Cancel"))
                    ImGui.CloseCurrentPopup();
                ImGui.EndPopup();
            }
        }

        if (_plugin.FolderBackupExists)
        {
            // Deliberately its own line, not SameLine'd - whatever was drawn immediately above
            // (the wrapped explanatory paragraph, or nothing if total == 0) can end at an
            // unpredictable X position depending on window width and how many lines it wrapped
            // to, which could otherwise push this button most of the way off the window edge.
            ImGui.BeginDisabled(!operationState.CanRunFolderCleanupRollback);
            if (ImGui.Button("Rollback Folder Cleanup"))
                RollbackFolderCleanup();
            ImGui.EndDisabled();
            if (!operationState.CanRunFolderCleanupRollback && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Another operation is in progress or requires recovery.");
        }

        DrawFolderActionResults();
    }

    private void DrawOrphanCheckbox(string path, string label)
    {
        var selected = _selectedOrphans.Contains(path);
        if (ImGui.Checkbox($"{label}##orphan-{path}", ref selected))
        {
            if (selected)
                _selectedOrphans.Add(path);
            else
                _selectedOrphans.Remove(path);
        }
    }

    private void DrawFolderActionResults()
    {
        if (_lastCleanupResult is not null)
        {
            var r = _lastCleanupResult;
            switch (r.Status)
            {
                case Organizer.FolderCleanupStatus.Success:
                    ImGui.TextUnformatted($"{r.Pruned.Count} folder entries removed from organization.json.");
                    TextColoredWrapped(ImGuiColors.DalamudYellow,
                        "Penumbra hasn't loaded this change yet — open Penumbra's Settings tab and click "
                        + "Rediscover Mods before making any other folder changes there.");
                    if (r.SkippedStale.Count > 0)
                        ImGui.TextWrapped(
                            $"{r.SkippedStale.Count} selected folder(s) were no longer orphaned and were skipped.");
                    break;
                case Organizer.FolderCleanupStatus.SucceededBackupFailed:
                    TextColoredWrapped(PluginTheme.CollisionBad,
                        $"{r.Pruned.Count} folder entries removed, but the rollback backup could not be saved. "
                        + "Rediscover Mods in Penumbra now, then avoid running another cleanup until you've "
                        + "confirmed the result — there is no safety net for this action right now.");
                    if (_plugin.FolderBackupExists)
                        TextColoredWrapped(PluginTheme.CollisionBad,
                            "The Rollback button restores an OLDER backup that predates this cleanup — "
                            + "clicking it would undo more than just this action.");
                    break;
                case Organizer.FolderCleanupStatus.NothingStillValid:
                    ImGui.TextWrapped(
                        "Nothing was cleaned up — the selected folder(s) are no longer orphaned (or no longer exist). "
                        + "No files were changed.");
                    break;
                case Organizer.FolderCleanupStatus.NothingSelected:
                    ImGui.TextUnformatted("Nothing selected — no files were changed.");
                    break;
                case Organizer.FolderCleanupStatus.FileMissing:
                    ImGui.TextWrapped("organization.json does not exist on this install — nothing to clean up.");
                    break;
                case Organizer.FolderCleanupStatus.UnsupportedVersion:
                case Organizer.FolderCleanupStatus.MalformedJson:
                    ImGui.TextColored(ImGuiColors.DalamudYellow,
                        "organization.json couldn't be read — no files were changed.");
                    break;
                case Organizer.FolderCleanupStatus.FileChangedDuringCleanup:
                    TextColoredWrapped(ImGuiColors.DalamudYellow,
                        "organization.json changed (likely Penumbra itself) while this cleanup was running — "
                        + "no files were touched. Scan and try again.");
                    break;
            }
        }

        if (_lastFolderRollbackResult is not null)
        {
            switch (_lastFolderRollbackResult.Status)
            {
                case Organizer.FolderRollbackStatus.Restored:
                    ImGui.TextUnformatted("Backup restored to organization.json.");
                    ImGui.TextColored(ImGuiColors.DalamudYellow,
                        "Penumbra hasn't loaded this change yet — click Rediscover Mods.");
                    break;
                case Organizer.FolderRollbackStatus.InvalidBackup:
                    TextColoredWrapped(PluginTheme.CollisionBad,
                        "The backup file is unreadable or unsupported — rollback aborted, organization.json was not touched.");
                    break;
                case Organizer.FolderRollbackStatus.NoBackup:
                    ImGui.TextUnformatted("No folder-cleanup backup exists.");
                    break;
            }
        }
    }

    private void SaveProtectionStateSafely()
    {
        try
        {
            _plugin.SaveProtectionState();
            _lastError = null;
        }
        catch (Exception ex)
        {
            _lastError = $"Failed to save protection settings: {ex.Message}";
            Plugin.Log.Error(ex, "Failed to save protection settings.");
        }
    }

    private void RunScan()
    {
        try
        {
            _plugin.RunScan();
            _lastError = null;
            _folderReloadRequired = false; // the banner's instruction is "Rediscover Mods, then Scan here"
            Plugin.Log.Information($"Scan completed: {_plugin.OrganizerState.Mods.Count} mods loaded.");
        }
        catch (Exception ex)
        {
            _lastError = $"Failed to reach Penumbra IPC: {ex.Message}";
            Plugin.Log.Error(ex, "Scan failed.");
        }

        RefreshOrphanedFolders();
    }

    private void ApplyChanges()
    {
        try
        {
            _plugin.StartApplyOperation();
            _lastError = null;
            _historyCache = null; // the pre-operation snapshot was just appended
        }
        catch (Exception ex)
        {
            _lastError = $"Apply failed: {ex.Message}";
            Plugin.Log.Error(ex, "Apply failed.");
        }
    }

    private void OpenConfigFile() => OpenContainingFolder(Plugin.PluginInterface.ConfigFile.FullName);

    // Opens Explorer with the file pre-selected rather than launching it with its default app -
    // these two files exist for attaching to a bug report, so showing where they are (and letting
    // the tester copy/zip/inspect from Explorer) is more useful than opening them directly.
    private void OpenContainingFolder(string filePath)
    {
        try
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{filePath}\"");
        }
        catch (Exception ex)
        {
            _lastError = $"Could not open folder for '{filePath}': {ex.Message}";
        }
    }

    // Non-invasive by design: no absolute filesystem paths (Penumbra's config dir and the mod
    // storage root both live under the user's Windows profile folder and would leak the local
    // account name), and no full mod name/author/path list (that's what the separate Export
    // button already produces) - this is a summary of what happened this session, meant to
    // complement Export, not duplicate it.
    private string CreateDiagnosticDump()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Penumbra Organizer diagnostic dump");
        sb.AppendLine($"Generated: {DateTimeOffset.Now:u}");
        sb.AppendLine($"Plugin version: {PluginVersion}");
        sb.AppendLine();

        sb.AppendLine("== Last error ==");
        sb.AppendLine(_lastError ?? "(none)");
        sb.AppendLine();

        sb.AppendLine("== Organizer state ==");
        var mods = _plugin.OrganizerState.Mods;
        sb.AppendLine($"Mods scanned: {mods.Count}");
        sb.AppendLine($"Protected: {mods.Count(m => m.Protected)}");
        sb.AppendLine($"Heliosphere-managed: {mods.Count(m => m.HeliosphereManaged)}");
        var validation = _plugin.OrganizerState.Validate();
        sb.AppendLine(
            $"Validation: {validation.ProtectedViolations.Count} protected violations, {validation.PathCollisions.Count} path collisions");
        sb.AppendLine();

        sb.AppendLine("== Last Apply result ==");
        // No session-results argument: the field that used to carry it was never assigned once the
        // async operation engine replaced the synchronous Apply path, so this section has been
        // driven entirely by the persisted Config.LastApply summary since then. Reconstructing a
        // live per-mod result list from the operation bundle is Plan E territory, not this cleanup's.
        sb.AppendLine(Organizer.DiagnosticSummaryFormatter.FormatApplySection(null, _plugin.Config.LastApply));
        sb.AppendLine();

        sb.AppendLine("== Last Restore result ==");
        sb.AppendLine(Organizer.DiagnosticSummaryFormatter.FormatRestoreSection(null, _plugin.Config.LastRestore));
        sb.AppendLine();

        sb.AppendLine("== Last Folder Cleanup result ==");
        sb.AppendLine(Organizer.DiagnosticSummaryFormatter.FormatFolderCleanupSection(_lastCleanupResult, _plugin.Config.LastFolderCleanup));
        sb.AppendLine();

        sb.AppendLine("== Last Folder Cleanup Rollback result ==");
        sb.AppendLine(Organizer.DiagnosticSummaryFormatter.FormatFolderCleanupRollbackSection(
            _lastFolderRollbackResult, _plugin.Config.LastFolderCleanupRollback));
        sb.AppendLine();

        sb.AppendLine("== Orphaned folder detection ==");
        sb.AppendLine(_orphanedFolders is null
            ? "(not run this session)"
            : $"Status={_orphanedFolders.Status}, Plain={_orphanedFolders.PlainEmpty.Count}, Customized={_orphanedFolders.CustomizedEmpty.Count}");
        sb.AppendLine();

        sb.AppendLine("== Rollback history ==");
        var history = _historyCache ??= _plugin.LoadHistory();
        sb.AppendLine($"Snapshot count: {history.Count}");
        foreach (var snapshot in history.OrderByDescending(s => s.CreatedAt))
        {
            var label = snapshot.Label is { Length: > 0 } l ? l : snapshot.AutoDescription;
            sb.AppendLine($"  {snapshot.CreatedAt.ToLocalTime():u} - {label} ({snapshot.ModPaths.Count} mods)");
        }
        sb.AppendLine();

        sb.AppendLine("== Interrupted operation ==");
        try
        {
            var pendingJournal = _plugin.OperationController.GetPendingRecoveryJournal();
            if (pendingJournal is not null)
            {
                sb.AppendLine($"  OperationId={pendingJournal.OperationId}, Type={pendingJournal.Type}, Stage={pendingJournal.Stage}, {pendingJournal.ProcessedStepCount}/{pendingJournal.TotalSteps} steps, UpdatedAt={pendingJournal.UpdatedAt.ToLocalTime():u}");
            }
            else
            {
                var blocked = _plugin.OperationController.GetBlockedOperations();
                if (blocked.Count == 0)
                {
                    sb.AppendLine("(none)");
                }
                else
                {
                    foreach (var (_, journal) in blocked)
                        sb.AppendLine($"  OperationId={journal.OperationId}, Type={journal.Type}, Stage={journal.Stage}, UpdatedAt={journal.UpdatedAt.ToLocalTime():u}");
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"(failed to read: {ex.Message})");
            Plugin.Log.Warning(ex, "Diagnostic dump: reading interrupted operation state failed.");
        }
        sb.AppendLine();

        sb.AppendLine("== Recent operations ==");
        try
        {
            var recentOperations = Organizer.Operations.OperationBundleDiscovery.LoadRecentCompletedJournals(_plugin.OperationsRoot, take: RecentOperationsCount);
            if (recentOperations.Count == 0)
            {
                sb.AppendLine("(none)");
            }
            else
            {
                foreach (var journal in recentOperations)
                    sb.AppendLine($"  {journal.UpdatedAt.ToLocalTime():u} - {journal.Type} - {journal.Stage} - {journal.Resolution}");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"(failed to read: {ex.Message})");
            Plugin.Log.Warning(ex, "Diagnostic dump: reading recent operations failed.");
        }
        sb.AppendLine();

        sb.AppendLine("== Slow calls ==");
        try
        {
            var diagnosticsLogPath = Organizer.Operations.OperationBundlePaths.DiagnosticsLogPath(_plugin.OperationsRoot);
            var slowCalls = Organizer.Operations.DiagnosticsLog.ReadAll(diagnosticsLogPath)
                .Where(e => e.Kind == Organizer.Operations.DiagnosticEventKind.SlowCall)
                .ToList();
            if (slowCalls.Count == 0)
            {
                sb.AppendLine("(none)");
            }
            else
            {
                // Grouped by identifier, not just the five longest raw events - five slow calls to the
                // same identifier would otherwise crowd out four other identifiers that are each slow
                // exactly once. Ranked by worst (max) duration per identifier.
                var grouped = slowCalls
                    .GroupBy(e => e.Identifier, StringComparer.Ordinal)
                    .Select(g => new { Identifier = g.Key, Count = g.Count(), WorstMs = g.Max(e => e.DurationMilliseconds), TotalMs = g.Sum(e => e.DurationMilliseconds) })
                    .OrderByDescending(x => x.WorstMs)
                    .ThenByDescending(x => x.Count)
                    .Take(5)
                    .ToList();
                sb.AppendLine($"{slowCalls.Count} recorded slow calls across {grouped.Count} displayed identifiers (ranked by worst duration):");
                foreach (var item in grouped)
                    sb.AppendLine($"  {item.Identifier}: {item.Count} calls, worst {item.WorstMs}ms, total {item.TotalMs}ms");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"(failed to read: {ex.Message})");
            Plugin.Log.Warning(ex, "Diagnostic dump: reading slow-call log failed.");
        }
        sb.AppendLine();

        sb.AppendLine("== Session event log (most recent first) ==");
        foreach (var line in _eventLog)
            sb.AppendLine(line);

        var path = Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "organizer-diagnostics.txt");
        Directory.CreateDirectory(Plugin.PluginInterface.ConfigDirectory.FullName);
        File.WriteAllText(path, sb.ToString());
        return path;
    }

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
            },
            Path.GetDirectoryName(_plugin.DefaultWorkbookFilePath));
    }

    private void OpenFileWithDefaultApp(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _lastError = $"Could not open '{path}': {ex.Message}";
        }
    }

    private void ImportWorkbook(string workbookPath)
    {
        try
        {
            var result = _plugin.ImportWorkbook(workbookPath);
            _lastWorkbookImportResult = new Organizer.WorkbookImportResultView(result.Summary, result.Errors, result.Warnings);
            _lastError = null;
        }
        catch (Exception ex)
        {
            _lastError = $"Workbook import failed: {ex.Message}";
        }
    }

    private async Task RefreshNpcNamesAsync()
    {
        try
        {
            _npcRefreshResult = await _plugin.RefreshNpcNamesAsync(CancellationToken.None);
            _lastError = null;
        }
        catch (Exception ex)
        {
            _lastError = $"NPC list refresh failed: {ex.Message}";
        }
    }

    // Detection reads a file and parses JSON — never callable from a draw method directly
    // (DrawReviewTab runs every frame). Recomputed only on explicit triggers; selection resets
    // to defaults on every recompute: a refresh means the world changed, and a stale selection
    // surviving it is the failure mode the write-time re-verification exists to catch.
    private void RefreshOrphanedFolders()
    {
        try
        {
            _orphanedFolders = _plugin.DetectOrphanedFolders();
            _organizationJsonLastReadAt = DateTimeOffset.Now;
            _selectedOrphans.Clear();
            if (_orphanedFolders.Status == Organizer.FolderDetectionStatus.Detected)
                foreach (var path in _orphanedFolders.PlainEmpty)
                    _selectedOrphans.Add(path);
        }
        catch (Exception ex)
        {
            _lastError = $"Orphaned-folder detection failed: {ex.Message}";
        }
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero; // defends against a backward system-clock adjustment

        if (elapsed < TimeSpan.FromMinutes(1))
            return $"{(int)elapsed.TotalSeconds}s";
        if (elapsed < TimeSpan.FromHours(1))
            return $"{(int)elapsed.TotalMinutes}m";
        if (elapsed < TimeSpan.FromDays(1))
            return $"{(int)elapsed.TotalHours}h";

        return $"{(int)elapsed.TotalDays}d";
    }

    private void CleanUpSelectedFolders()
    {
        try
        {
            _lastCleanupResult = _plugin.CleanUpFolders(_selectedOrphans.ToHashSet(StringComparer.Ordinal));
            _lastError = null;
            if (_lastCleanupResult.Status is Organizer.FolderCleanupStatus.Success
                or Organizer.FolderCleanupStatus.SucceededBackupFailed)
                _folderReloadRequired = true;
            Plugin.Log.Information(
                $"Folder cleanup: status={_lastCleanupResult.Status}, pruned={_lastCleanupResult.Pruned.Count}, skippedStale={_lastCleanupResult.SkippedStale.Count}.");
        }
        catch (Exception ex)
        {
            _lastError = $"Folder cleanup failed: {ex.Message}";
            Plugin.Log.Error(ex, "Folder cleanup failed.");
        }

        RefreshOrphanedFolders();
    }

    private void RollbackFolderCleanup()
    {
        try
        {
            _lastFolderRollbackResult = _plugin.RollbackFolderCleanup();
            _lastError = null;
            if (_lastFolderRollbackResult.Status == Organizer.FolderRollbackStatus.Restored)
                _folderReloadRequired = true;
            Plugin.Log.Information($"Folder cleanup rollback: status={_lastFolderRollbackResult.Status}.");
        }
        catch (Exception ex)
        {
            _lastError = $"Folder cleanup rollback failed: {ex.Message}";
            Plugin.Log.Error(ex, "Folder cleanup rollback failed.");
        }

        RefreshOrphanedFolders();
    }
}
