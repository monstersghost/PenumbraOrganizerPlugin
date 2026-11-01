using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;

namespace PenumbraOrganizer.Plugin.Windows;

public sealed partial class MainWindow
{
    private void DrawTemplatesTab()
    {
        using var tab = ImRaii.TabItem("Templates");
        if (!tab)
            return;

        var gates = CurrentGates();

        ImGui.TextWrapped(
            "A template is a layout someone else shared. Importing one proposes where your mods "
            + "would go: mods you both have land where they put them, and everything else is placed "
            + "by the fallback strategy they chose. Nothing is applied until you review it.");
        ImGui.Separator();

        _templateListing ??= _plugin.TemplateStore.List();

        if (ImGui.Button("Refresh list"))
        {
            _templateListing = _plugin.TemplateStore.List();
            _selectedTemplate = null;
            _templatePlan = null;
            _templatePlanTemplate = null;
            _templateStatus = null;
        }

        Help.Tooltip(HelpTopics.TemplatesRefreshList);

        ImGui.SameLine();
        if (ImGui.Button("Open templates folder"))
        {
            // Created on demand: the folder need not exist until someone actually wants it.
            // OpenFileWithDefaultApp is this window's existing helper and shell-executes a
            // directory path just as it does a file, so no new API is introduced here.
            try
            {
                Directory.CreateDirectory(_plugin.TemplatesDirectory);
                OpenFileWithDefaultApp(_plugin.TemplatesDirectory);
            }
            catch (Exception ex)
            {
                _lastError = $"Could not open the templates folder: {ex.Message}";
                _templateStatus = null;
            }
        }

        Help.Tooltip(HelpTopics.TemplatesOpenFolder);

        ImGui.SameLine();
        if (ImGui.Button("Import template file..."))
        {
            _fileDialogManager.OpenFileDialog(
                "Import Template",
                ".json",
                (success, paths) =>
                {
                    if (!success || paths.Count == 0)
                        return;

                    ImportTemplateFile(paths[0]);
                },
                selectionCountMax: 1);
        }

        Help.Tooltip(HelpTopics.TemplatesImportFile);

        if (_templateStatus is not null)
            ImGui.TextWrapped(_templateStatus);

        // Export sits above the list rather than below it: the list can be hundreds of rows tall,
        // and a control that publishes data should not be somewhere the user has to scroll to find
        // and might miss the review screen attached to it.
        DrawTemplateExportSection();

        if (_exportOpen)
            return;

        var listing = _templateListing!;

        foreach (var unreadable in listing.UnreadableFiles)
        {
            ImGui.TextColored(
                ImGuiColors.DalamudYellow, $"Skipped {unreadable}: not a readable template.");
        }

        if (listing.Templates.Count == 0)
        {
            ImGui.TextWrapped(
                "No templates yet. Use \"Import template file...\" to add a .json someone shared, "
                + "or drop one into the templates folder and hit Refresh list.");
            return;
        }

        ImGui.Separator();

        foreach (var stored in listing.Templates)
        {
            var label = string.IsNullOrWhiteSpace(stored.Template.Author)
                ? stored.Template.Name
                : $"{stored.Template.Name} — {stored.Template.Author}";
            label = StripImGuiIdMarkers(label);

            using var id = ImRaii.PushId(stored.FileName);
            if (ImGui.Selectable(label, _selectedTemplate == stored))
            {
                _selectedTemplate = stored;
                _templatePlan = null;
                _templatePlanTemplate = null;
                _templateStatus = null;
            }
        }

        if (_selectedTemplate is null)
            return;

        ImGui.Separator();
        DrawTemplatePreview(_selectedTemplate, gates);
    }

