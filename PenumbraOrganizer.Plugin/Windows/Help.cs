using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Bindings.ImGui;

namespace PenumbraOrganizer.Plugin.Windows;

/// <summary>One entry in <c>help-content.json</c>.</summary>
/// <remarks>
/// <c>id</c> and <c>title</c> are required; <c>short</c>, <c>body</c> and <c>step</c> are each
/// optional and a topic must carry at least one. Validation is by consumer: a tooltip needs
/// <c>short</c>, a Help section needs <c>body</c>, a walkthrough step needs <c>step</c>.
/// </remarks>
public sealed class HelpTopicEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("short")] public string? Short { get; set; }
    [JsonPropertyName("body")] public string? Body { get; set; }
    [JsonPropertyName("step")] public string? Step { get; set; }
}

public sealed class HelpContentDocument
{
    [JsonPropertyName("topics")] public List<HelpTopicEntry> Topics { get; set; } = [];
}

/// <summary>
/// The single source of every explanation the plugin shows, at three depths: <c>short</c> for
/// tooltips, <c>body</c> for the Help tab, <c>step</c> for the first-run walkthrough.
/// </summary>
public static class Help
{
    // public, not internal: this repo has no InternalsVisibleTo, so an internal member is
    // unreachable from PenumbraOrganizer.Plugin.Tests and HelpContentTests would not compile.
    public const string ResourceName = "PenumbraOrganizer.Plugin.Resources.help-content.json";

    // How wide a tooltip is allowed to get before wrapping, in pixels. ImGui.SetTooltip does not
    // wrap at all, so without this a long short produces a tooltip wider than the viewport.
    private const float TooltipWrapWidth = 400f;

    // Lazy, not eager: this reads an embedded resource, and a static initializer that throws inside
    // a Dalamud plugin's type load is far harder to diagnose than one that throws on first use.
    private static readonly Lazy<Dictionary<string, HelpTopicEntry>> Entries = new(() =>
        Parse(ReadEmbedded(typeof(Help).Assembly, ResourceName), ResourceName));

    /// <summary>
    /// Reads an embedded resource, or throws naming it. A missing resource is a packaging bug, not
    /// a runtime condition - the same treatment <c>NpcNameListStore</c> gives its seed.
    /// </summary>
    public static string ReadEmbedded(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static Dictionary<string, HelpTopicEntry> Parse(string json, string sourceName)
    {
        var document = JsonSerializer.Deserialize<HelpContentDocument>(json)
            ?? throw new InvalidOperationException($"'{sourceName}' did not parse as help content.");

        var duplicates = document.Topics
            .GroupBy(t => t.Id, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (duplicates.Count > 0)
            throw new InvalidOperationException(
                $"'{sourceName}' declares duplicate topic ids: {string.Join(", ", duplicates)}.");

        return document.Topics.ToDictionary(t => t.Id, StringComparer.Ordinal);
    }

    public static bool TryGet(HelpTopic topic, out HelpTopicEntry entry) =>
        Entries.Value.TryGetValue(topic.Id, out entry!);

    public static string? Title(HelpTopic topic) => TryGet(topic, out var e) ? e.Title : null;

    public static string? Short(HelpTopic topic) => TryGet(topic, out var e) ? e.Short : null;

    public static string? Body(HelpTopic topic) => TryGet(topic, out var e) ? e.Body : null;

    public static string? Step(HelpTopic topic) => TryGet(topic, out var e) ? e.Step : null;

    /// <summary>
    /// Shows this topic's <c>short</c> when the preceding widget is hovered, optionally followed by
    /// why that widget is currently disabled.
    /// </summary>
    /// <remarks>
    /// MUST be called immediately after the widget it describes and OUTSIDE any
    /// <c>BeginDisabled</c>/<c>EndDisabled</c> scope. ImGui is positional: <c>IsItemHovered</c>
    /// binds to whichever item was submitted last, so a call anywhere else attaches the tooltip to
    /// the wrong widget - or to nothing. <c>AllowWhenDisabled</c> is passed here rather than left to
    /// call sites because a disabled control is exactly when its explanation matters most, and
    /// omitting the flag fails silently.
    /// </remarks>
    public static void Tooltip(HelpTopic topic, string? disabledReason = null)
    {
        if (!ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            return;

        var text = Short(topic);
        if (text is null && disabledReason is null)
            return;

        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(TooltipWrapWidth);
        if (text is not null)
            ImGui.TextUnformatted(text);
        if (disabledReason is not null)
            ImGui.TextUnformatted(disabledReason);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }
}
