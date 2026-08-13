using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Templates;

namespace PenumbraOrganizer.Plugin.Windows;

/// <summary>
/// The export half of the Templates tab: a review-and-trim screen the author passes through before
/// anything leaves the machine.
/// </summary>
/// <remarks>
/// This screen is a safety mechanism, not presentation. Exporting a name-to-folder map publishes the
/// author's entire mod list, which for this plugin's user base routinely includes content they would
/// not choose to broadcast. There is deliberately no "quick export" path: the only two emit controls
/// in the plugin live at the bottom of this screen, after the author has seen every name that would
/// be included and had the chance to remove it. Adding a shortcut that skips this screen would
/// reintroduce exactly the hazard it exists to prevent.
/// <para>
/// All decision logic lives in <see cref="TemplateExportSelection"/>, <see cref="TemplateBuilder"/>,
/// <see cref="TemplateExportFolders"/> and <see cref="TemplateShareCode"/>, which are pure and
/// tested. This file holds layout and the two guarded side effects.
/// </para>
/// </remarks>
public sealed partial class MainWindow
{
    private const string ExportNeedsScanReason =
        "Press Refresh mod list on the Scan tab first. Until then the plugin does not know which "
        + "mods you have, and a template built now would contain your folders and nothing else.";

    private bool _exportOpen;
    private TemplateExportSelection? _exportSelection;
    private TemplateExportFolderSeed? _exportFolderSeed;
    private string _exportName = string.Empty;
    private string _exportAuthor = string.Empty;
    private string _exportDescription = string.Empty;
    private int _exportGroupingIndex = SortPanel.DefaultGroupingIndex;
    private bool _exportSplitGear;
    private bool _exportSplitNpc = true;
    private string _exportFilter = string.Empty;
    private string? _exportStatus;

    // The document is rebuilt when something changes rather than every frame: Describe deflates the
    // payload, which is far too much work to repeat sixty times a second for a 900-mod library.
    private bool _exportDirty = true;
    private TemplateBuildResult? _exportBuild;
    private TemplateShareCodeDescription? _exportShare;

    private void DrawTemplateExportSection()
    {
        if (!_exportOpen)
        {
            // Without a scan the plugin knows no mods, but the folder list is seeded from
            // Penumbra's organization.json, which needs no scan at all. Exporting in that state
            // silently produced a folders-only template with zero entries - a tester shipped one
            // and it looked like a working template. The two halves of this screen have different
            // prerequisites, so the screen has to gate on the stricter one.
            var scanned = _plugin.OrganizerState.HasScanned;

            ImGui.BeginDisabled(!scanned);
            if (ImGui.Button("Export my layout as a template..."))
            {
                BeginTemplateExport();
                _exportOpen = true;
            }
            ImGui.EndDisabled();

            Help.Tooltip(HelpTopics.TemplatesExport, scanned ? null : ExportNeedsScanReason);
            return;
        }

        ImGui.Separator();
        ImGui.TextWrapped(
            "This builds a template from how your library is organized RIGHT NOW - the folders your "
            + "mods are actually in, not any proposals waiting on the Review Changes tab.");

        ImGui.TextColored(
            ImGuiColors.DalamudYellow,
            "A template contains a list of your mod names. Anyone you send it to can read that list. "
            + "Check it below and remove anything you would rather not share.");

        // Belt and braces behind the gate above: the screen can be open across a scan being reset,
        // and this must never fall through to the emit controls with no rows behind them.
        if (_exportSelection is null || _exportFolderSeed is null || !_plugin.OrganizerState.HasScanned)
        {
            ImGui.TextWrapped(ExportNeedsScanReason);
            if (ImGui.Button("Close export"))
                _exportOpen = false;
            return;
        }

        if (_exportFolderSeed.OrganizationJsonUnavailable)
        {
            ImGui.TextColored(
                ImGuiColors.DalamudYellow,
                "Penumbra's folder list could not be read, so folders that hold no mods are missing "
                + "from the list below. Everything else is unaffected.");
        }

        DrawTemplateExportMetadata();
        ImGui.Separator();
        DrawTemplateExportFallback();
        ImGui.Separator();
        DrawTemplateExportFolders();
        ImGui.Separator();
        DrawTemplateExportMods();
        ImGui.Separator();
        DrawTemplateExportEmit();
    }

    private void BeginTemplateExport()
    {
        var rows = _plugin.OrganizerState.Mods;
        _exportFolderSeed = TemplateExportFolders.Seed(
            _plugin.OrganizerState.KnownFolders, _plugin.ReadOrganizationJsonOrNull());
        _exportSelection = new TemplateExportSelection(rows, _exportFolderSeed.Folders);
        _exportFilter = string.Empty;
        _exportStatus = null;
        MarkTemplateExportDirty();
    }

    private void MarkTemplateExportDirty()
    {
        _exportDirty = true;
        _exportBuild = null;
        _exportShare = null;
    }

