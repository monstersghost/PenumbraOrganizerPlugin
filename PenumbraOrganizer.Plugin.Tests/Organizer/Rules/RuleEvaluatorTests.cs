using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Rules;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Rules;

public class RuleEvaluatorTests
{
    private static RuleFieldValues Values(string name = "Hrothgar Body", string author = "Ann", string folder = "Old/Stuff",
        string type = "Body", string subtype = "", string race = "", string body = "") =>
        new(name, author, folder, type, subtype, race, body);

    private static bool Eval(string condition, RuleFieldValues v)
    {
        var parsed = RuleParser.Parse(condition + " -> X");
        Assert.True(parsed.Error is null, parsed.Error?.Message);
        return RuleEvaluator.Matches(parsed.Rule!.Condition, v);
    }

    [Theory]
    [InlineData("name contains 'HROTHGAR'", true)]
    [InlineData("name starts with 'hrothgar'", true)]
    [InlineData("name ends with 'BODY'", true)]
    [InlineData("name = 'hrothgar body'", true)]
    [InlineData("name != 'hrothgar body'", false)]
    [InlineData("author in ('bob', 'ANN')", true)]
    [InlineData("author not in ('ann')", false)]
    [InlineData("name contains 'x' or type = 'body'", true)]
    [InlineData("name contains 'x' and type = 'body'", false)]
    [InlineData("not (name contains 'x')", true)]
    public void Operators_are_case_insensitive(string condition, bool expected) =>
        Assert.Equal(expected, Eval(condition, Values()));

    [Fact]
    public void Empty_field_is_the_empty_string()
    {
        var v = Values(race: "", author: "");
        Assert.True(Eval("race = ''", v));
        Assert.False(Eval("race != ''", v));
        Assert.True(Eval("author in ('')", v));
        Assert.False(Eval("race contains 'a'", v));
    }

    [Fact]
    public void Folder_is_the_parent_path_and_empty_at_root()
    {
        var row = new OrganizerModRow
        {
            Identifier = "a", Name = "Mod", Author = "Ann", CurrentPath = "Gear\\Tops/Mod", ProposedPath = "x",
            Category = ModCategory.Gear,
        };
        var v = RuleFields.For(row, s => s, RaceSplitOptions.Off, null);
        Assert.Equal("Gear/Tops", v.Folder);

        var root = new OrganizerModRow
        {
            Identifier = "b", Name = "Mod", Author = "Ann", CurrentPath = "Mod", ProposedPath = "x",
        };
        Assert.Equal("", RuleFields.For(root, s => s, RaceSplitOptions.Off, null).Folder);
        Assert.Equal("Review", RuleFields.For(root, s => s, RaceSplitOptions.Off, null).Type);
    }

    [Fact]
    public void Type_merges_vfx_into_animation_and_vfx_and_subtype_is_raw()
    {
        var row = new OrganizerModRow
        {
            Identifier = "a", Name = "Mod", Author = "Ann", CurrentPath = "Mod", ProposedPath = "x",
            Category = ModCategory.Gear, SubCategory = "Feet",
        };
        var v = RuleFields.For(row, s => s, RaceSplitOptions.Off, null);
        Assert.Equal("Gear", v.Type);
        Assert.Equal("Feet", v.Subtype);

        var vfx = new OrganizerModRow
        {
            Identifier = "v", Name = "Mod", Author = "Ann", CurrentPath = "Mod", ProposedPath = "x",
            Category = ModCategory.VFX,
        };
        Assert.Equal("Animation and VFX", RuleFields.For(vfx, s => s, RaceSplitOptions.Off, null).Type);
    }

    [Fact]
    public void Author_uses_the_canonicalizer_like_the_creator_token()
    {
        var row = new OrganizerModRow
        {
            Identifier = "a", Name = "Mod", Author = "ann smith", CurrentPath = "Mod", ProposedPath = "x",
        };
        var v = RuleFields.For(row, s => s.ToUpperInvariant(), RaceSplitOptions.Off, null);
        Assert.Equal("ANN SMITH", v.Author);
    }

    [Fact]
    public void Turkish_dotless_i_does_not_break_ordinal_ignore_case()
    {
        var dotless = char.ConvertFromUtf32(0x0131); // dotless i
        Assert.False(Eval($"name = 'h{dotless}' ", Values(name: "hi")));
    }
}
