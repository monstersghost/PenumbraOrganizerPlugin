using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace PenumbraOrganizer.Plugin.Windows;

/// <summary>
/// The guided first run. A thin shell around <see cref="FirstRunSteps"/>, which owns every rule
/// worth testing; this contributes no logic of its own beyond drawing and routing buttons.
/// </summary>
public sealed class FirstRunWindow : Window
{
    private readonly Func<bool> _penumbraAvailable;
    private readonly Action _onSeen;

    private FirstRunSteps _steps = new(penumbraAvailable: true);

    public FirstRunWindow(Func<bool> penumbraAvailable, Action onSeen)
        : base("Penumbra Organizer - First Run")
    {
        _penumbraAvailable = penumbraAvailable;
        _onSeen = onSeen;

        // FirstUseEver, and offset rather than centred: MainWindow centres itself, and two centred
        // windows would sit on top of each other - which defeats a walkthrough that tells you to go
        // and press things in the window behind it.
        Size = new Vector2(420, 260);
        SizeCondition = ImGuiCond.FirstUseEver;
        Position = new Vector2(80, 80);
        PositionCondition = ImGuiCond.FirstUseEver;
    }

    /// <summary>
    /// Starts a fresh run. Progress is deliberately not resumed - reopening starts at step one.
    /// </summary>
    public void Start()
    {
        _steps = new FirstRunSteps(_penumbraAvailable());
        IsOpen = true;
    }

    /// <summary>
    /// The window's own X. Dalamud calls this on close however it happened, so it is the single
    /// place the run is settled - the buttons below just close the window and let this run.
    /// </summary>
    public override void OnClose()
    {
        _steps.Closed();
        if (_steps.ShouldMarkSeen)
            _onSeen();
    }

    public override void Draw()
    {
        using var theme = PluginTheme.Push();

        ImGui.PushTextWrapPos(ImGui.GetContentRegionAvail().X);
        ImGui.TextColored(PluginTheme.ChangedGood, Help.Title(_steps.Current) ?? string.Empty);
        ImGui.Spacing();
        ImGui.TextUnformatted(Help.Step(_steps.Current) ?? string.Empty);
        ImGui.PopTextWrapPos();

        // Buttons pinned to the bottom so they do not jump about as step text changes length.
        ImGui.SetCursorPosY(ImGui.GetWindowHeight() - ImGui.GetFrameHeightWithSpacing() - ImGui.GetStyle().WindowPadding.Y);

        if (_steps.IsWalkthrough)
        {
            ImGui.TextDisabled($"{_steps.Index + 1} of {_steps.Count}");
            ImGui.SameLine();
        }

        ImGui.BeginDisabled(!_steps.CanGoBack);
        if (ImGui.Button("Back"))
            _steps.Back();
        ImGui.EndDisabled();

        ImGui.SameLine();
        // "Done" rather than "Next" on the last step, so the button says what it will do. The
        // Penumbra-unavailable notice is a single step, so it reads Done immediately.
        if (ImGui.Button(_steps.IsLastStep ? "Done" : "Next"))
        {
            _steps.Next();
            if (_steps.IsFinished)
                Close();
        }

        if (!_steps.IsLastStep)
        {
            ImGui.SameLine();
            if (ImGui.Button("Skip"))
            {
                _steps.Skip();
                Close();
            }
        }
    }

    // Setting IsOpen = false routes through OnClose, so the seen flag is written in exactly one
    // place regardless of which button got here.
    private void Close() => IsOpen = false;
}
