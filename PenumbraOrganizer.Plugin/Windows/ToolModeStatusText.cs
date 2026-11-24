using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Windows;

/// <summary>The Sort tab's status line: its text, and whether it is drawn in the warning colour.</summary>
public sealed record ToolModeStatus(string Text, bool Warn);

/// <summary>
/// The Sort tab's one-line reminder of the Heliosphere and Proteus modes, for someone who opens Sort without
/// visiting Protect. Pure text; the modes themselves are only ever changed on the Protect tab. Plain ASCII.
/// </summary>
/// <remarks>public, not internal: this repo has no InternalsVisibleTo.</remarks>
public static class ToolModeStatusText
{
    public const string OpenProtectTabLabel = "Open Protect tab";
    private const string Hint = "Change this on the Protect tab.";

    /// <summary>
    /// The line, or null when there is nothing to say: no tool-managed mods and both modes at the default
    /// (Protected). A tool is named when it has mods or its mode is not the default. Warn is set when a named
    /// tool is Protected and has mods, since those mods will not be sorted.
    /// </summary>
    public static ToolModeStatus? Build(int heliosphereMods, ToolMode heliosphere, int proteusMods, ToolMode proteus)
    {
        heliosphere = ToolPolicy.Normalize(heliosphere);
        proteus = ToolPolicy.Normalize(proteus);

        var parts = new List<string>();
        var warn = false;
        Add("Heliosphere", heliosphereMods, heliosphere);
        Add("Proteus", proteusMods, proteus);
        if (parts.Count == 0)
            return null;

        parts.Add(Hint);
        return new ToolModeStatus(string.Join(" ", parts), warn);

        void Add(string tool, int count, ToolMode mode)
        {
            count = Math.Max(0, count);
            if (count == 0 && mode == ToolMode.Protected)
                return;

            var heldBack = mode == ToolMode.Protected && count > 0;
            warn |= heldBack;
            var label = ToolModeLabels.All[ToolModeLabels.IndexOf(mode)] + (heldBack ? " (not sorted)" : string.Empty);
            parts.Add($"{count} {tool} {(count == 1 ? "mod" : "mods")}: {label}.");
        }
    }

    /// <summary>The tool-managed mods among the loaded rows, counted as the Protect tab's dropdowns count them.</summary>
    public static (int Heliosphere, int Proteus) Count(IEnumerable<OrganizerModRow> mods)
    {
        var heliosphere = 0;
        var proteus = 0;
        foreach (var m in mods)
        {
            if (m.ProteusRole == ProteusRole.Made)
                proteus++;
            else if (m.HeliosphereManaged && m.ProteusRole == ProteusRole.None)
                heliosphere++;
        }

        return (heliosphere, proteus);
    }
}
