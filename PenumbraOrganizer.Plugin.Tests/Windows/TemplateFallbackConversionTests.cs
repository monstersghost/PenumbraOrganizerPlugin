namespace PenumbraOrganizer.Plugin.Tests.Windows;

using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Templates;
using PenumbraOrganizer.Plugin.Windows;

/// <summary>
/// <see cref="SortSelection"/> and <see cref="TemplateFallback"/> are structurally identical and stay
/// separate on purpose: one belongs to the Sort tab, the other is part of the on-disk template
/// format, and merging them would make the Templates domain depend on the Windows namespace.
/// </summary>
/// <remarks>
/// The risk that justifies these tests is narrow but real: if either type gains a field, a conversion
/// that silently drops it would publish a fallback the author did not choose. The property-count
/// assertion fails the moment that happens, which is the point - it is a tripwire, not a behaviour
/// test.
/// </remarks>
public class TemplateFallbackConversionTests
{
    [Theory]
    [InlineData(SortStrategy.CreatorOnly, true, true)]
    [InlineData(SortStrategy.TypeOnly, false, true)]
    [InlineData(SortStrategy.TypeThenCreator, true, false)]
    [InlineData(SortStrategy.CreatorThenType, false, false)]
    public void ToTemplateFallback_CarriesEveryField(SortStrategy strategy, bool splitGear, bool splitNpc)
    {
        var converted = MainWindow.ToTemplateFallback(new SortSelection(strategy, splitGear, splitNpc, SplitHair: false));

        Assert.Equal(new TemplateFallback(strategy, splitGear, splitNpc), converted);
    }

    // If one side gains a field the conversion above stops being total, and a dropped field means a
    // published template describes a layout the author did not pick.
    //
    // Counts the record's primary-constructor parameters, not its properties: SortSelection also
    // exposes computed properties, so a property count would not mean the same thing.
    //
    // SortSelection carries ten values; TemplateFallback stores six: grouping, the gear-slot and
    // NPC splits, and (added later, optional in the file) the hair split and the two body splits.
    // The tenth, Layout, holds the two layout options and is NOT stored in templates.
    // The hair race, Midlander fallback and body priority are NOT stored: they are the importer's
    // own settings, so a shared template never re-folders a recipient by someone else's order.
    // Adding a seventh value on either side must be a visible decision, which this count enforces.
    [Fact]
    public void BothTypes_CarryTheValuesTheyHaveBeenDesignedFor()
    {
        Assert.Equal(10, PrimaryConstructorParameterCount(typeof(SortSelection)));
        Assert.Equal(6, PrimaryConstructorParameterCount(typeof(TemplateFallback)));
    }

    private static int PrimaryConstructorParameterCount(Type type) =>
        type.GetConstructors().Max(constructor => constructor.GetParameters().Length);
}
