using PenumbraOrganizer.Plugin.Organizer.BodyTypes;
using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Classification;

public class BodyPriorityResolverTests
{
    private static readonly string[] All = ["Bibo+", "Rue+", "TBSE", "YAB"];

    private static BodyCandidate C(string label, BodyEvidenceSource source = BodyEvidenceSource.Option) => new(label, source);

    private static BodyPriority Order(params string[] saved) => BodyPriority.Normalize(saved, All);

    [Fact]
    public void NullCandidates_NoAnswer() =>
        Assert.Null(BodyPriorityResolver.Resolve(null, Order()));

    [Fact]
    public void SingleCandidate_Wins()
    {
        var r = BodyPriorityResolver.Resolve(new BodyCandidates { Content = [C("TBSE")] }, Order());

        Assert.Equal(new BodyResolution("TBSE", BodyEvidenceTier.Content, BodyEvidenceSource.Option), r);
    }

    [Fact]
    public void SeveralCandidates_HighestRankedWins_AndReorderingChangesTheWinner()
    {
        var candidates = new BodyCandidates { Content = [C("Rue+"), C("TBSE")] };

        Assert.Equal("Rue+", BodyPriorityResolver.Resolve(candidates, Order("Rue+"))!.Label);
        Assert.Equal("TBSE", BodyPriorityResolver.Resolve(candidates, Order("TBSE"))!.Label);
    }

    [Fact]
    public void ContentOutranksName_EvenWhenTheNamedBodyRanksHigher()
    {
        var candidates = new BodyCandidates
        {
            Content = [C("Rue+")],
            Name = [C("Bibo+", BodyEvidenceSource.Name)],
        };

        var r = BodyPriorityResolver.Resolve(candidates, Order("Bibo+"))!;

        Assert.Equal("Rue+", r.Label);
        Assert.Equal(BodyEvidenceTier.Content, r.Tier);
    }

    [Fact]
    public void FallsThroughContentThenNameThenLibraryPath()
    {
        var nameOnly = new BodyCandidates { Name = [C("YAB", BodyEvidenceSource.Name)] };
        var pathOnly = new BodyCandidates { LibraryPath = [C("Bibo+", BodyEvidenceSource.LibraryPath)] };

        Assert.Equal(BodyEvidenceTier.Name, BodyPriorityResolver.Resolve(nameOnly, Order())!.Tier);
        Assert.Equal(BodyEvidenceTier.LibraryPath, BodyPriorityResolver.Resolve(pathOnly, Order())!.Tier);
    }

    [Fact]
    public void NameBeatsLibraryPath()
    {
        var both = new BodyCandidates
        {
            Name = [C("YAB", BodyEvidenceSource.Name)],
            LibraryPath = [C("Bibo+", BodyEvidenceSource.LibraryPath)],
        };

        Assert.Equal("YAB", BodyPriorityResolver.Resolve(both, Order("Bibo+"))!.Label);
    }

    [Fact]
    public void NothingFound_NoAnswer_EvenWhenTheReadFailed()
    {
        Assert.Null(BodyPriorityResolver.Resolve(new BodyCandidates(), Order()));
        Assert.Null(BodyPriorityResolver.Resolve(new BodyCandidates { ContentReadFailed = true }, Order()));
    }

    [Fact]
    public void FailedContentRead_StillUsesTheNameTier()
    {
        var candidates = new BodyCandidates
        {
            ContentReadFailed = true,
            Name = [C("Bibo+", BodyEvidenceSource.Name)],
        };

        Assert.Equal("Bibo+", BodyPriorityResolver.Resolve(candidates, Order())!.Label);
    }

    [Fact]
    public void LabelsUnknownToThePriority_TieBreakByOrdinalLabel_NotInputOrder()
    {
        var a = new BodyCandidates { Content = [C("Zeta"), C("Alpha")] };
        var b = new BodyCandidates { Content = [C("Alpha"), C("Zeta")] };

        Assert.Equal("Alpha", BodyPriorityResolver.Resolve(a, Order())!.Label);
        Assert.Equal("Alpha", BodyPriorityResolver.Resolve(b, Order())!.Label);
    }

    [Fact]
    public void BodyMod_NameOutranksContent_BecauseItsOwnNameSaysWhichBodyItIs()
    {
        // "Yet Another Body+" ships a Bibo+ compatibility option group; it is still a YAB mod.
        var candidates = new BodyCandidates
        {
            Content = [C("Bibo+")],
            Name = [C("YAB", BodyEvidenceSource.Name)],
        };

        var r = BodyPriorityResolver.Resolve(candidates, Order("Bibo+"), nameFirst: true)!;

        Assert.Equal(new BodyResolution("YAB", BodyEvidenceTier.Name, BodyEvidenceSource.Name), r);
    }

    [Fact]
    public void BodyMod_WithNoNameEvidence_StillFallsBackToContent()
    {
        var candidates = new BodyCandidates { Content = [C("Bibo+")] };

        Assert.Equal("Bibo+", BodyPriorityResolver.Resolve(candidates, Order(), nameFirst: true)!.Label);
    }
}
