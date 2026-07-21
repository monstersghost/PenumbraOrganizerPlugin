namespace PenumbraOrganizer.Plugin.Tests.Organizer;

using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Core.Models;
using PenumbraOrganizer.Plugin.Organizer;

public class WorkbookAdapterInventoryTests
{
    private static OrganizerModRow MakeRow(string identifier, string name, string author, string currentPath, ModCategory? category = null) => new()
    {
        Identifier = identifier,
        Name = name,
        Author = author,
        CurrentPath = currentPath,
        ProposedPath = currentPath,
        Category = category,
    };

    private static PenumbraInstallation MakeInstallation() => new(
        ConfigurationPath: "C:/Penumbra/Penumbra.json",
        ConfigDirectory: "C:/Penumbra",
        ModRoot: "C:/Penumbra/Mods",
        PluginAssemblyPath: null,
        PluginManifestPath: null,
        InstalledVersion: null,
        Confidence: DiscoveryConfidence.High,
        Evidence: [],
        Warnings: []);

    [Fact]
    public void ToScanInventory_MapsIdentifierToStableScanId()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeRow("Foo", "Foo Mod", "Author", "Gear/Foo Mod")], new HashSet<string>());

        var inventory = WorkbookAdapter.ToScanInventory(state, MakeInstallation());

        var mod = Assert.Single(inventory.Mods);
        Assert.Equal("Foo", mod.StableScanId);
    }

    [Fact]
    public void ToScanInventory_CurrentVirtualFolderIsFolderOnlySplitOfCurrentPath()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeRow("Foo", "Foo Mod", "Author", "Tsar/Gear/Foo Mod")], new HashSet<string>());

        var inventory = WorkbookAdapter.ToScanInventory(state, MakeInstallation());

        Assert.Equal("Tsar/Gear", inventory.Mods.Single().CurrentVirtualFolder);
    }

    [Fact]
    public void ToScanInventory_NullCategoryMapsToOthers()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeRow("Foo", "Foo Mod", "Author", "Foo Mod", category: null)], new HashSet<string>());

        var inventory = WorkbookAdapter.ToScanInventory(state, MakeInstallation());

        Assert.Equal(ModCategory.Others, inventory.Mods.Single().DetectedCategory);
    }

    [Fact]
    public void ToProposals_CarriesStableScanIdAndProtected()
    {
        var state = new OrganizerState();
        state.LoadScan([MakeRow("Foo", "Foo Mod", "Author", "Gear/Foo Mod")], new HashSet<string> { "Foo" });

        var proposals = WorkbookAdapter.ToProposals(state);

        var proposal = Assert.Single(proposals);
        Assert.Equal("Foo", proposal.StableScanId);
        Assert.True(proposal.Protected);
    }

    [Theory]
    [InlineData(OrganizationStrategy.TypeOnly)]
    [InlineData(OrganizationStrategy.CreatorOnly)]
    [InlineData(OrganizationStrategy.TypeThenCreator)]
    [InlineData(OrganizationStrategy.CreatorThenType)]
    public void ToOrganizationPreferences_CarriesRequestedStrategy(OrganizationStrategy strategy)
    {
        var preferences = WorkbookAdapter.ToOrganizationPreferences(strategy);

        Assert.Equal(strategy, preferences.Strategy);
    }
}
