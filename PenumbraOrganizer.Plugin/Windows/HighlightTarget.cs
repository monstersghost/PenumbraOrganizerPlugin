namespace PenumbraOrganizer.Plugin.Windows;

/// <summary>
/// Something in the main window the first-run walkthrough can point at. The window outlines it and,
/// for a tab, switches to it. Public with no behaviour so <see cref="FirstRunSteps"/> can be tested.
/// </summary>
public enum HighlightTarget
{
    None,
    ScanTab,
    ProtectTab,
    SortTab,
    ReviewTab,
    HistoryTab,
    HelpTab,
    RefreshButton,
    SortButton,
    ApplyButton,
}
