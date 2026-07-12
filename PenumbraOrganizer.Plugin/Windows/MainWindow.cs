using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Penumbra.Api.Enums;

namespace PenumbraOrganizer.Plugin.Windows;

public sealed class MainWindow : Window, IDisposable
{
    private const int MaxEventLogLines = 200;

    private readonly Plugin _plugin;
    private readonly List<string> _eventLog = [];

    private Dictionary<string, string> _mods = [];
    private readonly Dictionary<string, string> _resolvedPaths = [];
    private string? _lastError;

    public MainWindow(Plugin plugin)
        : base("Penumbra Organizer (MVP)###PenumbraOrganizerPluginMain")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(520, 420),
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
        ImGui.TextWrapped(
            "Read-only spike: lists mods and current Penumbra virtual paths via IPC. No write calls are made.");
        ImGui.Spacing();

        if (ImGui.Button("Refresh mod list"))
            RefreshMods();

        ImGui.SameLine();
        ImGui.Text($"{_mods.Count} mods loaded");

        if (_lastError != null)
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), _lastError);

        ImGui.Spacing();

        if (ImGui.BeginTable("ModTable", 3,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY,
                new Vector2(0, 220)))
        {
            ImGui.TableSetupColumn("Directory");
            ImGui.TableSetupColumn("Name");
            ImGui.TableSetupColumn("Current Penumbra Path");
            ImGui.TableHeadersRow();

            foreach (var (dir, name) in _mods)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(dir);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(name);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(_resolvedPaths.GetValueOrDefault(dir, "(unresolved)"));
            }

            ImGui.EndTable();
        }

        ImGui.Spacing();
        ImGui.Text("Live events (ModAdded / ModDeleted / ModMoved):");
        if (ImGui.BeginChild("EventLog", new Vector2(0, 0), true))
        {
            foreach (var line in _eventLog)
                ImGui.TextUnformatted(line);
        }

        ImGui.EndChild();
    }

    private void RefreshMods()
    {
        try
        {
            _mods = _plugin.GetModListIpc.Invoke();
            _resolvedPaths.Clear();

            foreach (var dir in _mods.Keys)
            {
                var (ec, path, _, _) = _plugin.GetModPathIpc.Invoke(dir);
                _resolvedPaths[dir] = ec == PenumbraApiEc.Success ? path : $"(error: {ec})";
            }

            _lastError = null;
        }
        catch (Exception ex)
        {
            _lastError = $"Failed to reach Penumbra IPC: {ex.Message}";
        }
    }
}
