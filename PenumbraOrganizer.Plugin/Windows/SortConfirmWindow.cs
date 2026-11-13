using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using PenumbraOrganizer.Plugin.Organizer.NewMods;

namespace PenumbraOrganizer.Plugin.Windows;

/// <summary>
/// The confirm step for the new-mod popup's Sort now and /porganizer sortnew|sortroot: lists what the
/// staged sort would move and where, then Apply, Review first or Cancel. A thin shell over
/// <see cref="NewModSortPipeline.Preview"/>; the buttons only record the answer, and the pipeline acts
/// on it on the framework update (Apply goes through the existing Apply path only).
/// </summary>
public sealed class SortConfirmWindow : Window
{
    private readonly NewModSortPipeline _pipeline;

    public SortConfirmWindow(NewModSortPipeline pipeline)
        : base(NewModPromptText.ConfirmTitle + "###PenumbraOrganizerSortConfirm",
            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings)
    {
        _pipeline = pipeline;
        Size = new Vector2(620, 380);
        SizeCondition = ImGuiCond.Appearing;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 220),
            MaximumSize = new Vector2(1600, 1200),
        };
        Position = new Vector2(120, 160);
        PositionCondition = ImGuiCond.Appearing;
    }

    public override void PreOpenCheck() => IsOpen = _pipeline.Preview is not null;

    // Closing with the X (or Escape) while the question is open is Cancel.
    public override void OnClose()
    {
        if (_pipeline.Preview is not null)
            _pipeline.Confirm(NewModConfirmChoice.Cancel);
    }

    public override void Draw()
    {
        using var theme = PluginTheme.Push();

        if (_pipeline.Preview is not { } preview)
            return;

        ImGui.TextColored(PluginTheme.ChangedGood, preview.Title);
        if (preview.ScopeText.Length > 0)
            ImGui.TextDisabled(preview.ScopeText);
        ImGui.Spacing();

        var buttonsHeight = ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y;
        using (var child = ImRaii.Child("SortConfirmRows", new Vector2(0, -buttonsHeight), border: true))
        {
            if (child)
                DrawRows(preview);
        }

        if (ImGui.Button(NewModPromptText.ApplyLabel + "##sortconfirm-apply"))
            _pipeline.Confirm(NewModConfirmChoice.Apply);
        Help.Tooltip(HelpTopics.NewModsConfirm);
        ImGui.SameLine();
        if (ImGui.Button(NewModPromptText.ReviewFirstLabel + "##sortconfirm-review"))
            _pipeline.Confirm(NewModConfirmChoice.ReviewFirst);
        ImGui.SameLine();
        if (ImGui.Button(NewModPromptText.CancelLabel + "##sortconfirm-cancel"))
            _pipeline.Confirm(NewModConfirmChoice.Cancel);
    }

    private static void DrawRows(NewModPreview preview)
    {
        // The child around it scrolls; the table itself does not.
        using var table = ImRaii.Table("SortConfirmTable", 3,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable);
        if (!table)
            return;

        ImGui.TableSetupColumn("Mod");
        ImGui.TableSetupColumn("From");
        ImGui.TableSetupColumn("To");
        ImGui.TableHeadersRow();

        foreach (var row in preview.Rows)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.Name);
            ImGui.TableNextColumn();
            ImGui.TextDisabled(row.From);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.To);
        }
    }
}
