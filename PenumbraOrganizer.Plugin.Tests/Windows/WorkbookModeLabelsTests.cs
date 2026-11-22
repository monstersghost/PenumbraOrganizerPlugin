using PenumbraOrganizer.Plugin.Windows;

namespace PenumbraOrganizer.Plugin.Tests.Windows;

public class WorkbookModeLabelsTests
{
    // The combo index is the mode (0 As is, 1 As suggested by Sort), and As is must stay the default.
    [Fact]
    public void TheLabelsAreInModeOrder() =>
        Assert.Equal(["As is (current folders)", "As suggested by Sort"], MainWindow.WorkbookModeLabels);
}
