using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Windows;

/// <summary>The labels of the Protect tab's tool mode dropdowns, kept beside their index mapping.</summary>
/// <remarks>public, not internal: this repo has no InternalsVisibleTo.</remarks>
public static class ToolModeLabels
{
    public static IReadOnlyList<string> All { get; } =
        ["Protected", "Sort inside their folder", "Treat as normal"];

    public static int IndexOf(ToolMode mode) => (int)ToolPolicy.Normalize(mode);

    /// <summary>An index outside the list reads as Protected, the safe default.</summary>
    public static ToolMode FromIndex(int index) =>
        index >= 0 && index < All.Count ? (ToolMode)index : ToolMode.Protected;
}
