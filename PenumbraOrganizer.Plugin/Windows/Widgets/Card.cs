using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace PenumbraOrganizer.Plugin.Windows.Widgets;

/// <summary>
/// A rounded, tinted child region with a thin border and inner padding, as tall as its content.
/// <c>using var card = Card.Begin("##id");</c> ends it on every path, early returns included.
/// </summary>
/// <remarks>
/// Dalamud's ImGui binding has no child auto-resize flag (no ImGuiChildFlags), so the card remembers where its
/// content ended last frame and opens at that height (<see cref="WidgetLayout.CardHeight"/>); the first frame of a
/// new card, or a frame where its content grows, is one frame short. A card scrolled out of view keeps its last
/// height (<see cref="WidgetLayout.CardContentBottom"/>), so the page never shrinks under the scroll position. The
/// wheel scrolls the page, not the card.
/// </remarks>
public static class Card
{
    private static readonly Dictionary<string, float> Heights = new(StringComparer.Ordinal);

    // How many cards are open around the one being begun: a top-level card keeps a right gutter, a nested one fills
    // its parent (WidgetLayout.CardWidth). Draw-thread only, like every ImGui call.
    private static int _depth;

    public static CardScope Begin(string id)
    {
        Heights.TryGetValue(id, out var remembered);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, PluginTheme.CardBackground);
        ImGui.PushStyleColor(ImGuiCol.Border, PluginTheme.CardBorder);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, PluginTheme.CardRounding);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, PluginTheme.CardPadding);
        var visible = ImGui.BeginChild(
            id, new Vector2(WidgetLayout.CardWidth(_depth), WidgetLayout.CardHeight(remembered, PluginTheme.CardPadding.Y)), true,
            ImGuiWindowFlags.AlwaysUseWindowPadding | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        // Read by BeginChild itself, so they can go now; children inside the card keep the normal style.
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(2);
        _depth++;
        return new CardScope(id, visible);
    }

    internal static void Remember(string id, float contentBottom, bool drawn)
    {
        _depth = Math.Max(0, _depth - 1);
        Heights.TryGetValue(id, out var remembered);
        Heights[id] = WidgetLayout.CardContentBottom(drawn, remembered, contentBottom);
    }
}

/// <summary>Ends a <see cref="Card"/>. A class, so a second Dispose cannot end the child twice.</summary>
public sealed class CardScope : IDisposable
{
    private readonly string _id;
    private bool _ended;

    internal CardScope(string id, bool visible)
    {
        _id = id;
        Visible = visible;
    }

    /// <summary>False when the card is clipped; drawing inside may be skipped.</summary>
    public bool Visible { get; }

    public static implicit operator bool(CardScope scope) => scope.Visible;

    public void Dispose()
    {
        if (_ended)
            return;
        _ended = true;
        // The cursor's local Y is where the content ended (it already includes the top padding). A clipped card drew
        // nothing, so its cursor says nothing about its height: it keeps the last measurement (no scroll jump).
        Card.Remember(_id, ImGui.GetCursorPosY(), Visible);
        ImGui.EndChild(); // BeginChild needs EndChild whatever it returned
    }
}
