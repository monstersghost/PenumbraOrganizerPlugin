using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer.BodyTypes;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Classification;

public class BodyCandidateBuilderTests
{
    private static readonly BodyTypeMatcher Matcher = new(
    [
        new BodyTypeEntry { Label = "Bibo+", Aliases = ["bibo+", "bibo"] },
        new BodyTypeEntry { Label = "Rue+", Aliases = ["rue+", "rue"] },
        new BodyTypeEntry { Label = "YAB", Aliases = ["yab", "yab+"] },
        new BodyTypeEntry { Label = "TBSE", Aliases = ["tbse"] },
    ]);

    private static ModContent Content(string[]? groups = null, string[]? options = null, string[]? values = null) =>
        new(new HashSet<EquipmentSlot>(),
            new ModHairFileReader.HairEvidence(new HashSet<int>(),
                new Dictionary<string, IReadOnlySet<int>>()),
            groups ?? [], options ?? [], values ?? []);

    private static string[] Labels(IReadOnlyList<BodyCandidate> list) => [.. list.Select(c => c.Label)];

    [Fact]
    public void Content_FindsBodiesInGroupAndOptionNames()
    {
        var result = BodyCandidateBuilder.Build(Matcher, "Corset", "Corset",
            Content(groups: ["Installation (Bibo+)"], options: ["Rue+", "TBSE"]), contentReadFailed: false);

        Assert.Equal(["Bibo+", "Rue+", "TBSE"], Labels(result.Content));
        Assert.Equal(BodyEvidenceSource.Group, result.Content[0].Source);
        Assert.Equal(BodyEvidenceSource.Option, result.Content[1].Source);
    }

    [Theory]
    [InlineData("Prestige YAB+/top.mdl", true)]
    [InlineData("Bibo+/chest.tex", true)]
    [InlineData("shared/Bibo/file.tex", false)]
    [InlineData("common/textures/rue.tex", false)]
    [InlineData("rue_top.mdl", false)]
    public void FilePathRule_OnlyTheFirstFolderCounts(string value, bool counts)
    {
        var result = BodyCandidateBuilder.Build(Matcher, "X", "X", Content(values: [value]), false);

        Assert.Equal(counts, result.Content.Count == 1);
        if (counts)
            Assert.Equal(BodyEvidenceSource.FilePath, result.Content[0].Source);
    }

    [Fact]
    public void FirstFolder_NoFolderOrEmptyFirstSegment_IsNull()
    {
        Assert.Equal("Rue+", BodyCandidateBuilder.FirstFolder("Rue+/a/b.mdl"));
        Assert.Null(BodyCandidateBuilder.FirstFolder("file.mdl"));
        Assert.Null(BodyCandidateBuilder.FirstFolder("/file.mdl"));
    }

    [Fact]
    public void SameBodyFromSeveralSources_IsOneCandidate_FirstSourceWins()
    {
        var result = BodyCandidateBuilder.Build(Matcher, "X", "X",
            Content(groups: ["Rue+"], options: ["Rue+"], values: ["Rue+/a.mdl"]), false);

        Assert.Equal(["Rue+"], Labels(result.Content));
        Assert.Equal(BodyEvidenceSource.Group, result.Content[0].Source);
    }

    [Fact]
    public void ModTitleInsideContent_IsNotATier1Signal()
    {
        // The walker never reports the title; this pins that the builder does not invent it either.
        var result = BodyCandidateBuilder.Build(Matcher, "Bibo+ Corset Gown", "Bibo+ Corset Gown", Content(), false);

        Assert.Empty(result.Content);
        Assert.Equal(["Bibo+"], Labels(result.Name));
    }

    [Fact]
    public void LibraryPath_UsesFoldersButNotTheModsOwnLeaf()
    {
        var result = BodyCandidateBuilder.Build(Matcher, "Cool Dress", "Heliosphere/Bibo+ Stuff/Cool Dress", Content(), false);

        Assert.Equal(["Bibo+"], Labels(result.LibraryPath));
        Assert.Equal(BodyEvidenceSource.LibraryPath, result.LibraryPath[0].Source);

        var leafOnly = BodyCandidateBuilder.Build(Matcher, "Rue+ Dress", "Rue+ Dress", Content(), false);
        Assert.Empty(leafOnly.LibraryPath);
    }

    [Fact]
    public void LibraryPath_AcceptsBackslashSeparators()
    {
        var result = BodyCandidateBuilder.Build(Matcher, "X", "Folder\\YAB\\X", Content(), false);

        Assert.Equal(["YAB"], Labels(result.LibraryPath));
    }

    [Fact]
    public void NullContent_FlagsFailureOnlyWhenTheReadActuallyFailed()
    {
        var failed = BodyCandidateBuilder.Build(Matcher, "X", "X", null, contentReadFailed: true);
        var notRead = BodyCandidateBuilder.Build(Matcher, "X", "X", null, contentReadFailed: false);

        Assert.True(failed.ContentReadFailed);
        Assert.False(notRead.ContentReadFailed);
        Assert.Empty(failed.Content);
    }

    [Fact]
    public void Describe_ListsEveryTierAndTheReadStatus()
    {
        var result = BodyCandidateBuilder.Build(Matcher, "TBSE Top", "A/B", Content(options: ["Rue+"]), false);

        Assert.Equal("content=[Rue+ (Option)] name=[TBSE (Name)] library=[] contentRead=ok", result.Describe());
        Assert.Contains("contentRead=FAILED",
            BodyCandidateBuilder.Build(Matcher, "X", "X", null, true).Describe());
    }
}
