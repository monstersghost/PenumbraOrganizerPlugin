using PenumbraOrganizer.Plugin.Windows;

namespace PenumbraOrganizer.Plugin.Tests.Windows;

public class ProtectToggleLabelsTests
{
    [Fact]
    public void All_NamesTheActionAClickWillTake()
    {
        Assert.Equal("Protect all", ProtectToggleLabels.All(allProtected: false));
        Assert.Equal("Unprotect all", ProtectToggleLabels.All(allProtected: true));
    }
}
