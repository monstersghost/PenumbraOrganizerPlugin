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
    private const string WindowId = "###PenumbraOrganizerNewModPopup";

    private readonly NewModSortPipeline _pipeline;
    private readonly Configuration _config;
    private readonly Action _saveConfig;
    private readonly Action _openProtectTab;

    public NewModPopup(NewModSortPipeline pipeline, Configuration config, Action saveConfig, Action openProtectTab)
        : base(NewModPromptText.PopupTitle + WindowId,
            ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.AlwaysAutoResize
            | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings)
    {
        _pipeline = pipeline;
        _config = config;
        _saveConfig = saveConfig;
        _openProtectTab = openProtectTab;

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
        var banner = _pipeline.Banner is not null;
        var notice = !banner && _pipeline.ToolNotice is not null;
        var asking = banner || notice;
        IsOpen = asking || _pipeline.ProgressText is not null;
        ShowCloseButton = asking;
        // The ### id keeps the window (position, size) the same whichever title shows.
        WindowName = (notice ? NewModPromptText.ToolNoticeTitle : NewModPromptText.PopupTitle) + WindowId;
    }

    // The window's own X while the question is still open counts as Not now (or OK for the tool
    // notice). When it closes because the question was answered or withdrawn, both are already null
    // and this does nothing.
    public override void OnClose()
    {
        if (_pipeline.Banner is not null)
            _pipeline.UserChose(NewModChoice.NotNow);
        else if (_pipeline.ToolNotice is not null)
            _pipeline.DismissToolNotice();
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
            if (banner.ToolLine is { } toolLine)
                ImGui.TextColored(PluginTheme.Warning, toolLine);
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
            if (banner.ToolLine is not null)
            {
                ImGui.SameLine();
                // Opens the Protect tab and leaves the question open: the eligible mods are still waiting.
                if (ImGui.Button(NewModPromptText.OpenProtectTabLabel + "##newmods-protect"))
                    _openProtectTab();
            }

            DrawDontShow();
        }
        else if (_pipeline.ToolNotice is { } notice)
        {
            // Nothing would move, so no Sort now: only what happened and where to change it.
            ImGui.PushTextWrapPos(WrapWidth);
            ImGui.TextColored(PluginTheme.Warning, notice.Message);
            ImGui.PopTextWrapPos();

            ImGui.Spacing();
            if (ImGui.Button(NewModPromptText.OpenProtectTabLabel + "##newmods-notice-protect"))
            {
                _pipeline.DismissToolNotice();
                _openProtectTab();
            }
            ImGui.SameLine();
            if (ImGui.Button(NewModPromptText.ToolNoticeOkLabel + "##newmods-notice-ok"))
                _pipeline.DismissToolNotice();

            DrawDontShow();
        }
        else if (_pipeline.ProgressText is { } progress)
        {
            ImGui.TextDisabled(progress);
        }
    }

    private void DrawDontShow()
    {
        ImGui.Spacing();
        // Drawn only while the popup may show, so it always starts unticked. Silences the tool notice too.
        var dontShow = false;
        if (ImGui.Checkbox(NewModPromptText.DontShowPopupLabel + "##newmods-dont-show", ref dontShow) && dontShow)
        {
            _config.ShowNewModPopup = false;
            _saveConfig();
        }
        Help.Tooltip(HelpTopics.NewModsPopupDontShow);
    }
}
