namespace PenumbraOrganizer.Plugin.Tests.Organizer.Templates;

using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Templates;

public class TemplatePlannerFromDecodedTests
{
    private static OrganizerModRow Row(string identifier, string name) => new()
    {
        Identifier = identifier,
        Name = name,
        Author = "Tsar",
        CurrentPath = name,
        ProposedPath = name,
        Category = ModCategory.Gear,
    };

    private static string Same(string value) => value;

    // A document with a bad folder produces a decode warning. Routing through PlanFromDecoded
    // makes it impossible for a caller to forget to pass those warnings along.
    [Fact]
    public void PlanFromDecoded_CarriesDecodeWarningsIntoThePlan()
    {
        var decoded = TemplateCodec.DecodeJson(
            """
            {"formatVersion":1,"name":"x","fallbackStrategy":"ModType",
             "folders":["Gear//Bad"],"entries":[{"n":"some mod","f":"Gear"}]}
            """);
        Assert.True(decoded.Succeeded);

        var plan = TemplatePlanner.PlanFromDecoded(decoded, [Row("id1", "Some Mod")], Same);

        Assert.Contains(plan.Warnings, w => w.Code == TemplateWarningCode.InvalidFolderPath);
    }

    [Fact]
    public void PlanFromDecoded_ProducesTheSamePlacementsAsPlan()
    {
        var decoded = TemplateCodec.DecodeJson(
            """
            {"formatVersion":1,"name":"x","fallbackStrategy":"ModType",
             "entries":[{"n":"some mod","f":"Characters/Nyx"}]}
            """);
        OrganizerModRow[] Rows() => [Row("id1", "Some Mod"), Row("id2", "Other Mod")];

        var viaHelper = TemplatePlanner.PlanFromDecoded(decoded, Rows(), Same);
        var viaPlan = TemplatePlanner.Plan(decoded.Template!, Rows(), Same, decoded.Warnings);

        Assert.Equal(viaPlan.DestinationFolders, viaHelper.DestinationFolders);
        Assert.Equal(viaPlan.Report, viaHelper.Report);
    }

    // Planning against a failed decode is a caller bug, not a user-facing condition: the UI must
    // surface the decode error instead of planning at all.
    [Fact]
    public void PlanFromDecoded_FailedDecode_Throws()
    {
        var decoded = TemplateCodec.DecodeJson("{ not json");
        Assert.False(decoded.Succeeded);

        Assert.Throws<ArgumentException>(
            () => TemplatePlanner.PlanFromDecoded(decoded, [], Same));
    }
}
