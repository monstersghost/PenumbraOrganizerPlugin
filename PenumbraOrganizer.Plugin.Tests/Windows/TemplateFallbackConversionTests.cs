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
    // SortSelection now carries the hair split and race priority, which the on-disk template format
    // deliberately does not store. The conversion drops those three fields on purpose; this count is
    // what makes adding a fourth value a visible decision rather than a silent one.
    [Fact]
    public void BothTypes_CarryTheValuesTheyHaveBeenDesignedFor()
    {
        Assert.Equal(6, PrimaryConstructorParameterCount(typeof(SortSelection)));
        Assert.Equal(3, PrimaryConstructorParameterCount(typeof(TemplateFallback)));
    }

    private static int PrimaryConstructorParameterCount(Type type) =>
        type.GetConstructors().Max(constructor => constructor.GetParameters().Length);
}
