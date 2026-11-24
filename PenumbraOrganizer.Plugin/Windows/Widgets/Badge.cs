using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace PenumbraOrganizer.Plugin.Windows.Widgets;

/// <summary>The badge colours: the warning colour for "Custom", a muted one for counts.</summary>
public enum BadgeKind
{
    Warning,
    Muted,
}

/// <summary>A small rounded pill with a short text.</summary>
public static class Badge
{
    /// <summary>Draws the badge as a layout item at the cursor (it takes space like a text).</summary>
    public static void Draw(string text, BadgeKind kind)
    {
        var size = Size(text);
        var start = ImGui.GetCursorScreenPos();
        ImGui.Dummy(size);
        DrawAt(ImGui.GetWindowDrawList(), start, text, kind);
    }

    /// <summary>The badge's outer size for this text.</summary>
    public static Vector2 Size(string text) => WidgetLayout.BadgeSize(ImGui.CalcTextSize(text));

    /// <summary>Draws the badge at a screen position without taking layout space (for custom-drawn rows).</summary>
    public static void DrawAt(ImDrawListPtr draw, Vector2 topLeft, string text, BadgeKind kind)
    {
        var size = Size(text);
        var colour = ImGui.GetColorU32(kind == BadgeKind.Warning ? PluginTheme.Warning : PluginTheme.TextMuted);
        draw.AddRect(topLeft, topLeft + size, colour, size.Y / 2f, ImDrawFlags.None, 1f);
        draw.AddText(topLeft + WidgetLayout.BadgePadding, colour, text);
    }
}
