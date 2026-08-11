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
        var converted = MainWindow.ToTemplateFallback(new SortSelection(strategy, splitGear, splitNpc));

        Assert.Equal(new TemplateFallback(strategy, splitGear, splitNpc), converted);
    }

    // If one side gains a field the conversion above stops being total, and a dropped field means a
    // published template describes a layout the author did not pick.
    //
    // Counts the record's primary-constructor parameters, not its properties: SortSelection also
    // exposes the computed SplitsApply, so a property count would be 4 against 3 and would have to
    // be written as two different magic numbers that no longer mean the same thing.
    [Fact]
    public void BothTypes_StillCarryExactlyThreeValues()
    {
        Assert.Equal(3, PrimaryConstructorParameterCount(typeof(SortSelection)));
        Assert.Equal(3, PrimaryConstructorParameterCount(typeof(TemplateFallback)));
    }

    private static int PrimaryConstructorParameterCount(Type type) =>
        type.GetConstructors().Max(constructor => constructor.GetParameters().Length);
}