    private void DrawTemplateExportMetadata()
    {
        if (ImGui.InputText("Template name", ref _exportName, 200))
            MarkTemplateExportDirty();
        Help.Tooltip(HelpTopics.TemplatesExportName);

        if (ImGui.InputText("Your name (optional)", ref _exportAuthor, 200))
            MarkTemplateExportDirty();

        if (ImGui.InputText("Description (optional)", ref _exportDescription, 500))
            MarkTemplateExportDirty();
    }

    private void DrawTemplateExportFallback()
    {
        ImGui.TextWrapped(
            "Mods the importer has but your template does not mention are placed by this grouping, "
            + "the same choices the Sort tab offers.");

        var labels = SortPanel.Groupings.Select(g => g.Label).ToArray();
        if (ImGui.Combo("Fallback grouping", ref _exportGroupingIndex, labels, labels.Length))
            MarkTemplateExportDirty();
        Help.Tooltip(HelpTopics.TemplatesExportFallback);

        // Both splits are inert for Creator-only grouping, which never consults the mod's type.
        var splitsApply = CurrentExportFallback().Strategy != SortStrategy.CreatorOnly;

        ImGui.BeginDisabled(!splitsApply);
        if (ImGui.Checkbox("Split gear by equipment slot", ref _exportSplitGear))
            MarkTemplateExportDirty();
        if (ImGui.Checkbox("Split NPC mods by kind", ref _exportSplitNpc))
            MarkTemplateExportDirty();
        ImGui.EndDisabled();
    }

    private TemplateFallback CurrentExportFallback()
    {
        // Clamped rather than trusted: the combo index is session state and the Groupings array is
        // the single source of truth for which strategies exist.
        var index = Math.Clamp(_exportGroupingIndex, 0, SortPanel.Groupings.Length - 1);
        return ToTemplateFallback(
            new SortSelection(SortPanel.Groupings[index].Strategy, _exportSplitGear, _exportSplitNpc));
    }

    /// <summary>
    /// Converts the UI's sort selection into the template domain's equivalent.
    /// </summary>
    /// <remarks>
    /// The two types are structurally identical and stay separate on purpose: <see cref="SortSelection"/>
    /// belongs to the Sort tab and <see cref="TemplateFallback"/> is part of the on-disk format, so
    /// collapsing them would make the Templates domain depend on this namespace. Neither carries
    /// behaviour, so unlike the folder-selection logic there is nothing here that can drift into two
    /// different answers. <c>TemplateFallbackConversionTests</c> fails if either gains a field.
    /// </remarks>
    /// <remarks>
    /// public, not internal: this repo has no InternalsVisibleTo, so the guard test below could not
    /// see it otherwise. Same reason <see cref="SortSelection"/> itself is public.
    /// </remarks>
    public static TemplateFallback ToTemplateFallback(SortSelection selection) =>
        new(selection.Strategy, selection.SplitGear, selection.SplitNpc);

    private void DrawTemplateExportFolders()
    {
        var selection = _exportSelection!;
        var seed = _exportFolderSeed!;

        ImGui.Text($"Folders: {selection.IncludedFolderCount} of {seed.Folders.Count} included");
        Help.Tooltip(HelpTopics.TemplatesExportFolders);

        using var child = ImRaii.Child("##export-folders", new System.Numerics.Vector2(0, 120), true);
        foreach (var folder in seed.Folders)
        {
            using var id = ImRaii.PushId(folder);
            var included = selection.IsFolderIncluded(folder);
            if (ImGui.Checkbox(StripImGuiIdMarkers(folder), ref included))
            {
                // Excluding a folder also excludes the mods inside it, so an excluded folder cannot
                // keep publishing its contents through the entry list.
                selection.SetFolder(folder, included);
                MarkTemplateExportDirty();
            }
        }
    }

    private void DrawTemplateExportMods()
    {
        var selection = _exportSelection!;
        var rows = _plugin.OrganizerState.Mods;

        ImGui.Text($"Mods: {selection.IncludedRowCount} included, {selection.ExcludedRowCount} excluded");

        if (ImGui.Button("Include all"))
        {
            selection.SetAllRows(true);
            MarkTemplateExportDirty();
        }

        ImGui.SameLine();
        if (ImGui.Button("Exclude all"))
        {
            selection.SetAllRows(false);
            MarkTemplateExportDirty();
        }

        ImGui.SameLine();
        ImGui.TextDisabled("(these apply to every mod, not only the filtered ones)");

        ImGui.InputText("Search mods##export-filter", ref _exportFilter, 256);
        Help.Tooltip(HelpTopics.TemplatesExportSearch);

        using var child = ImRaii.Child("##export-mods", new System.Numerics.Vector2(0, 220), true);
        foreach (var row in rows)
        {
            // Filtering changes what is SHOWN and nothing else. Include all / Exclude all above
            // deliberately ignore it, so a filtered list cannot hide rows from a bulk decision.
            if (!TemplateExportSelection.MatchesFilter(row.Name, _exportFilter))
                continue;

            using var id = ImRaii.PushId(row.Identifier);
            var included = selection.IsRowIncluded(row.Identifier);
            if (ImGui.Checkbox(StripImGuiIdMarkers(row.Name), ref included))
            {
                selection.SetRow(row.Identifier, included);
                MarkTemplateExportDirty();
            }
        }
    }

