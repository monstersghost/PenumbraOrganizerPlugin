using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Core.Services;

namespace PenumbraOrganizer.Plugin.Tests;

public class LinkedFilesTests
{
    [Fact]
    public void ModCategory_HasExpectedValue()
    {
        Assert.Equal(4, (int)ModCategory.Hair);
    }

    [Fact]
    public void CreatorCanonicalizer_MergesKnownAlias()
    {
        var canonicalizer = new CreatorCanonicalizer();
        Assert.Equal("Enni", canonicalizer.Canonicalize("enni"));
    }
}
