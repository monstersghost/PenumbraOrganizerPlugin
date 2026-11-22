using PenumbraOrganizer.Plugin.Organizer.Workbook;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Rules;

public class RuleFolderTests
{
    private static readonly DestinationTokens Tokens = new("Ann", "Body", "Viera");

    [Fact]
    public void Tokens_expand_and_a_literal_first_segment_is_not_a_code_or_name()
    {
        Assert.Equal("17/x/Ann", DestinationResolver.ResolveRuleFolder("17/x/{creator}", Tokens).Destination);
        Assert.Equal("vfx/Body", DestinationResolver.ResolveRuleFolder("vfx/{type}", Tokens).Destination);
    }

    [Fact]
    public void Whole_token_segment_that_expands_empty_collapses_and_a_mixed_one_keeps_its_text()
    {
        var none = DestinationTokens.None with { Type = "Body" };
        Assert.Equal("Body/X", DestinationResolver.ResolveRuleFolder("{type}/{race}/X", none).Destination);
        Assert.Equal("By-/X", DestinationResolver.ResolveRuleFolder("By-{creator}/X", none).Destination);
    }

    [Fact]
    public void Expanding_to_nothing_is_an_error()
    {
        var result = DestinationResolver.ResolveRuleFolder("{creator}", DestinationTokens.None);
        Assert.Null(result.Destination);
        Assert.Equal("Folder expands to nothing for this mod.", result.Error);
    }

    [Fact]
    public void Unknown_token_and_stray_braces_are_errors()
    {
        Assert.Equal("unknown token {nope}", DestinationResolver.ResolveRuleFolder("A/{nope}", Tokens).Error);
        Assert.Equal("Folder has an unmatched { or }.", DestinationResolver.ResolveRuleFolder("A/{creator", Tokens).Error);
    }

    [Fact]
    public void Segments_are_trimmed_and_backslashes_normalised_and_token_case_is_ignored()
    {
        Assert.Equal("A/B/Ann", DestinationResolver.ResolveRuleFolder(" A \\ B / {Creator} ", Tokens).Destination);
    }

    [Fact]
    public void Structure_errors_still_apply()
    {
        Assert.NotNull(DestinationResolver.ResolveRuleFolder("../x", Tokens).Error);
        Assert.NotNull(DestinationResolver.ResolveRuleFolder("C:/x", Tokens).Error);
        Assert.NotNull(DestinationResolver.ResolveRuleFolder(new string('a', 300), Tokens).Error);
    }
}
