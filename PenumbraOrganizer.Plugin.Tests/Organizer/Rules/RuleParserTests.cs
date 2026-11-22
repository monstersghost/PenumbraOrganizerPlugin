using PenumbraOrganizer.Plugin.Organizer.Rules;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Rules;

public class RuleParserTests
{
    private static Rule Ok(string text)
    {
        var r = RuleParser.Parse(text);
        Assert.True(r.Error is null, r.Error?.Message);
        return r.Rule!;
    }

    private static RuleParseError Fail(string text)
    {
        var r = RuleParser.Parse(text);
        Assert.NotNull(r.Error);
        Assert.Null(r.Rule);
        return r.Error!;
    }

    [Fact]
    public void Simple_comparison_and_folder()
    {
        var rule = Ok("name contains 'hrothgar' and type = 'Body' -> Body/Hrothgar/{creator}");
        Assert.Equal("Body/Hrothgar/{creator}", rule.FolderTemplate);
        var and = Assert.IsType<RuleAnd>(rule.Condition);
        var left = Assert.IsType<RuleCompare>(and.Left);
        Assert.Equal(RuleField.Name, left.Field);
        Assert.Equal(RuleOp.Contains, left.Op);
        Assert.Equal(["hrothgar"], left.Values);
    }

    [Fact]
    public void Arrow_inside_a_string_does_not_split()
    {
        var rule = Ok("name = 'a->b' -> X");
        Assert.Equal("X", rule.FolderTemplate);
        Assert.Equal(["a->b"], Assert.IsType<RuleCompare>(rule.Condition).Values);
    }

    [Fact]
    public void Doubled_quote_is_one_quote_and_empty_string_is_allowed()
    {
        Assert.Equal(["O'Brien"], Assert.IsType<RuleCompare>(Ok("author = 'O''Brien' -> X").Condition).Values);
        Assert.Equal([""], Assert.IsType<RuleCompare>(Ok("name = '' -> X").Condition).Values);
    }

    [Fact]
    public void Precedence_not_then_and_then_or()
    {
        // A or B and not C  ==  A or (B and (not C))
        var rule = Ok("name = 'a' or name = 'b' and not name = 'c' -> X");
        var or = Assert.IsType<RuleOr>(rule.Condition);
        var and = Assert.IsType<RuleAnd>(or.Right);
        Assert.IsType<RuleNot>(and.Right);
    }

    [Fact]
    public void Parentheses_override_precedence()
    {
        var rule = Ok("(name = 'a' or name = 'b') and name = 'c' -> X");
        var and = Assert.IsType<RuleAnd>(rule.Condition);
        Assert.IsType<RuleOr>(and.Left);
    }

    [Fact]
    public void Repeated_not_and_field_not_operator_forms()
    {
        var outer = Assert.IsType<RuleNot>(Ok("not not name = 'a' -> X").Condition);
        Assert.IsType<RuleNot>(outer.Inner);
        Assert.IsType<RuleNot>(Ok("name not contains 'a' -> X").Condition);
        Assert.IsType<RuleNot>(Ok("name not in ('a','b') -> X").Condition);
        Assert.IsType<RuleNot>(Ok("name != 'a' -> X").Condition);
    }

    [Fact]
    public void In_list_forms()
    {
        Assert.Equal(["a"], Assert.IsType<RuleCompare>(Ok("type in ('a') -> X").Condition).Values);
        Assert.Equal(["a", "a", ""], Assert.IsType<RuleCompare>(Ok("type in ('a', 'a', '') -> X").Condition).Values);
    }

    [Fact]
    public void Keywords_and_fields_are_case_insensitive()
    {
        var rule = Ok("NAME CONTAINS 'x' AND Type = 'y' -> X");
        Assert.IsType<RuleAnd>(rule.Condition);
    }

    [Fact]
    public void Starts_with_and_ends_with()
    {
        Assert.Equal(RuleOp.StartsWith, Assert.IsType<RuleCompare>(Ok("name starts with 'a' -> X").Condition).Op);
        Assert.Equal(RuleOp.EndsWith, Assert.IsType<RuleCompare>(Ok("name ends with 'a' -> X").Condition).Op);
    }

