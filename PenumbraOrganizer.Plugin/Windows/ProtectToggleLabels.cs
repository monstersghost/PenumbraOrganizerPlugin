namespace PenumbraOrganizer.Plugin.Windows;

/// <summary>
/// The Protect tab's two bulk buttons used to read "Toggle ...", which does not say which way. They
/// now name the action a click performs, chosen from the same state the click already uses.
/// </summary>
/// <remarks>public, not internal: this repo has no InternalsVisibleTo.</remarks>
public static class ProtectToggleLabels
{
    public static string All(bool allProtected) => allProtected ? "Unprotect all" : "Protect all";
}
