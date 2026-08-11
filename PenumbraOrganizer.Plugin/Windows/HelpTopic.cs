namespace PenumbraOrganizer.Plugin.Windows;

/// <summary>
/// A typed reference to one entry in <c>help-content.json</c>.
/// </summary>
/// <remarks>
/// A struct wrapping a string rather than a bare string, so a call site cannot pass an arbitrary
/// literal: every reference has to come from <see cref="HelpTopics"/>, and a mistyped id is a
/// compile error instead of a tooltip that silently never appears.
/// </remarks>
public readonly record struct HelpTopic(string Id);
