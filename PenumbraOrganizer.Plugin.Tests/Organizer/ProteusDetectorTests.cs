using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class ProteusDetectorTests
{
    [Theory]
    [InlineData("refit-abc", "Proteus", ProteusRole.Made)]
    [InlineData("anything", "Proteus", ProteusRole.Made)]
    [InlineData("refit-abc", "proteus", ProteusRole.None)]   // Proteus's own check is ordinal
    [InlineData("refit-abc", "PROTEUS", ProteusRole.None)]
    [InlineData("refit-abc", "Proteus ", ProteusRole.None)]
    [InlineData("refit-abc", "Solona", ProteusRole.None)]
    [InlineData("refit-abc", "", ProteusRole.None)]
    public void Classify_MadeMods_AreExactlyTheOnesAuthoredByProteus(string dir, string author, ProteusRole expected) =>
        Assert.Equal(expected, ProteusDetector.Classify(dir, author));

    [Fact]
    public void Classify_NullAuthor_IsNone() =>
        Assert.Equal(ProteusRole.None, ProteusDetector.Classify("refit-abc", null));

    [Theory]
    [InlineData("Proteus")]
    [InlineData("proteus")]
    [InlineData("PROTEUS")]
    public void Classify_TheManagedMod_NeedsTheDirectoryNameAndTheAuthor(string dir) =>
        Assert.Equal(ProteusRole.Managed, ProteusDetector.Classify(dir, "Proteus"));

    [Fact]
    public void Classify_ADirectoryCalledProteusWithAnotherAuthor_IsNotFlagged()
    {
        Assert.Equal(ProteusRole.None, ProteusDetector.Classify("Proteus", "Someone Else"));
        Assert.Equal(ProteusRole.None, ProteusDetector.Classify("Proteus", null));
    }
}
