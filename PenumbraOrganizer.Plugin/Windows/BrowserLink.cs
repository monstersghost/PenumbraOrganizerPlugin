namespace PenumbraOrganizer.Plugin.Windows;

/// <summary>
/// Opens a URL in the user's default browser.
/// </summary>
/// <remarks>
/// Uses the OS handler rather than any in-game browser: Dalamud plugins have no way to open a
/// page in game, and silently doing nothing would look like a broken button.
/// Extracted from <see cref="HelpTab"/> once the title bar's link buttons became a second caller.
/// </remarks>
public static class BrowserLink
{
    public static void Open(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // A failed browser launch must not take the draw call with it - this runs inside the
            // frame, and an exception here would tear down the whole window.
            Plugin.Log.Warning(ex, $"Could not open {url} in a browser.");
        }
    }
}
