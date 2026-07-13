using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using PenumbraOrganizer.Core.Services;

namespace PenumbraOrganizer.Plugin.Windows;

public sealed class MainWindow : Window, IDisposable
{
    private const int MaxEventLogLines = 200;

    private readonly Plugin _plugin;
    private readonly CreatorCanonicalizer _creatorCanonicalizer = new();
    private readonly List<string> _eventLog = [];
    private string? _lastError;
    private string _manualFolderInput = string.Empty;
    private string? _selectedManualModIdentifier;

    public MainWindow(Plugin plugin)
        : base("Penumbra Organizer###PenumbraOrganizerPluginMain")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(640, 480),
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
        if (ImGui.Button("SPIKE: Dump changed items (temporary, Phase 1c data gathering)"))
        {
            try
            {
                var path = _plugin.DumpChangedItemsSpike();
                LogEvent($"Changed-items spike dump written to: {path}");
                _lastError = null;
            }
            catch (Exception ex)
            {
                _lastError = $"Spike dump failed: {ex.Message}";
            }
        }

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

        if (ImGui.Button("Toggle Heliosphere protection"))
        {
            var allProtected = _plugin.OrganizerState.Mods
                .Where(m => m.HeliosphereManaged)
                .All(m => m.Protected);
            _plugin.OrganizerState.SetHeliosphereProtection(!allProtected);
            _plugin.SaveProtectionState();
        }

        ImGui.Spacing();

        foreach (var mod in _plugin.OrganizerState.Mods)
        {
            var isProtected = mod.Protected;
            if (ImGui.Checkbox($"{mod.Name}##protect-{mod.Identifier}", ref isProtected))
            {
                _plugin.OrganizerState.SetProtected(mod.Identifier, isProtected);
                _plugin.SaveProtectionState();
            }
        }
    }

    private void DrawSortTab()
    {
        using var tab = ImRaii.TabItem("Sort");
        if (!tab)
            return;

        if (ImGui.Button("By Creator"))
            _plugin.OrganizerState.SortByCreator(_creatorCanonicalizer.Canonicalize);

        ImGui.Spacing();
        ImGui.TextUnformatted("Start Manually: pick a mod below, type a folder, click Assign.");

        ImGui.InputText("Destination folder", ref _manualFolderInput, 256);

        ImGui.SameLine();
        if (ImGui.Button("Assign") && _selectedManualModIdentifier is not null && _manualFolderInput.Length > 0)
        {
            var mod = _plugin.OrganizerState.Mods.FirstOrDefault(m => m.Identifier == _selectedManualModIdentifier);
            if (mod is not null)
                _plugin.OrganizerState.AssignManual(_selectedManualModIdentifier, $"{_manualFolderInput}/{mod.Name}");
        }

        ImGui.Spacing();
        using (var child = ImRaii.Child("ManualModList", new Vector2(0, 300), border: true))
        {
            if (child)
                foreach (var mod in _plugin.OrganizerState.Mods.Where(m => !m.Protected))
                {
                    if (ImGui.RadioButton(mod.Name, _selectedManualModIdentifier == mod.Identifier))
                        _selectedManualModIdentifier = mod.Identifier;
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
            ImGui.TextColored(ImGuiColors.HealerGreen, "No issues found.");

        foreach (var identifier in result.ProtectedViolations)
            ImGui.TextColored(ImGuiColors.DalamudRed, $"Protected mod changed: {identifier}");

        foreach (var (path, identifiers) in result.PathCollisions)
            ImGui.TextColored(ImGuiColors.DalamudRed, $"Collision at '{path}': {string.Join(", ", identifiers)}");

        ImGui.Spacing();
        PathTreeView.Draw(_plugin.OrganizerState.Mods, showProposedColumn: true);

        ImGui.Spacing();
        ImGui.BeginDisabled();
        ImGui.Button("Apply (disabled in Phase 1)");
        ImGui.EndDisabled();
    }

    private void RunScan()
    {
        try
        {
            _plugin.RunScan();
            _lastError = null;
        }
        catch (Exception ex)
        {
            _lastError = $"Failed to reach Penumbra IPC: {ex.Message}";
        }
    }
}