    private void DrawTemplatePreview(Organizer.Templates.StoredTemplate stored, ActivityGates gates)
    {
        var previewTemplate = _templatePlanTemplate ?? stored.Template;
        if (!string.IsNullOrWhiteSpace(previewTemplate.Description))
            ImGui.TextWrapped(previewTemplate.Description);

        ImGui.BeginDisabled(!gates.CanStageProposals);
        if (ImGui.Button("Preview against my library"))
            BuildTemplatePlan(stored);
        ImGui.EndDisabled();

        // Help.Tooltip passes AllowWhenDisabled itself and appends the reason under the
        // explanation, so the control keeps its help text while gated rather than swapping to a
        // bare "library work is in progress" that says nothing about what the button does.
        Help.Tooltip(HelpTopics.TemplatesPreview, gates.CanStageProposals ? null : LibraryWorkInProgress);

        if (_templatePlan is null)
            return;

        var plan = _templatePlan;
        var report = plan.Report;

        ImGui.TextWrapped(
            $"{report.RowsMatchedByEntry} of {report.ConsideredRows} mods matched this template; "
            + $"{report.RowsPlacedByFallback} placed by its fallback strategy; "
            + $"{report.ProtectedRows} skipped as protected. "
            + $"{report.TemplateEntriesUnmatched} of the template's entries matched nothing you own.");

        if (report.AmbiguousLocalMatchGroups > 0)
        {
            ImGui.TextColored(
                ImGuiColors.DalamudYellow,
                $"{report.AmbiguousLocalMatchGroups} template entries matched more than one of your "
                + "mods; every match is placed in the same folder.");
        }

        foreach (var warning in plan.Warnings.Take(20))
            ImGui.TextColored(ImGuiColors.DalamudYellow, DescribeWarning(warning));

        if (plan.Warnings.Count > 20)
            ImGui.TextDisabled($"...and {plan.Warnings.Count - 20} more.");

        ImGui.Separator();

        var tree = Organizer.Templates.TemplateTreeBuilder.Build(
            previewTemplate.Folders, plan.FolderCounts);
        DrawTemplateTree(tree);

        ImGui.Separator();
        ImGui.BeginDisabled(!gates.CanStageProposals);
        if (ImGui.Button("Apply this template to my proposals"))
            ApplyTemplatePlan();
        ImGui.EndDisabled();

        Help.Tooltip(HelpTopics.TemplatesApply, gates.CanStageProposals ? null : LibraryWorkInProgress);
    }

    private const string LibraryWorkInProgress = "Library work is in progress.";

    private static void DrawTemplateTree(IReadOnlyList<Organizer.Templates.TemplateTreeNode> nodes)
    {
        foreach (var node in nodes)
        {
            // PushID hashes the string as an opaque id rather than parsing it for "##"/"###"
            // markers, so an untrusted folder segment containing '#' cannot collapse two nodes
            // onto one id. Stripping '#' from the path instead would make "#a" and "a" collide.
            using var id = ImRaii.PushId(node.FullPath);

            var label = node.TotalCount == 0
                ? $"{StripImGuiIdMarkers(node.Segment)} (empty)"
                : $"{StripImGuiIdMarkers(node.Segment)} ({node.TotalCount})";

            if (node.Children.Count == 0)
            {
                ImGui.TreeNodeEx(label, ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen);
                continue;
            }

            using var treeNode = ImRaii.TreeNode(label);
            if (treeNode)
                DrawTemplateTree(node.Children);
        }
    }

    // ImGui reads "###" as "the ID is everything after this", so a '#' in a stranger's template
    // name or folder segment would let two distinct widgets collapse onto one ID and share state.
    private static string StripImGuiIdMarkers(string text) => text.Replace("#", string.Empty);

