using Dalamud.Bindings.ImGui;

namespace PenumbraOrganizer.Plugin.Windows;

public static class PluginTheme
{
    private const uint Accent = 0xFFF16663;      // #6366F1
    private const uint AccentHover = 0xFFF57D7A; // #7A7DF5
    private const uint WindowBg = 0xFF241D1B;    // #1B1D24
    private const uint Surface = 0xFF2F2623;     // #23262F
    private const uint SurfaceAlt = 0xFF3A2E2A;  // #2A2E3A
    private const uint Border = 0xFF4A3C38;      // #383C4A
    private const uint Text = 0xFFF3E9E7;        // #E7E9F3
    private const uint TextDim = 0xFFB8A39C;     // #9CA3B8

    private const int ColorCount = 18;
    private const int StyleVarCount = 6;

    public static IDisposable Push()
    {
        ImGui.PushStyleColor(ImGuiCol.WindowBg, WindowBg);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Surface);
        ImGui.PushStyleColor(ImGuiCol.Button, Surface);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, SurfaceAlt);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Accent);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Surface);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, SurfaceAlt);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Accent);
        ImGui.PushStyleColor(ImGuiCol.CheckMark, Accent);
        ImGui.PushStyleColor(ImGuiCol.Tab, Surface);
        ImGui.PushStyleColor(ImGuiCol.TabHovered, SurfaceAlt);
        ImGui.PushStyleColor(ImGuiCol.TabActive, Accent);
        ImGui.PushStyleColor(ImGuiCol.TableHeaderBg, Surface);
        ImGui.PushStyleColor(ImGuiCol.TableRowBg, Surface);
        ImGui.PushStyleColor(ImGuiCol.TableRowBgAlt, SurfaceAlt);
        ImGui.PushStyleColor(ImGuiCol.Border, Border);
        ImGui.PushStyleColor(ImGuiCol.Text, Text);
        ImGui.PushStyleColor(ImGuiCol.TextDisabled, TextDim);

        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.TabRounding, 4.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 4.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarRounding, 4.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.GrabRounding, 4.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1.0f);

        return new PopScope(ColorCount, StyleVarCount);
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
