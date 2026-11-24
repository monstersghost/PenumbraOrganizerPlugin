using PenumbraOrganizer.Plugin.Organizer.Layouts;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Layouts;

public class LayoutFolderNameTests
{
    [Theory]
    [InlineData("Gear")]
    [InlineData("Equipment/Gear")]
    [InlineData("A/B/C/D")]
    [InlineData("  Equipment/Gear  ")]
    [InlineData("Equipment / Gear")]
    public void ValidNames_HaveNoError(string raw) => Assert.Null(LayoutFolderName.Error(raw));

    [Theory]
    [InlineData(@"Equipment\Gear", "Equipment/Gear")]
    [InlineData("/Equipment/Gear/", "Equipment/Gear")]
    [InlineData(" Gear ", "Gear")]
    public void Normalize_ReadsBackslashesAsSlashes_AndTrims(string raw, string expected) =>
        Assert.Equal(expected, LayoutFolderName.Normalize(raw));

    [Theory]
    [InlineData("Equipment / Gear", "Equipment/Gear")]
    [InlineData(" Equipment /  Gear ", "Equipment/Gear")]
    [InlineData(@"Looks \ Hair", "Looks/Hair")]
    [InlineData("A /B/ C", "A/B/C")]
    [InlineData("Gear / ", "Gear")]
    public void Normalize_TrimsEverySegment_AsPenumbraDoes(string raw, string expected) =>
        Assert.Equal(expected, LayoutFolderName.Normalize(raw));

    [Fact]
    public void Normalize_OfNull_IsEmpty() => Assert.Equal(string.Empty, LayoutFolderName.Normalize(null));

    [Theory]
    [InlineData("", LayoutFolderName.EmptyError)]
    [InlineData("   ", LayoutFolderName.EmptyError)]
    [InlineData("/", LayoutFolderName.EmptyError)]
    [InlineData(" / ", LayoutFolderName.EmptyError)]
    [InlineData(null, LayoutFolderName.EmptyError)]
    [InlineData("Equipment//Gear", "Destination cannot contain empty segments, . or ..")]
    [InlineData("Equipment/ /Gear", "Destination cannot contain empty segments, . or ..")]
    [InlineData("Equipment/../Gear", "Destination cannot contain empty segments, . or ..")]
    [InlineData("Equipment/ .. /Gear", "Destination cannot contain empty segments, . or ..")]
    [InlineData("..", "Destination cannot contain empty segments, . or ..")]
    [InlineData("C:/Gear", "Use a relative Penumbra folder path.")]
    [InlineData("Mods/{creator}", LayoutFolderName.BracesError)]
    [InlineData("Mods/}", LayoutFolderName.BracesError)]
    public void InvalidNames_SayWhy(string? raw, string expected) => Assert.Equal(expected, LayoutFolderName.Error(raw));

    [Fact]
    public void TheLengthCapIs260()
    {
        Assert.Null(LayoutFolderName.Error(new string('a', 260)));
        Assert.Equal("Destination is longer than 260 characters.", LayoutFolderName.Error(new string('a', 261)));
    }

    [Fact]
    public void TheLengthCapCountsTheNormalizedName()
    {
        Assert.Null(LayoutFolderName.Error(new string('a', 130) + " / " + new string('b', 129)));
    }
}