    private static string DescribeWarning(Organizer.Templates.TemplateWarning warning) =>
        warning.Code switch
        {
            Organizer.Templates.TemplateWarningCode.UnmatchedTemplateEntry =>
                $"You do not have \"{warning.Subject}\".",
            Organizer.Templates.TemplateWarningCode.AmbiguousLocalMatch =>
                $"More than one of your mods is named \"{warning.Subject}\".",
            Organizer.Templates.TemplateWarningCode.InvalidEntryPath =>
                $"Skipped a bad entry: \"{warning.Subject}\".",
            Organizer.Templates.TemplateWarningCode.InvalidFolderPath =>
                $"Skipped a bad folder: \"{warning.Subject}\".",
            Organizer.Templates.TemplateWarningCode.UnknownFolderLabelKey =>
                $"Ignored an unknown folder label: \"{warning.Subject}\".",
            Organizer.Templates.TemplateWarningCode.DuplicateEntry =>
                $"Duplicate entry for \"{warning.Subject}\".",
            Organizer.Templates.TemplateWarningCode.ConflictingDuplicateEntry =>
                $"\"{warning.Subject}\" appears twice with different folders, so it was skipped.",
            _ => $"{warning.Code}: {warning.Subject}",
        };

    private void BuildTemplatePlan(Organizer.Templates.StoredTemplate stored)
    {
        try
        {
            // Re-read the file rather than planning from the listing's cached copy: the listing
            // may be minutes old and the file may have been replaced on disk since.
            var json = File.ReadAllText(Path.Combine(_plugin.TemplatesDirectory, stored.FileName));
            var decoded = Organizer.Templates.TemplateCodec.DecodeJson(json);
            if (!decoded.Succeeded)
            {
                _lastError = $"Template could not be read: {decoded.ErrorDetail}";
                _templateStatus = null;
                _templatePlan = null;
                _templatePlanTemplate = null;
                return;
            }

            _templatePlan = Organizer.Templates.TemplatePlanner.PlanFromDecoded(
                decoded, _plugin.OrganizerState.Mods, _creatorCanonicalizer.Canonicalize);
            _templatePlanTemplate = decoded.Template;
            _templatePlanScanGeneration = _plugin.OrganizerState.ScanGeneration;
            _templateStatus = null;
            _lastError = null;
        }
        catch (Exception ex)
        {
            _lastError = $"Template preview failed: {ex.Message}";
            _templateStatus = null;
            _templatePlan = null;
            _templatePlanTemplate = null;
        }
    }

    private void ApplyTemplatePlan()
    {
        if (_templatePlan is null)
            return;

        // A plan describes the rows it was built from. If a rescan landed between the preview and
        // this click, applying it would stage a partial result while reporting the old counts --
        // so refuse and make the user look at a fresh preview instead.
        if (_templatePlanScanGeneration != _plugin.OrganizerState.ScanGeneration)
        {
            _templatePlan = null;
            _templatePlanTemplate = null;
            _lastError = "Your library was rescanned after this preview. Preview again before applying.";
            _templateStatus = null;
            return;
        }

        if (!CurrentGates().CanStageProposals)
        {
            _lastError = "Applying the template was cancelled because library work started.";
            _templateStatus = null;
            return;
        }

        var report = _plugin.OrganizerState.ApplyTemplate(_templatePlan);
        _templateStatus =
            $"Staged {report.RowsMatchedByEntry + report.RowsPlacedByFallback} proposals. "
            + "Open Review Changes to check them before applying.";
        _lastError = null;
    }

    private void ImportTemplateFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length > Organizer.Templates.TemplateLimits.MaxDecompressedBytes)
            {
                _lastError =
                    $"That file is {info.Length / 1024 / 1024} MB; templates are limited to "
                    + $"{Organizer.Templates.TemplateLimits.MaxDecompressedBytes / 1024 / 1024} MB.";
                _templateStatus = null;
                return;
            }

            var json = File.ReadAllText(path);
            var fileName = _plugin.TemplateStore.Save(json, Path.GetFileNameWithoutExtension(path));

            _templateListing = _plugin.TemplateStore.List();
            _selectedTemplate = _templateListing.Templates
                .FirstOrDefault(t => string.Equals(t.FileName, fileName, StringComparison.OrdinalIgnoreCase));
            _templatePlan = null;
            _templatePlanTemplate = null;
            _templateStatus = $"Imported as {fileName}.";
            _lastError = null;
        }
        catch (Exception ex)
        {
            _lastError = $"Template import failed: {ex.Message}";
            _templateStatus = null;
        }
    }
}
