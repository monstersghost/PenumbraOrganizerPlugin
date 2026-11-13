using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using PenumbraOrganizer.Plugin.Organizer.NewMods;

namespace PenumbraOrganizer.Plugin.Windows;

/// <summary>
/// The floating new-mod popup, drawn whether or not the main window is open. A thin shell: the
/// pipeline decides when it shows (<see cref="NewModSortPipeline.Banner"/>) and what the buttons do;
/// the buttons only record the answer, and the work happens on the framework update.
/// </summary>
public sealed class NewModPopup : Window
{
    private const float WrapWidth = 380;

    private readonly NewModSortPipeline _pipeline;
    private readonly Configuration _config;
    private readonly Action _saveConfig;

    public NewModPopup(NewModSortPipeline pipeline, Configuration config, Action saveConfig)
        : base(NewModPromptText.PopupTitle + "###PenumbraOrganizerNewModPopup",
            ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.AlwaysAutoResize
            | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings)
    {
        _pipeline = pipeline;
        _config = config;
        _saveConfig = saveConfig;

        // Shown unprompted, so Escape aimed at the game must not dismiss it.
        RespectCloseHotkey = false;
        DisableWindowSounds = true;
        Position = new Vector2(80, 120);
        PositionCondition = ImGuiCond.FirstUseEver;
    }

    // Every frame, before the open state is checked: the pipeline alone decides visibility. The
    // progress line has no X: it would only reopen next frame.
    public override void PreOpenCheck()
    {
        var asking = _pipeline.Banner is not null;
        IsOpen = asking || _pipeline.ProgressText is not null;
        ShowCloseButton = asking;
    }

    // The window's own X while the question is still open counts as Not now. When it closes
    // because the question was answered or withdrawn, Banner is already null and this does nothing.
    public override void OnClose()
    {
        if (_pipeline.Banner is not null)
            _pipeline.UserChose(NewModChoice.NotNow);
    }

    public override void Draw()
    {
        using var theme = PluginTheme.Push();

        if (_pipeline.Banner is { } banner)
        {
            ImGui.PushTextWrapPos(WrapWidth);
            ImGui.TextColored(PluginTheme.ChangedGood, banner.Message);
            foreach (var warning in banner.Warnings)
                ImGui.TextDisabled(warning);
            ImGui.PopTextWrapPos();

            ImGui.Spacing();
            if (ImGui.Button(NewModPromptText.SortNowLabel + "##newmods-sort"))
                _pipeline.UserChose(NewModChoice.SortNow);
            Help.Tooltip(HelpTopics.NewModsBanner);
            ImGui.SameLine();
            if (ImGui.Button(NewModPromptText.ReviewFirstLabel + "##newmods-review"))
                _pipeline.UserChose(NewModChoice.ReviewFirst);
            ImGui.SameLine();
            if (ImGui.Button(NewModPromptText.NotNowLabel + "##newmods-later"))
                _pipeline.UserChose(NewModChoice.NotNow);

            ImGui.Spacing();
            // Drawn only while the popup may show, so it always starts unticked.
            var dontShow = false;
            if (ImGui.Checkbox(NewModPromptText.DontShowPopupLabel + "##newmods-dont-show", ref dontShow) && dontShow)
            {
                _config.ShowNewModPopup = false;
                _saveConfig();
            }
            Help.Tooltip(HelpTopics.NewModsPopupDontShow);
        }
        else if (_pipeline.ProgressText is { } progress)
        {
            ImGui.TextDisabled(progress);
        }
    }
}
