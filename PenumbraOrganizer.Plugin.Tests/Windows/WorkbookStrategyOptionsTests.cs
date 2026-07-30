namespace PenumbraOrganizer.Plugin.Tests.Windows;

using System.Reflection;
using PenumbraOrganizer.Core.Models;

public class WorkbookStrategyOptionsTests
{
    // The dropdown array is private static and MainWindow cannot be constructed without a live
    // Dalamud, so this reads the field reflectively rather than instantiating the window.
    private static (string Label, OrganizationStrategy Strategy)[] ReadOptions()
    {
        var field = typeof(PenumbraOrganizer.Plugin.Windows.MainWindow)
            .GetField("WorkbookStrategyOptions", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("WorkbookStrategyOptions not found.");
        return ((string, OrganizationStrategy)[])field.GetValue(null)!;
    }

    [Fact]
    public void Options_OfferKeepCurrentFolders()
    {
        var options = ReadOptions();
        var asIs = Assert.Single(options, option => option.Strategy == OrganizationStrategy.PreserveAndClean);
        Assert.Equal("Keep current folders (as-is)", asIs.Label);
    }

    [Fact]
    public void DefaultIndex_StillSelectsTypeThenCreator()
    {
        // The as-is entry must be appended, not inserted: _workbookStrategyIndex defaults to 2.
        Assert.Equal(OrganizationStrategy.TypeThenCreator, ReadOptions()[2].Strategy);
    }
}
