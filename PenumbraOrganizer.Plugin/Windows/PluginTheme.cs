using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace PenumbraOrganizer.Plugin.Windows;

public static class PluginTheme
{
    private const uint Accent = 0xFFF16663;      // #6366F1
    private const uint WindowBg = 0xFF241D1B;    // #1B1D24
    private const uint Surface = 0xFF2F2623;     // #23262F
    private const uint SurfaceAlt = 0xFF3A2E2A;  // #2A2E3A
    private const uint Border = 0xFF4A3C38;      // #383C4A
    private const uint Text = 0xFFF3E9E7;        // #E7E9F3
    private const uint TextDim = 0xFFB8A39C;     // #9CA3B8

    // Semantic status colors from the mockup's swatch row (protected / changed-good / collision-bad).
    public static readonly Vector4 Protected = Rgb(0xF3, 0xC9, 0x69);   // #F3C969
    public static readonly Vector4 ChangedGood = Rgb(0x6E, 0xE7, 0xA8); // #6EE7A8
    public static readonly Vector4 CollisionBad = Rgb(0xF9, 0x80, 0x80); // #F98080

    // The widget kit's colours and shapes (spec 2, section 11). Screens never use a literal colour: a new value
    // goes here. The values follow the constants above, so the kit matches the rest of the window.
    public static readonly Vector4 AccentColor = Rgb(0x63, 0x66, 0xF1);                 // Accent
    public static readonly Vector4 AccentText = Rgb(0xA5, 0xA8, 0xFF);                  // the accent, readable on Surface
    public static readonly Vector4 AccentSoft = new(0x63 / 255f, 0x66 / 255f, 0xF1 / 255f, 0.22f);
    public static readonly Vector4 CardBackground = Rgb(0x2A, 0x2E, 0x3A);              // SurfaceAlt
    public static readonly Vector4 CardBorder = Rgb(0x38, 0x3C, 0x4A);                  // Border
    public static readonly Vector4 RowHover = Rgb(0x33, 0x37, 0x46);
    public static readonly Vector4 PathBackground = Rgb(0x23, 0x26, 0x2F);              // Surface
    public static readonly Vector4 TextNormal = Rgb(0xE7, 0xE9, 0xF3);                  // Text
    public static readonly Vector4 TextMuted = Rgb(0x9C, 0xA3, 0xB8);                   // TextDim
    public static readonly Vector4 ToggleOff = Rgb(0x4A, 0x50, 0x63);
    public static readonly Vector4 Warning = Protected;
    public const float CardRounding = 6f;
    public const float RowRounding = 4f;
    public static readonly Vector2 CardPadding = new(12f, 10f);

    private static Vector4 Rgb(byte r, byte g, byte b) => new(r / 255f, g / 255f, b / 255f, 1f);

    public static IDisposable Push()
    {
        var colors = 0;
        var vars = 0;

        void Color(ImGuiCol col, uint value)
        {
            ImGui.PushStyleColor(col, value);
            colors++;
        }

        void Var(ImGuiStyleVar var, float value)
        {
            ImGui.PushStyleVar(var, value);
            vars++;
        }

        void Var2(ImGuiStyleVar var, Vector2 value)
        {
            ImGui.PushStyleVar(var, value);
            vars++;
        }

        Color(ImGuiCol.WindowBg, WindowBg);
        Color(ImGuiCol.ChildBg, Surface);
        Color(ImGuiCol.Button, Surface);
        Color(ImGuiCol.ButtonHovered, SurfaceAlt);
        Color(ImGuiCol.ButtonActive, Accent);
        Color(ImGuiCol.FrameBg, Surface);
        Color(ImGuiCol.FrameBgHovered, SurfaceAlt);
        Color(ImGuiCol.FrameBgActive, Accent);
        Color(ImGuiCol.CheckMark, Accent);
        Color(ImGuiCol.Tab, Surface);
        Color(ImGuiCol.TabHovered, SurfaceAlt);
        Color(ImGuiCol.TabActive, Accent);
        Color(ImGuiCol.TableHeaderBg, Surface);
        Color(ImGuiCol.TableRowBg, Surface);
        Color(ImGuiCol.TableRowBgAlt, SurfaceAlt);
        Color(ImGuiCol.Border, Border);
        Color(ImGuiCol.Text, Text);
        Color(ImGuiCol.TextDisabled, TextDim);

        Var(ImGuiStyleVar.FrameRounding, 4.0f);
        Var(ImGuiStyleVar.TabRounding, 4.0f);
        Var(ImGuiStyleVar.PopupRounding, 4.0f);
        Var(ImGuiStyleVar.ScrollbarRounding, 4.0f);
        Var(ImGuiStyleVar.GrabRounding, 4.0f);
        Var(ImGuiStyleVar.FrameBorderSize, 1.0f);
        Var(ImGuiStyleVar.WindowRounding, 5.0f);
        Var2(ImGuiStyleVar.WindowPadding, new Vector2(10, 10));
        Var2(ImGuiStyleVar.FramePadding, new Vector2(6, 4));
        Var2(ImGuiStyleVar.ItemSpacing, new Vector2(8, 6));
        Var2(ImGuiStyleVar.CellPadding, new Vector2(8, 4));

        return new PopScope(colors, vars);
    }

    public static IDisposable PrimaryButton()
    {
        ImGui.PushStyleColor(ImGuiCol.Button, Accent);
        return new PopScope(colors: 1);
    }

    private sealed class PopScope(int colors, int vars = 0) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            if (colors > 0)
                ImGui.PopStyleColor(colors);
            if (vars > 0)
                ImGui.PopStyleVar(vars);

            _disposed = true;
        }
    }
}
