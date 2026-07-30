namespace PenumbraOrganizer.Plugin.Tests.Organizer.Templates;

using PenumbraOrganizer.Plugin.Organizer.Templates;

public class TemplatePathValidatorTests
{
    [Theory]
    [InlineData("")]              // root is valid, matching the workbook's folder-only convention
    [InlineData("Gear")]
    [InlineData("Gear/Head")]
    [InlineData("Animation and VFX/Emotes")]
    [InlineData("_Unsorted")]
    [InlineData("Café")]
    public void IsValidFolder_WellFormed_ReturnsTrue(string folder)
    {
        Assert.True(TemplatePathValidator.IsValidFolder(folder));
    }

    [Theory]
    [InlineData("/Gear")]         // leading separator
    [InlineData("Gear/")]         // trailing separator
    [InlineData("Gear//Head")]    // empty segment
    [InlineData("Gear/ /Head")]   // whitespace-only segment
    [InlineData("GearHead")]  // control character
    [InlineData("Gear\nHead")]
    public void IsValidFolder_Malformed_ReturnsFalse(string folder)
    {
        Assert.False(TemplatePathValidator.IsValidFolder(folder));
    }

    [Fact]
    public void IsValidFolder_AtDepthLimit_ReturnsTrue()
    {
        var folder = string.Join('/', Enumerable.Repeat("a", TemplateLimits.MaxPathDepth));

        Assert.True(TemplatePathValidator.IsValidFolder(folder));
    }

    [Fact]
    public void IsValidFolder_OverDepthLimit_ReturnsFalse()
    {
        var folder = string.Join('/', Enumerable.Repeat("a", TemplateLimits.MaxPathDepth + 1));

        Assert.False(TemplatePathValidator.IsValidFolder(folder));
    }

    [Fact]
    public void IsValidFolder_OverSegmentLengthLimit_ReturnsFalse()
    {
        var folder = new string('a', TemplateLimits.MaxSegmentLength + 1);

        Assert.False(TemplatePathValidator.IsValidFolder(folder));
    }

    [Fact]
    public void IsValidFolder_OverTotalStringLimit_ReturnsFalse()
    {
        var folder = string.Join('/', Enumerable.Repeat(new string('a', 100), 8));

        Assert.True(folder.Length > TemplateLimits.MaxStringLength);
        Assert.False(TemplatePathValidator.IsValidFolder(folder));
    }
}