    [Theory]
    [InlineData("", 0, "Rule is empty.")]
    [InlineData("name = 'a'", 10, "Missing '->' and a folder.")]
    [InlineData("name = 'a' -> ", 14, "Folder is empty.")]
    [InlineData("-> X", 0, "Expected a field name (name, author, folder, type, subtype, race, body).")]
    [InlineData("name 'a' -> X", 5, "Expected an operator (=, !=, contains, starts with, ends with, in).")]
    [InlineData("name = a -> X", 7, "Expected a quoted text like 'abc'.")]
    [InlineData("name = 'abc -> X", 7, "Text starting here is not closed; add a closing '.")]
    [InlineData("name contains '' -> X", 14, "'contains' needs some text; an empty value would match everything.")]
    [InlineData("name starts with '' -> X", 17, "'starts with' needs some text; an empty value would match everything.")]
    [InlineData("name ends with '' -> X", 15, "'ends with' needs some text; an empty value would match everything.")]
    [InlineData("name in () -> X", 9, "Expected a quoted text like 'abc'.")]
    [InlineData("name in ('a',) -> X", 13, "Expected a quoted text like 'abc'.")]
    [InlineData("(name = 'a' -> X", 12, "Expected ')'.")]
    [InlineData("name = 'a' and -> X", 15, "Expected a field name (name, author, folder, type, subtype, race, body).")]
    public void Invalid_rules_report_exact_position_and_message(string text, int position, string message)
    {
        var error = Fail(text);
        Assert.Equal(position, error.Position);
        Assert.Equal(message, error.Message);
    }

    [Fact]
    public void Unknown_field_suggests_closest_name_with_ties_in_declaration_order()
    {
        var typo = Fail("nmae = 'a' -> X");
        Assert.Equal(0, typo.Position);
        Assert.Equal("Unknown field 'nmae'. Did you mean 'name'?", typo.Message);

        // 'rame' is distance 1 from 'race' and from 'name': declaration order puts name first.
        Assert.Equal("Unknown field 'rame'. Did you mean 'name'?", Fail("rame = 'a' -> X").Message);

        Assert.Equal("Unknown field 'zzzzzz'.", Fail("zzzzzz = 'a' -> X").Message);
    }

    [Fact]
    public void Unknown_operator_suggests_closest_operator()
    {
        var error = Fail("name contians 'a' -> X");
        Assert.Equal(5, error.Position);
        Assert.Equal("Unknown operator 'contians'. Did you mean 'contains'?", error.Message);
    }

    [Fact]
    public void Pasted_curly_and_double_quotes_get_a_targeted_error()
    {
        var curly = $"name = {(char)0x2018}a{(char)0x2019} -> X";
        var e1 = Fail(curly);
        Assert.Equal(7, e1.Position);
        Assert.Equal("Use straight single quotes: 'abc'.", e1.Message);

        var e2 = Fail("name = \"a\" -> X");
        Assert.Equal(7, e2.Position);
        Assert.Equal("Use straight single quotes: 'abc'.", e2.Message);
    }

    [Fact]
    public void Length_limit_counts_utf16_units()
    {
        var ok = "name = '" + new string('a', 300 - "name = '' -> X".Length) + "' -> X";
        Assert.Equal(300, ok.Length);
        Ok(ok);

        var tooLong = ok + "y";
        var error = Fail(tooLong);
        Assert.Equal(300, error.Position);
        Assert.Equal("Rule is longer than 300 characters.", error.Message);
    }

    [Fact]
    public void Error_after_a_surrogate_pair_uses_utf16_offset()
    {
        var emoji = char.ConvertFromUtf32(0x1F600); // two UTF-16 units
        var error = Fail($"name = '{emoji}' and nmae = 'a' -> X");
        // "name = '" is 8 units, emoji 2, "' and " is 6, so the bad field starts at 16.
        Assert.Equal(16, error.Position);
    }

    [Fact]
    public void Trailing_text_after_condition_before_arrow_is_an_error()
    {
        var error = Fail("name = 'a' 'b' -> X");
        Assert.Equal(11, error.Position);
        Assert.Equal("Expected 'and', 'or' or '->'.", error.Message);
    }
}
