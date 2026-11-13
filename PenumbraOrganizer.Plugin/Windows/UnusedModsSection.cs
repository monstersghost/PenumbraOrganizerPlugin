using System.Collections.Concurrent;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Collections;
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Windows;

/// <summary>
/// The Scan tab's collapsed "Unused mods" section: mods no collection enables, their size on disk,
/// and a staged move to the Unused folder. Nothing moves until the user applies on Review.
/// </summary>
public sealed class UnusedModsSection : IDisposable
{
    private const string ActivityGateReason = "Another operation is in progress or requires recovery.";

    private IReadOnlyList<UnusedModEntry> _entries = [];
    private bool _checked;
    private string? _collectionsLine;
    private string? _message;
    private bool _onlyActiveCollection;
    private bool _protectFolder = true;
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _sizes = new(StringComparer.Ordinal);
    private CancellationTokenSource? _sizeCts;
    private int _scanGenerationSeen = -1;

    public void Dispose() => CancelSizes();

    private void CancelSizes()
    {
        _sizeCts?.Cancel();
        _sizeCts?.Dispose();
        _sizeCts = null;
    }

    // A published scan replaces every row, so the old report and its sizes describe a library that
    // is gone. Dropped here rather than left to mislead.
    private void ResetIfScanChanged(OrganizerState state)
    {
        if (_scanGenerationSeen == state.ScanGeneration)
            return;

        _scanGenerationSeen = state.ScanGeneration;
        CancelSizes();
        _entries = [];
        _checked = false;
        _collectionsLine = null;
        _message = null;
        _selected.Clear();
        _sizes.Clear();
    }

    public void Draw(
        OrganizerState state,
        ActivityGates gates,
        Configuration config,
        Action saveProtection,
        Func<CollectionUsageSet?> readCollections,
        Func<CollectionRead?> readActiveCollection,
        Func<string?> modDirectory)
    {
        ResetIfScanChanged(state);

        var label = _checked ? $"Unused mods ({_entries.Count})###scan-unused" : "Unused mods###scan-unused";
        var open = ImGui.CollapsingHeader(label);
        Help.Tooltip(HelpTopics.UnusedCheck);
        if (!open)
            return;

        ImGui.Checkbox("Only my active collection##unused-active", ref _onlyActiveCollection);
        ImGui.SameLine();
        if (ImGui.Button("Check unused mods##unused-check"))
            Check(state, readCollections, readActiveCollection, modDirectory);
        Help.Tooltip(HelpTopics.UnusedCheck);

        if (_collectionsLine is not null)
            ImGui.TextDisabled(_collectionsLine);
        if (_message is not null)
            ImGui.TextWrapped(_message);
        if (!_checked)
            return;

        if (_entries.Count == 0)
        {
            ImGui.TextDisabled("Every mod is enabled in at least one collection.");
            return;
        }

        if (ImGui.SmallButton("Select all##unused-all"))
            foreach (var entry in _entries.Where(e => e.WillMove))
                _selected.Add(entry.Identifier);
        ImGui.SameLine();
        if (ImGui.SmallButton("Select none##unused-none"))
            _selected.Clear();

        using (var child = ImRaii.Child("UnusedModList", new Vector2(0, 220), border: true))
        {
            if (child)
            {
                foreach (var entry in _entries)
                {
                    ImGui.PushID(entry.Identifier);
                    var ticked = _selected.Contains(entry.Identifier);
                    ImGui.BeginDisabled(!entry.WillMove);
                    if (ImGui.Checkbox("##pick", ref ticked))
                    {
                        if (ticked)
                            _selected.Add(entry.Identifier);
                        else
                            _selected.Remove(entry.Identifier);
                    }
                    ImGui.EndDisabled();
                    ImGui.SameLine();
                    var size = _sizes.TryGetValue(entry.Identifier, out var bytes) ? ByteSize.Format(bytes) : "...";
                    var protectedNote = entry.WillMove ? string.Empty : $"  ({UnusedModsReport.ProtectedNote})";
                    ImGui.TextUnformatted($"{entry.Name}  [{entry.CurrentPath}]  {size}{protectedNote}");
                    ImGui.PopID();
                }
            }
        }

        ImGui.BeginDisabled(!gates.CanStageProposals || _selected.Count == 0);
        var moveClicked = ImGui.Button($"Move {_selected.Count} selected to {config.UnusedFolderName}##unused-move");
        ImGui.EndDisabled();
        Help.Tooltip(HelpTopics.UnusedMove, gates.CanStageProposals ? null : ActivityGateReason);
        ImGui.SameLine();
        ImGui.Checkbox($"Protect the {config.UnusedFolderName} folder##unused-protect", ref _protectFolder);
        Help.Tooltip(HelpTopics.UnusedProtectFolder);

        if (moveClicked)
        {
            var staged = state.StageMoveUnder(_selected, config.UnusedFolderName);
            if (_protectFolder)
            {
                state.SetFolderProtectionRule(config.UnusedFolderName, FolderProtectionRule.Protected);
                saveProtection();
            }

            _message = $"Staged {staged} mods under {config.UnusedFolderName}. Open Review Changes to check and apply.";
        }
    }

    private void Check(
        OrganizerState state,
        Func<CollectionUsageSet?> readCollections,
        Func<CollectionRead?> readActiveCollection,
        Func<string?> modDirectory)
    {
        CancelSizes();
        _selected.Clear();
        _sizes.Clear();
        _message = null;

        IReadOnlySet<string> used;
        if (_onlyActiveCollection)
        {
            var read = readActiveCollection();
            if (read is null)
            {
                _checked = false;
                _collectionsLine = null;
                _message = "Could not read your active collection. Is Penumbra running?";
                return;
            }

            used = read.EnabledModDirectories;
            _collectionsLine = $"Checked 1 collection: {read.Name}. Temporary settings from other plugins are ignored.";
        }
        else
        {
            var set = readCollections();
            if (set is null)
            {
                _checked = false;
                _collectionsLine = null;
                _message = "Could not read Penumbra's collections. Is Penumbra running?";
                return;
            }

            used = set.UsedDirectories();
            var names = string.Join(", ", set.Collections.Select(c => c.Name));
            _collectionsLine = $"Checked {set.Collections.Count} collections: {names}. "
                + "Temporary settings from other plugins are ignored.";
            if (set.Unreadable.Count > 0)
                _message = $"Could not read: {string.Join(", ", set.Unreadable)}. "
                    + "Mods those collections enable may be listed as unused.";
        }

        _entries = UnusedModsReport.Build(state.Mods, used);
        _checked = true;
        StartSizes(modDirectory());
    }

    // Read-only disk walk, off the framework thread. Cancelled when a scan publishes or on unload.
    private void StartSizes(string? root)
    {
        if (string.IsNullOrEmpty(root) || _entries.Count == 0)
            return;

        var cts = new CancellationTokenSource();
        _sizeCts = cts;
        var identifiers = _entries.Select(e => e.Identifier).ToList();
        var sizes = _sizes;
        _ = Task.Run(() =>
        {
            foreach (var identifier in identifiers)
            {
                if (cts.Token.IsCancellationRequested)
                    return;

                var bytes = ModFolderSize.Measure(Path.Combine(root, identifier), cts.Token);
                if (bytes is { } value)
                    sizes[identifier] = value;
            }
        }, cts.Token);
    }
}