    private void DrawTemplateExportEmit()
    {
        RebuildTemplateExportIfDirty();

        var build = _exportBuild!;
        var share = _exportShare!;

        ImGui.Text($"{build.Document.Entries.Count} mods would be written into this template.");

        // Deliberately a warning and not a block. "Here is my folder skeleton, sort your own mods
        // into it" is a real thing to share, and TemplateBuilder already guarantees the result
        // stays importable. What went wrong before was that it happened by accident and said
        // nothing, so this makes the consequence explicit instead of forbidding it.
        if (build.Document.Entries.Count == 0)
        {
            ImGui.TextColored(
                ImGuiColors.DalamudYellow,
                "This template will carry your folders only. It will not place any mod - anyone "
                + "importing it gets the empty folder structure and their own fallback sort.");
        }

        if (build.RootLevelSkipped > 0)
        {
            ImGui.TextWrapped(
                $"{build.RootLevelSkipped} mods sit at the top level of your library, outside any "
                + "folder. A template only carries folders, so those are left out.");
        }

        foreach (var warning in build.Warnings.Take(20))
        {
            ImGui.TextColored(
                ImGuiColors.DalamudYellow,
                $"Two of your mods normalize to \"{warning.Subject}\" but sit in different folders, "
                + "so it was left out. Exclude one of them to include the other.");
        }

        if (build.Warnings.Count > 20)
            ImGui.TextDisabled($"...and {build.Warnings.Count - 20} more.");

        var nameMissing = string.IsNullOrWhiteSpace(_exportName);
        if (nameMissing)
            ImGui.TextDisabled("Give the template a name to save or copy it.");

        ImGui.BeginDisabled(nameMissing);
        if (ImGui.Button("Save to templates folder"))
            SaveTemplateExport();
        ImGui.EndDisabled();
        Help.Tooltip(HelpTopics.TemplatesExportSave);

        ImGui.SameLine();
        ImGui.BeginDisabled(nameMissing || share.ExceedsChatLimit);
        if (ImGui.Button("Copy share code"))
            CopyTemplateExportShareCode();
        ImGui.EndDisabled();
        Help.Tooltip(HelpTopics.TemplatesExportShareCode);

        if (share.ExceedsChatLimit)
        {
            ImGui.TextWrapped(
                $"The share code is {share.Length} characters, past the {TemplateShareCode.ChatMessageLimit} "
                + "a chat message holds, so it would be cut off when pasted. Save the file and send "
                + "that instead, or exclude more mods.");
        }
        else
        {
            ImGui.TextDisabled($"Share code: {share.Length} characters.");
        }

        if (_exportStatus is not null)
            ImGui.TextWrapped(_exportStatus);

        if (ImGui.Button("Close export"))
        {
            _exportOpen = false;
            _exportStatus = null;
        }
    }

    private void RebuildTemplateExportIfDirty()
    {
        if (!_exportDirty && _exportBuild is not null && _exportShare is not null)
            return;

        var metadata = new TemplateMetadata(
            string.IsNullOrWhiteSpace(_exportName) ? "Untitled" : _exportName.Trim(),
            string.IsNullOrWhiteSpace(_exportAuthor) ? null : _exportAuthor.Trim(),
            string.IsNullOrWhiteSpace(_exportDescription) ? null : _exportDescription.Trim(),
            CurrentExportFallback(),
            // Folder-label renaming is an import-side nicety the author does not need to configure to
            // publish a working template, so v1 of this screen ships without an editor for it rather
            // than with a half-built one. The format carries the field either way.
            new Dictionary<string, string>());

        _exportBuild = TemplateBuilder.Build(
            _plugin.OrganizerState.Mods,
            _exportSelection!.IncludedIdentifiers,
            _exportSelection.IncludedFolders,
            metadata);

        _exportShare = TemplateShareCode.Describe(_exportBuild.Document);
        _exportDirty = false;
    }

    private void SaveTemplateExport()
    {
        try
        {
            var json = TemplateCodec.EncodeJson(_exportBuild!.Document);
            var fileName = _plugin.TemplateStore.Save(json, _exportName.Trim());

            // The new file belongs in the list above without needing a manual refresh.
            _templateListing = _plugin.TemplateStore.List();
            _exportStatus = $"Saved as {fileName}.";
            _lastError = null;
        }
        catch (Exception ex)
        {
            // This runs inside the draw call, where an escaping exception kills the frame.
            _lastError = $"Could not save the template: {ex.Message}";
            _exportStatus = null;
        }
    }

    private void CopyTemplateExportShareCode()
    {
        try
        {
            ImGui.SetClipboardText(_exportShare!.Code);
            _exportStatus = "Share code copied to your clipboard.";
            _lastError = null;
        }
        catch (Exception ex)
        {
            _lastError = $"Could not copy the share code: {ex.Message}";
            _exportStatus = null;
        }
    }
}
