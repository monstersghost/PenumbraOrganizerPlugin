# Sort Keyword Rules Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the Sort tab file mods by typed SQL-like rules (`condition -> folder`), shared with the workbook's "As suggested by Sort".

**Architecture:** A pure rule language (`RuleParser`, `RuleFields`, `RuleSet`) in a new `Organizer/Rules/` folder. `OrganizerState.Sort` matches rules once per row before staging race/body or building numbered layouts, expands the rule folder through a new `DestinationResolver.ResolveRuleFolder`, and hands the result to the normal path tail. Rules ride in `LayoutOptions.Rules` and are appended to `SortSelection.Fingerprint()`. The panel edits a working list through a pure `RulesEditor` class.

**Tech Stack:** C#/.NET 10, xUnit, Dalamud.Bindings.ImGui.

Spec: `docs/superpowers/specs/2026-11-23-sort-keyword-rules-design.md` (read section 7, the review amendments, first: it overrides earlier lines).

## Global Constraints

- Max 100 rules; max 300 UTF-16 units per rule line. Error positions are zero-based UTF-16 offsets into the rule line.
- Matching is `StringComparison.OrdinalIgnoreCase`. Keywords and field names are case-insensitive.
- Fields: `name`, `author`, `folder`, `type`, `subtype`, `race`, `body`. Operators: `=`, `!=`, `contains`, `starts with`, `ends with`, `in (...)`, optional `not` before an operator. `not` > `and` > `or`; parentheses group.
- An empty rule list must leave Sort output and `Fingerprint()` byte-identical to today.
- No config reads or rule parsing per frame (the Review tab calls `SuggestedWorkbookUnavailableReason` every frame).
- No release, no version bump, no workbook change.
- Non-ASCII test text is built from code points (`char.ConvertFromUtf32` / `(char)0x2018`), never typed as literals. Assert the specific error, not just "an error".
- Tests: `dotnet test PenumbraOrganizer.Plugin.Tests` (2529 passing at start). Work on branch `feat/sort-keyword-rules`. Commit messages end with `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.

## File Structure

Create (all `namespace PenumbraOrganizer.Plugin.Organizer.Rules`, in `PenumbraOrganizer.Plugin/Organizer/Rules/`):
- `RuleModel.cs`: `RuleField`, `RuleOp`, expression records, `Rule`, `RuleParseError`, `RuleParseResult`.
- `RuleParser.cs`: tokenizer, parser, did-you-mean.
- `RuleFields.cs`: `RuleFieldValues`, `RuleFields.For`, `RuleFields.BodyLabel`, evaluator `RuleEvaluator.Matches`.
- `RuleSet.cs`: `RuleEntry`, `CompiledRule`, `RuleSet`, `RuleMatch`.
- `RuleSetCache.cs`: single-entry compiled cache keyed on config version and race options.
- `RulesEditor.cs`: pure panel state (list edits, live check text, debounced win count).

Modify:
- `Organizer/Workbook/DestinationResolver.cs`: add `ResolveRuleFolder`.
- `Organizer/LayoutOptions.cs`: add `RuleSet? Rules = null`.
- `Organizer/OrganizerModRow.cs`: add `StagedRule`.
- `Organizer/OrganizerState.cs`: rule matching in `Sort`, `StagedRule` clearing, protected-path reservation, summary.
- `Organizer/SortSummary.cs`, `Organizer/OrganizerExportFormatter.cs`.
- `Windows/SortPanel.cs` (`Fingerprint()`, Rules section), `Windows/SortSelectionFactory.cs`.
- `Configuration.cs` (`SortRules`, `SortRuleEntry`, `SortRulesVersion`).
- `Resources/help-content.json`, `docs/USER_GUIDE.md`.
- Harness test and a new checklist (Task 7).

Tests (in `PenumbraOrganizer.Plugin.Tests/Organizer/Rules/` unless noted): `RuleParserTests.cs`, `RuleEvaluatorTests.cs`, `RuleSetTests.cs`, `RuleFolderTests.cs`, `RulesEditorTests.cs`, `Organizer/OrganizerStateRulesTests.cs`, `Windows/SortRulesFingerprintTests.cs`, `RulesConfigTests.cs`.

---

### Task 1: Rule model and parser

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Rules/RuleModel.cs`, `RuleParser.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Rules/RuleParserTests.cs`

**Interfaces:**
- Produces:
  - `enum RuleField { Name, Author, Folder, Type, Subtype, Race, Body }`
  - `enum RuleOp { Equals, Contains, StartsWith, EndsWith, In }`
  - `abstract record RuleExpr`; `sealed record RuleCompare(RuleField Field, RuleOp Op, IReadOnlyList<string> Values) : RuleExpr`; `sealed record RuleNot(RuleExpr Inner)`; `RuleAnd(RuleExpr Left, RuleExpr Right)`; `RuleOr(RuleExpr Left, RuleExpr Right)` (all : `RuleExpr`). `!=` parses to `RuleNot(RuleCompare(.., Equals, ..))`.
  - `sealed record Rule(RuleExpr Condition, string FolderTemplate)`
  - `sealed record RuleParseError(int Position, string Message)`
  - `sealed record RuleParseResult(Rule? Rule, RuleParseError? Error)`
  - `static class RuleParser { public const int MaxLength = 300; public static RuleParseResult Parse(string text); }`

- [ ] **Step 1: Write the failing tests**

```csharp
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
    [InlineData("name contains '' -> X", 16, "'contains' needs some text; an empty value would match everything.")]
    [InlineData("name starts with '' -> X", 19, "'starts with' needs some text; an empty value would match everything.")]
    [InlineData("name ends with '' -> X", 17, "'ends with' needs some text; an empty value would match everything.")]
    [InlineData("name in () -> X", 9, "Expected a quoted text like 'abc'.")]
    [InlineData("name in ('a',) -> X", 14, "Expected a quoted text like 'abc'.")]
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
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter RuleParserTests`
Expected: build fails ("RuleParser does not exist").

- [ ] **Step 3: Write `RuleModel.cs`**

```csharp
namespace PenumbraOrganizer.Plugin.Organizer.Rules;

public enum RuleField { Name, Author, Folder, Type, Subtype, Race, Body }

public enum RuleOp { Equals, Contains, StartsWith, EndsWith, In }

public abstract record RuleExpr;

public sealed record RuleCompare(RuleField Field, RuleOp Op, IReadOnlyList<string> Values) : RuleExpr;

public sealed record RuleNot(RuleExpr Inner) : RuleExpr;

public sealed record RuleAnd(RuleExpr Left, RuleExpr Right) : RuleExpr;

public sealed record RuleOr(RuleExpr Left, RuleExpr Right) : RuleExpr;

/// <summary>One parsed rule: the condition and the folder text (tokens not yet expanded).</summary>
public sealed record Rule(RuleExpr Condition, string FolderTemplate);

/// <summary>A zero-based UTF-16 offset into the rule line, and what is wrong there.</summary>
public sealed record RuleParseError(int Position, string Message);

public sealed record RuleParseResult(Rule? Rule, RuleParseError? Error);
```

- [ ] **Step 4: Write `RuleParser.cs`**

```csharp
namespace PenumbraOrganizer.Plugin.Organizer.Rules;

/// <summary>
/// Turns "condition -> folder" into a <see cref="Rule"/>. Pure, no state. Grammar:
/// or := and ('or' and)*; and := unary ('and' unary)*; unary := 'not' unary | primary;
/// primary := '(' or ')' | FIELD ['not'] op; op := '=' STR | '!=' STR | 'contains' STR
/// | 'starts' 'with' STR | 'ends' 'with' STR | 'in' '(' STR (',' STR)* ')'.
/// </summary>
public static class RuleParser
{
    public const int MaxLength = 300;

    private static readonly string[] FieldNames = ["name", "author", "folder", "type", "subtype", "race", "body"];
    private static readonly string[] OperatorWords = ["contains", "starts", "ends", "in"];
    private const string FieldList = "name, author, folder, type, subtype, race, body";

    private enum Kind { Word, Str, LParen, RParen, Comma, Eq, Neq, Arrow, Bad, End }

    private readonly record struct Token(Kind Kind, int Start, int End, string Text);

    private sealed class Fail(RuleParseError error) : Exception
    {
        public RuleParseError Error { get; } = error;
    }

    public static RuleParseResult Parse(string text)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new Fail(new RuleParseError(0, "Rule is empty."));
            if (text.Length > MaxLength)
                throw new Fail(new RuleParseError(MaxLength, $"Rule is longer than {MaxLength} characters."));

            var tokens = Tokenize(text);
            var p = new Parser(text, tokens);
            return new RuleParseResult(p.ParseRule(), null);
        }
        catch (Fail f)
        {
            return new RuleParseResult(null, f.Error);
        }
    }

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (c == '\'')
            {
                var start = i++;
                var sb = new System.Text.StringBuilder();
                var closed = false;
                while (i < text.Length)
                {
                    if (text[i] == '\'')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '\'') { sb.Append('\''); i += 2; continue; }
                        i++; closed = true; break;
                    }
                    sb.Append(text[i++]);
                }
                if (!closed)
                    throw new Fail(new RuleParseError(start, "Text starting here is not closed; add a closing '."));
                tokens.Add(new Token(Kind.Str, start, i, sb.ToString()));
                continue;
            }

            if (c == '"' || c == (char)0x2018 || c == (char)0x2019 || c == (char)0x201C || c == (char)0x201D)
                throw new Fail(new RuleParseError(i, "Use straight single quotes: 'abc'."));

            if (c == '-' && i + 1 < text.Length && text[i + 1] == '>')
            {
                tokens.Add(new Token(Kind.Arrow, i, i + 2, "->"));
                i += 2;
                // Everything after the arrow is the folder; the parser reads it from the raw text.
                tokens.Add(new Token(Kind.End, i, text.Length, text[i..]));
                return tokens;
            }

            switch (c)
            {
                case '(': tokens.Add(new Token(Kind.LParen, i, i + 1, "(")); i++; continue;
                case ')': tokens.Add(new Token(Kind.RParen, i, i + 1, ")")); i++; continue;
                case ',': tokens.Add(new Token(Kind.Comma, i, i + 1, ",")); i++; continue;
                case '=': tokens.Add(new Token(Kind.Eq, i, i + 1, "=")); i++; continue;
                case '!' when i + 1 < text.Length && text[i + 1] == '=':
                    tokens.Add(new Token(Kind.Neq, i, i + 2, "!=")); i += 2; continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                tokens.Add(new Token(Kind.Word, start, i, text[start..i]));
                continue;
            }

            tokens.Add(new Token(Kind.Bad, i, i + 1, c.ToString()));
            i++;
        }

        tokens.Add(new Token(Kind.End, text.Length, text.Length, string.Empty));
        return tokens;
    }

    private sealed class Parser(string text, List<Token> tokens)
    {
        private int _pos;
        private Token Cur => tokens[_pos];

        public Rule ParseRule()
        {
            var condition = ParseOr();
            if (Cur.Kind != Kind.Arrow)
            {
                if (Cur.Kind == Kind.End)
                    throw new Fail(new RuleParseError(text.Length, "Missing '->' and a folder."));
                throw new Fail(new RuleParseError(Cur.Start, "Expected 'and', 'or' or '->'."));
            }

            _pos++; // arrow; the next token is End carrying the folder text
            var folder = text[Cur.Start..].Trim();
            if (folder.Length == 0)
                throw new Fail(new RuleParseError(text.Length, "Folder is empty."));
            return new Rule(condition, folder);
        }

        private bool IsWord(string word) =>
            Cur.Kind == Kind.Word && string.Equals(Cur.Text, word, StringComparison.OrdinalIgnoreCase);

        private RuleExpr ParseOr()
        {
            var left = ParseAnd();
            while (IsWord("or")) { _pos++; left = new RuleOr(left, ParseAnd()); }
            return left;
        }

        private RuleExpr ParseAnd()
        {
            var left = ParseUnary();
            while (IsWord("and")) { _pos++; left = new RuleAnd(left, ParseUnary()); }
            return left;
        }

        private RuleExpr ParseUnary()
        {
            if (IsWord("not")) { _pos++; return new RuleNot(ParseUnary()); }
            return ParsePrimary();
        }

        private RuleExpr ParsePrimary()
        {
            if (Cur.Kind == Kind.LParen)
            {
                _pos++;
                var inner = ParseOr();
                if (Cur.Kind != Kind.RParen)
                    throw new Fail(new RuleParseError(Cur.Kind == Kind.End ? Math.Min(Cur.Start, text.Length) : Cur.Start,
                        "Expected ')'."));
                _pos++;
                return inner;
            }

            if (Cur.Kind != Kind.Word)
                throw new Fail(new RuleParseError(Cur.Start, $"Expected a field name ({FieldList})."));

            var field = FieldFor(Cur);
            _pos++;

            var negated = false;
            if (IsWord("not")) { negated = true; _pos++; }

            RuleExpr compare = ParseOperator(field);
            return negated ? new RuleNot(compare) : compare;
        }

        private RuleExpr ParseOperator(RuleField field)
        {
            var t = Cur;
            if (t.Kind == Kind.Eq) { _pos++; return new RuleCompare(field, RuleOp.Equals, [ParseString(allowEmpty: true, "'='")]); }
            if (t.Kind == Kind.Neq)
            {
                _pos++;
                return new RuleNot(new RuleCompare(field, RuleOp.Equals, [ParseString(allowEmpty: true, "'!='")]));
            }

            if (t.Kind != Kind.Word)
                throw new Fail(new RuleParseError(t.Start, "Expected an operator (=, !=, contains, starts with, ends with, in)."));

            var word = t.Text.ToLowerInvariant();
            switch (word)
            {
                case "contains":
                    _pos++;
                    return new RuleCompare(field, RuleOp.Contains, [ParseString(false, "'contains'")]);
                case "starts":
                    _pos++; ExpectWith(t);
                    return new RuleCompare(field, RuleOp.StartsWith, [ParseString(false, "'starts with'")]);
                case "ends":
                    _pos++; ExpectWith(t);
                    return new RuleCompare(field, RuleOp.EndsWith, [ParseString(false, "'ends with'")]);
                case "in":
                    _pos++;
                    return ParseList(field);
            }

            var near = Closest(word, OperatorWords);
            var message = near is null
                ? $"Unknown operator '{t.Text}'."
                : $"Unknown operator '{t.Text}'. Did you mean '{near}'?";
            throw new Fail(new RuleParseError(t.Start, message));
        }

        private void ExpectWith(Token op)
        {
            if (!IsWord("with"))
                throw new Fail(new RuleParseError(Cur.Start, $"Expected 'with' after '{op.Text.ToLowerInvariant()}'."));
            _pos++;
        }

        private RuleExpr ParseList(RuleField field)
        {
            if (Cur.Kind != Kind.LParen)
                throw new Fail(new RuleParseError(Cur.Start, "Expected '(' after 'in'."));
            _pos++;

            var values = new List<string> { ParseString(true, "'in'") };
            while (Cur.Kind == Kind.Comma)
            {
                _pos++;
                values.Add(ParseString(true, "'in'"));
            }

            if (Cur.Kind != Kind.RParen)
                throw new Fail(new RuleParseError(Cur.Start, "Expected ')'."));
            _pos++;
            return new RuleCompare(field, RuleOp.In, values);
        }

        private string ParseString(bool allowEmpty, string opName)
        {
            if (Cur.Kind != Kind.Str)
                throw new Fail(new RuleParseError(Cur.Start, "Expected a quoted text like 'abc'."));
            var t = Cur;
            if (!allowEmpty && t.Text.Length == 0)
                throw new Fail(new RuleParseError(t.Start, $"{opName} needs some text; an empty value would match everything."));
            _pos++;
            return t.Text;
        }

        private static RuleField FieldFor(Token t)
        {
            var lower = t.Text.ToLowerInvariant();
            var index = Array.IndexOf(FieldNames, lower);
            if (index >= 0)
                return (RuleField)index;

            var near = Closest(lower, FieldNames);
            throw new Fail(new RuleParseError(t.Start, near is null
                ? $"Unknown field '{t.Text}'."
                : $"Unknown field '{t.Text}'. Did you mean '{near}'?"));
        }
    }

    // Closest candidate within edit distance 2; the first in list order wins a tie.
    private static string? Closest(string word, string[] candidates)
    {
        string? best = null;
        var bestDistance = 3;
        foreach (var candidate in candidates)
        {
            var d = Distance(word, candidate);
            if (d < bestDistance) { best = candidate; bestDistance = d; }
        }
        return best;
    }

    private static int Distance(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
```

Two expected-position fixes the implementer must reconcile while running the tests (the table above was written by hand; the code is the reference for the grammar, the table for the messages): if a table row's position differs by the exact counted offset, recount the offset in the test string and fix the **test**, not the grammar. `name in () -> X` reports at the `)` token (offset 9); `(name = 'a' -> X` reports at the arrow (offset 12) because the arrow token is reached while expecting `)`.

- [ ] **Step 5: Run tests**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter RuleParserTests`
Expected: PASS (all).

- [ ] **Step 6: Commit**

```bash
git add PenumbraOrganizer.Plugin/Organizer/Rules PenumbraOrganizer.Plugin.Tests/Organizer/Rules
git commit -m "feat: sort rule parser with positions and did-you-mean"
```

---

### Task 2: Fields and evaluator

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Rules/RuleFields.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Rules/RuleEvaluatorTests.cs`

**Interfaces:**
- Consumes: `RuleExpr` types (Task 1); `WorkbookTokens.TypeFolderFor(ModCategory?)`; `RaceSplitRouting.ResolveForToken(OrganizerModRow, RaceSplitOptions)`; `SortFolderSelectors.KnownSegment`; `PenumbraPathSemantics.FixName`; `BodyPriorityResolver.Resolve(row.BodyCandidates, priority, nameFirst:)` returning an object with `.Label` (as used in `SortFolderSelectors.ResolveBody`).
- Produces:
  - `sealed record RuleFieldValues(string Name, string Author, string Folder, string Type, string Subtype, string Race, string Body)`
  - `static class RuleFields { RuleFieldValues For(OrganizerModRow row, Func<string,string> canonicalizeCreator, RaceSplitOptions race, string? body); string? BodyLabel(OrganizerModRow row, BodyPriority? priority, bool splitGearBody, bool splitBodyMods); }`
  - `static class RuleEvaluator { bool Matches(RuleExpr expr, RuleFieldValues values); string Value(RuleFieldValues v, RuleField f); }`

- [ ] **Step 1: Write the failing tests**

```csharp
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
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter RuleEvaluatorTests`
Expected: build error, `RuleFields`/`RuleEvaluator` missing.

- [ ] **Step 3: Implement `RuleFields.cs`**

```csharp
using PenumbraOrganizer.Plugin.Organizer.BodyTypes;
using PenumbraOrganizer.Plugin.Organizer.Classification;
using PenumbraOrganizer.Plugin.Organizer.Workbook;
using PenumbraOrganizer.Core.Classification;

namespace PenumbraOrganizer.Plugin.Organizer.Rules;

public sealed record RuleFieldValues(
    string Name, string Author, string Folder, string Type, string Subtype, string Race, string Body);

public static class RuleFields
{
    /// <param name="body">The body label to match, from <see cref="BodyLabel"/>; null reads as empty.</param>
    public static RuleFieldValues For(
        OrganizerModRow row, Func<string, string> canonicalizeCreator, RaceSplitOptions race, string? body)
    {
        var author = SortFolderSelectors.KnownSegment(canonicalizeCreator(row.Author)) ?? string.Empty;
        var raceFolder = RaceSplitRouting.ResolveForToken(row, race)?.Folder ?? string.Empty;
        return new RuleFieldValues(
            PenumbraPathSemantics.FixName(row.Name).Trim(),
            author,
            ParentOf(row.CurrentPath),
            WorkbookTokens.TypeFolderFor(row.Category),
            row.SubCategory ?? string.Empty,
            raceFolder,
            body ?? string.Empty);
    }

    /// <summary>
    /// The priority-resolved body while this row's category has its body split on and the row has
    /// candidates; otherwise the scan's default <see cref="OrganizerModRow.BodyType"/>.
    /// </summary>
    public static string? BodyLabel(
        OrganizerModRow row, BodyPriority? priority, bool splitGearBody, bool splitBodyMods)
    {
        var applies = row.Category == ModCategory.Gear ? splitGearBody : row.Category == ModCategory.Body && splitBodyMods;
        if (applies && priority is not null && row.BodyCandidates is not null)
            return BodyPriorityResolver.Resolve(row.BodyCandidates, priority, nameFirst: row.Category == ModCategory.Body)?.Label;
        return row.BodyType;
    }

    private static string ParentOf(string currentPath)
    {
        var normalized = currentPath.Replace('\\', '/').Trim('/');
        var slash = normalized.LastIndexOf('/');
        return slash < 0 ? string.Empty : normalized[..slash];
    }
}

public static class RuleEvaluator
{
    public static bool Matches(RuleExpr expr, RuleFieldValues v) => expr switch
    {
        RuleAnd a => Matches(a.Left, v) && Matches(a.Right, v),
        RuleOr o => Matches(o.Left, v) || Matches(o.Right, v),
        RuleNot n => !Matches(n.Inner, v),
        RuleCompare c => Compare(c, Value(v, c.Field)),
        _ => false,
    };

    public static string Value(RuleFieldValues v, RuleField field) => field switch
    {
        RuleField.Name => v.Name,
        RuleField.Author => v.Author,
        RuleField.Folder => v.Folder,
        RuleField.Type => v.Type,
        RuleField.Subtype => v.Subtype,
        RuleField.Race => v.Race,
        RuleField.Body => v.Body,
        _ => string.Empty,
    };

    private static bool Compare(RuleCompare c, string actual)
    {
        const StringComparison Cmp = StringComparison.OrdinalIgnoreCase;
        return c.Op switch
        {
            RuleOp.Equals => string.Equals(actual, c.Values[0], Cmp),
            RuleOp.Contains => actual.Contains(c.Values[0], Cmp),
            RuleOp.StartsWith => actual.StartsWith(c.Values[0], Cmp),
            RuleOp.EndsWith => actual.EndsWith(c.Values[0], Cmp),
            RuleOp.In => c.Values.Any(value => string.Equals(actual, value, Cmp)),
            _ => false,
        };
    }
}
```

If `BodyPriorityResolver`'s namespace differs, `grep -rn "class BodyPriorityResolver"` and fix the `using`.

- [ ] **Step 4: Run tests**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter RuleEvaluatorTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A PenumbraOrganizer.Plugin PenumbraOrganizer.Plugin.Tests
git commit -m "feat: sort rule fields and evaluator"
```

---

### Task 3: RuleSet and rule-folder expansion

**Files:**
- Create: `PenumbraOrganizer.Plugin/Organizer/Rules/RuleSet.cs`
- Modify: `PenumbraOrganizer.Plugin/Organizer/Workbook/DestinationResolver.cs` (add `ResolveRuleFolder`)
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Rules/RuleSetTests.cs`, `RuleFolderTests.cs`

**Interfaces:**
- Consumes: `RuleParser.Parse`, `RuleEvaluator.Matches`, `RuleFieldValues`, `RaceSplitOptions`, `DestinationTokens`, `DestinationResult`.
- Produces:
  - `sealed record RuleEntry(string Text, bool Enabled)`
  - `sealed class CompiledRule { int Ordinal (1-based); string Text; bool Enabled; Rule? Rule; RuleParseError? Error; bool Runs => Enabled && Rule is not null; }`
  - `sealed record RuleMatch(CompiledRule Rule)`
  - `sealed class RuleSet : IEquatable<RuleSet>`: `static readonly RuleSet Empty`; `static RuleSet Compile(IReadOnlyList<RuleEntry> entries, RaceSplitOptions race)`; `const int MaxRules = 100`; `IReadOnlyList<CompiledRule> Rules`; `RaceSplitOptions Race`; `bool HasRules` (any entries); `bool AnyRuns`; `bool UsesRace` (any running rule reads `race` or `{race}`); `string Digest` (length-prefixed `enabled,length:text` for every entry); `RuleMatch? Match(RuleFieldValues values)`; `IReadOnlyList<CompiledRule> Broken` (enabled rules with a parse error, or over the limit).
  - `DestinationResolver.ResolveRuleFolder(string raw, DestinationTokens tokens) : DestinationResult`

- [ ] **Step 1: Write the failing tests**

`RuleSetTests.cs`:

```csharp
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Rules;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Rules;

public class RuleSetTests
{
    private static readonly RuleFieldValues Mod =
        new("Hrothgar Body", "Ann", "Old", "Body", "", "Hrothgar", "");

    private static RuleSet Compile(params (string, bool)[] rules) =>
        RuleSet.Compile(rules.Select(r => new RuleEntry(r.Item1, r.Item2)).ToList(), RaceSplitOptions.Off);

    [Fact]
    public void First_enabled_valid_match_wins_and_order_matters()
    {
        var set = Compile(("name contains 'body' -> A", true), ("name contains 'hroth' -> B", true));
        Assert.Equal(1, set.Match(Mod)!.Rule.Ordinal);

        var reversed = Compile(("name contains 'hroth' -> B", true), ("name contains 'body' -> A", true));
        Assert.Equal("name contains 'hroth' -> B", reversed.Match(Mod)!.Rule.Text);
    }

    [Fact]
    public void Disabled_and_broken_rules_are_skipped()
    {
        var set = Compile(("name contains 'body' -> A", false), ("nmae = 'x' -> B", true), ("type = 'body' -> C", true));
        var match = set.Match(Mod)!;
        Assert.Equal(3, match.Rule.Ordinal);
        Assert.Single(set.Broken);
        Assert.Equal(2, set.Broken[0].Ordinal);
    }

    [Fact]
    public void No_match_returns_null()
    {
        Assert.Null(Compile(("name = 'zzz' -> A", true)).Match(Mod));
        Assert.Null(RuleSet.Empty.Match(Mod));
    }

    [Fact]
    public void Rules_past_one_hundred_are_flagged_and_never_run()
    {
        var rules = Enumerable.Range(0, 101).Select(i => ($"name = 'n{i}' -> F", true)).ToArray();
        var set = Compile(rules);
        Assert.Equal(101, set.Rules.Count);
        Assert.Null(set.Rules[100].Rule);
        Assert.Equal("Only the first 100 rules run.", set.Rules[100].Error!.Message);
        Assert.Single(set.Broken);
    }

    [Fact]
    public void Equality_follows_every_entry_including_disabled_and_invalid_ones()
    {
        var a = Compile(("nmae = 'x' -> A", false));
        var b = Compile(("nmae = 'y' -> A", false));
        var c = Compile(("nmae = 'x' -> A", false));
        Assert.NotEqual(a, b);
        Assert.Equal(a, c);
        Assert.Equal(a.GetHashCode(), c.GetHashCode());
        Assert.NotEqual(Compile(("name = 'a' -> X", true)), Compile(("name = 'a' -> X", false)));
    }

    [Fact]
    public void Digest_cannot_collide_through_a_separator_character()
    {
        var one = Compile(("name = 'a|b' -> X", true));
        var two = Compile(("name = 'a' -> X", true), ("b' -> X", true));
        Assert.NotEqual(one.Digest, two.Digest);
    }

    [Fact]
    public void UsesRace_reads_the_condition_and_the_folder()
    {
        Assert.True(Compile(("race = 'Viera' -> X", true)).UsesRace);
        Assert.True(Compile(("name = 'a' -> X/{race}", true)).UsesRace);
        Assert.False(Compile(("name = 'a' -> X/{type}", true)).UsesRace);
        Assert.False(Compile(("race = 'Viera' -> X", false)).UsesRace);
    }

    [Fact]
    public void Equality_ignores_race_options_unless_a_rule_uses_race()
    {
        var withRace = RaceSplitOptions.Off with { MultiRaceToMultiRace = false };
        var plain = RuleSet.Compile([new RuleEntry("name = 'a' -> X", true)], RaceSplitOptions.Off);
        var plain2 = RuleSet.Compile([new RuleEntry("name = 'a' -> X", true)], withRace);
        Assert.Equal(plain, plain2);

        var raced = RuleSet.Compile([new RuleEntry("race = 'a' -> X", true)], RaceSplitOptions.Off);
        var raced2 = RuleSet.Compile([new RuleEntry("race = 'a' -> X", true)], withRace);
        Assert.NotEqual(raced, raced2);
    }
}
```

`RuleFolderTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "RuleSetTests|RuleFolderTests"`
Expected: build errors.

- [ ] **Step 3: Add `ResolveRuleFolder` to `DestinationResolver`**

Insert after `Resolve(...)` in `DestinationResolver.cs`:

```csharp
    /// <summary>
    /// A sort rule's folder: tokens always on, never read as a category code or built-in name (a rule's
    /// "17/x" is the literal folder 17), each segment trimmed, a whole-token segment that expands empty
    /// dropped, and a stray brace an error. Pure.
    /// </summary>
    public static DestinationResult ResolveRuleFolder(string raw, DestinationTokens tokens)
    {
        var normalized = NormalizePath(raw);
        if (normalized.Length == 0)
            return DestinationResult.Fail("Destination is empty.");

        var expanded = new List<string>();
        foreach (var part in normalized.Split('/'))
        {
            var segment = part.Trim();
            string? unknown = null;
            var text = TokenPattern.Replace(segment, match =>
            {
                var value = ValueOf(match.Groups[1].Value, tokens);
                if (value is null)
                {
                    unknown ??= match.Groups[1].Value;
                    return match.Value;
                }

                return value;
            });

            if (unknown is not null)
                return DestinationResult.Fail($"unknown token {{{unknown}}}");
            if (text.Contains('{') || text.Contains('}'))
                return DestinationResult.Fail("Folder has an unmatched { or }.");

            text = text.Trim();
            if (text.Length == 0)
                continue;
            expanded.Add(text);
        }

        if (expanded.Count == 0)
            return DestinationResult.Fail("Folder expands to nothing for this mod.");

        var final = string.Join('/', expanded);
        return StructureError(final) is { } error ? DestinationResult.Fail(error) : DestinationResult.Ok(final);
    }
```

Caveat: a token value could itself contain `{`; token values are never re-expanded but the brace check runs on the expanded text. A creator named `A{b}` would then trip the check. To avoid that, run the brace check on `segment` BEFORE replacement instead: move the `Contains('{')` test to the pre-replacement text with the tokens removed (`TokenPattern.Replace(segment, "")`). Write the code that way:

```csharp
            if (TokenPattern.Replace(segment, string.Empty).Any(c => c is '{' or '}'))
                return DestinationResult.Fail("Folder has an unmatched { or }.");
```
placed before the `Replace` call, and drop the post-replacement check. Re-run `Unknown_token` and `stray braces` tests.

- [ ] **Step 4: Write `RuleSet.cs`**

```csharp
using System.Text.RegularExpressions;

namespace PenumbraOrganizer.Plugin.Organizer.Rules;

public sealed record RuleEntry(string Text, bool Enabled);

public sealed class CompiledRule
{
    public required int Ordinal { get; init; }
    public required string Text { get; init; }
    public required bool Enabled { get; init; }
    public Rule? Rule { get; init; }
    public RuleParseError? Error { get; init; }

    public bool Runs => Enabled && Rule is not null;
}

public sealed record RuleMatch(CompiledRule Rule);

/// <summary>
/// An ordered, compiled rule list with value equality. Equality and <see cref="Digest"/> follow EVERY
/// entry (disabled and invalid ones too, so editing them greys a staged export); the race options count
/// only when a running rule reads race.
/// </summary>
public sealed class RuleSet : IEquatable<RuleSet>
{
    public const int MaxRules = 100;

    private static readonly Regex RaceToken = new(@"\{race\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static readonly RuleSet Empty = new([], RaceSplitOptions.Off);

    private RuleSet(IReadOnlyList<CompiledRule> rules, RaceSplitOptions race)
    {
        Rules = rules;
        Race = race;
        Digest = string.Concat(rules.Select(r => $"{(r.Enabled ? 1 : 0)}{r.Text.Length}:{r.Text}"));
        UsesRace = rules.Any(r => r.Runs && (ReadsRace(r.Rule!.Condition) || RaceToken.IsMatch(r.Rule.FolderTemplate)));
        Broken = rules.Where(r => r.Enabled && r.Error is not null).ToList();
    }

    public IReadOnlyList<CompiledRule> Rules { get; }
    public RaceSplitOptions Race { get; }
    public string Digest { get; }
    public bool UsesRace { get; }
    public IReadOnlyList<CompiledRule> Broken { get; }
    public bool HasRules => Rules.Count > 0;
    public bool AnyRuns => Rules.Any(r => r.Runs);

    public static RuleSet Compile(IReadOnlyList<RuleEntry> entries, RaceSplitOptions race)
    {
        var compiled = new List<CompiledRule>(entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (i >= MaxRules)
            {
                compiled.Add(new CompiledRule
                {
                    Ordinal = i + 1, Text = entry.Text, Enabled = entry.Enabled,
                    Error = new RuleParseError(0, $"Only the first {MaxRules} rules run."),
                });
                continue;
            }

            var parsed = RuleParser.Parse(entry.Text);
            compiled.Add(new CompiledRule
            {
                Ordinal = i + 1, Text = entry.Text, Enabled = entry.Enabled,
                Rule = parsed.Rule, Error = parsed.Error,
            });
        }

        return new RuleSet(compiled, race);
    }

    public RuleMatch? Match(RuleFieldValues values)
    {
        foreach (var rule in Rules)
        {
            if (rule.Runs && RuleEvaluator.Matches(rule.Rule!.Condition, values))
                return new RuleMatch(rule);
        }

        return null;
    }

    private static bool ReadsRace(RuleExpr expr) => expr switch
    {
        RuleCompare c => c.Field == RuleField.Race,
        RuleNot n => ReadsRace(n.Inner),
        RuleAnd a => ReadsRace(a.Left) || ReadsRace(a.Right),
        RuleOr o => ReadsRace(o.Left) || ReadsRace(o.Right),
        _ => false,
    };

    public bool Equals(RuleSet? other) =>
        other is not null && Digest == other.Digest && (!UsesRace || Race.Equals(other.Race));

    public override bool Equals(object? obj) => Equals(obj as RuleSet);

    public override int GetHashCode() => Digest.GetHashCode(StringComparison.Ordinal);
}
```

- [ ] **Step 5: Run tests**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "RuleSetTests|RuleFolderTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add -A PenumbraOrganizer.Plugin PenumbraOrganizer.Plugin.Tests
git commit -m "feat: sort rule set with value equality and rule-folder expansion"
```

---

### Task 4: Sort wiring, staged rule, summary, fingerprint

**Files:**
- Modify: `Organizer/LayoutOptions.cs`, `Organizer/OrganizerModRow.cs`, `Organizer/OrganizerState.cs`, `Organizer/SortSummary.cs`, `Organizer/OrganizerExportFormatter.cs`, `Windows/SortPanel.cs` (`Fingerprint()` only)
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/OrganizerStateRulesTests.cs`, `PenumbraOrganizer.Plugin.Tests/Windows/SortRulesFingerprintTests.cs`

**Interfaces:**
- Consumes: `RuleSet`, `RuleFields`, `WorkbookTokens.For`, `DestinationResolver.ResolveRuleFolder` (Tasks 1 to 3).
- Produces:
  - `LayoutOptions(..., RuleSet? Rules = null)` (new last positional parameter).
  - `sealed record StagedRule(int Ordinal, string Text)`; `OrganizerModRow.StagedRule { get; set; }`.
  - `sealed record RuleSortSummary(int Applied, IReadOnlyList<string> SkippedRules, IReadOnlyList<string> Unplaced)`; `SortSummary(..., RuleSortSummary? Rules = null)`; `SortSummaryText.Describe` appends rule lines.
  - Diagnostics line `StagedRule: #<n> <text>`.

- [ ] **Step 1: Write the failing tests**

`OrganizerStateRulesTests.cs`:

```csharp
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Rules;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class OrganizerStateRulesTests
{
    private static string Canon(string s) => s;

    private static OrganizerModRow Row(string id, string name, string author = "Ann", string path = "",
        ModCategory? category = ModCategory.Gear, string? sub = "Feet", bool prot = false) =>
        new()
        {
            Identifier = id, Name = name, Author = author,
            CurrentPath = path.Length == 0 ? name : path, ProposedPath = path.Length == 0 ? name : path,
            Category = category, SubCategory = sub, Protected = prot,
        };

    private static OrganizerState State(params OrganizerModRow[] rows)
    {
        var state = new OrganizerState();
        state.LoadScan(rows, new HashSet<string>());
        return state;
    }

    private static LayoutOptions Rules(params (string, bool)[] rules) =>
        new(Rules: RuleSet.Compile(rules.Select(r => new RuleEntry(r.Item1, r.Item2)).ToList(), RaceSplitOptions.Off));

    private static int Sort(OrganizerState s, LayoutOptions? options, SortScope? scope = null) =>
        s.Sort(SortStrategy.TypeThenCreator, true, true, false, Canon, options: options, scope: scope);

    [Fact]
    public void Rule_replaces_the_whole_path_and_names_the_rule()
    {
        var state = State(Row("a", "Hrothgar Boots"));
        Sort(state, Rules(("name contains 'hrothgar' -> Special/{creator}", true)));
        var row = state.Mods.Single();
        Assert.Equal("Special/Ann/Hrothgar Boots", row.ProposedPath);
        Assert.Equal(new StagedRule(1, "name contains 'hrothgar' -> Special/{creator}"), row.StagedRule);
        Assert.Null(row.StagedRace);
        Assert.Null(row.StagedBodyResolution);
    }

    [Fact]
    public void No_match_sorts_normally_and_empty_rules_equal_no_rules()
    {
        var plain = State(Row("a", "Boots"));
        Sort(plain, null);
        var withEmpty = State(Row("a", "Boots"));
        Sort(withEmpty, new LayoutOptions(Rules: RuleSet.Empty));
        var noMatch = State(Row("a", "Boots"));
        Sort(noMatch, Rules(("name = 'zzz' -> X", true)));

        Assert.Equal(plain.Mods.Single().ProposedPath, withEmpty.Mods.Single().ProposedPath);
        Assert.Equal(plain.Mods.Single().ProposedPath, noMatch.Mods.Single().ProposedPath);
        Assert.Null(noMatch.Mods.Single().StagedRule);
    }

    [Fact]
    public void Protected_mods_are_never_touched()
    {
        var state = State(Row("a", "Hrothgar Boots", prot: true));
        Sort(state, Rules(("name contains 'hrothgar' -> X", true)));
        Assert.Equal("Hrothgar Boots", state.Mods.Single().ProposedPath);
        Assert.Null(state.Mods.Single().StagedRule);
    }

    [Fact]
    public void Invalid_expansion_consumes_the_mod_and_leaves_it_in_place_even_if_a_later_rule_matches()
    {
        var state = State(Row("a", "Boots", author: ""));
        Sort(state, Rules(("name = 'boots' -> {creator}", true), ("name = 'boots' -> Later", true)));
        var row = state.Mods.Single();
        Assert.Equal("Boots", row.ProposedPath);
        Assert.Null(row.StagedRule);
        var summary = state.LastSortSummary!.Rules!;
        Assert.Single(summary.Unplaced);
        Assert.Contains("Rule 1", summary.Unplaced[0]);
    }

    [Fact]
    public void Broken_rules_are_skipped_and_listed()
    {
        var state = State(Row("a", "Boots"));
        Sort(state, Rules(("nmae = 'x' -> A", true), ("name = 'boots' -> B", true)));
        Assert.Equal("B/Boots", state.Mods.Single().ProposedPath);
        var summary = state.LastSortSummary!.Rules!;
        Assert.Equal(1, summary.Applied);
        Assert.Single(summary.SkippedRules);
        Assert.Contains("Rule 1", summary.SkippedRules[0]);
        Assert.Contains("Did you mean 'name'", summary.SkippedRules[0]);
    }

    [Fact]
    public void Ruled_rows_leave_no_gaps_in_the_numbered_gear_layout()
    {
        // Two body types would number Gear/1. X and Gear/2. Y. A ruled row must not take a number.
        var state = State(
            new OrganizerModRow { Identifier = "r", Name = "Ruled", Author = "A", CurrentPath = "Ruled", ProposedPath = "Ruled",
                Category = ModCategory.Gear, SubCategory = "Top", BodyType = "Bibo+" },
            new OrganizerModRow { Identifier = "k", Name = "Kept", Author = "A", CurrentPath = "Kept", ProposedPath = "Kept",
                Category = ModCategory.Gear, SubCategory = "Top", BodyType = "Gen3" });
        var options = Rules(("name = 'ruled' -> Out", true));
        state.Sort(SortStrategy.TypeOnly, true, true, false, Canon, splitGearBody: true, options: options);
        var kept = state.Mods.Single(m => m.Identifier == "k").ProposedPath;
        // With the ruled row excluded there is ONE body, so its number is 1.
        Assert.StartsWith("Gear/1.", kept);
    }

    [Fact]
    public void Scoped_sort_applies_rules_only_inside_the_scope()
    {
        var state = State(Row("a", "Boots", path: "Folder/Boots"), Row("b", "Boots Two"));
        Sort(state, Rules(("name contains 'boots' -> R", true)), SortScope.RootOnlyScope());
        Assert.Equal("Folder/Boots", state.Mods.Single(m => m.Identifier == "a").ProposedPath);
        Assert.Equal("R/Boots Two", state.Mods.Single(m => m.Identifier == "b").ProposedPath);
    }

    [Fact]
    public void Sorting_twice_is_stable_for_a_folder_rule()
    {
        var state = State(Row("a", "Boots", path: "Old/Boots"));
        var options = Rules(("folder starts with 'Old' -> New", true));
        Sort(state, options);
        var first = state.Mods.Single().ProposedPath;
        Assert.Equal("New/Boots", first);
        // Simulate Apply plus a rescan: the mod now lives at New/Boots, so the rule no longer matches
        // and the normal sort runs; the result is whatever normal sort gives, not a flip back to Old.
        var again = State(Row("a", "Boots", path: first));
        Sort(again, options);
        Assert.NotEqual("Old/Boots", again.Mods.Single().ProposedPath);
    }

    [Fact]
    public void Collision_with_a_protected_mod_is_renamed_for_a_ruled_row()
    {
        var state = State(
            Row("p", "Boots", path: "R/Boots", prot: true),
            Row("a", "Boots", path: "Loose/Boots"));
        Sort(state, Rules(("name = 'boots' -> R", true)));
        var ruled = state.Mods.Single(m => m.Identifier == "a").ProposedPath;
        Assert.NotEqual("R/Boots", ruled);
        Assert.StartsWith("R/Boots", ruled);
    }

    [Fact]
    public void StagedRule_is_cleared_by_manual_assignment_and_by_a_later_sort_without_a_match()
    {
        var state = State(Row("a", "Boots"));
        Sort(state, Rules(("name = 'boots' -> R", true)));
        Assert.NotNull(state.Mods.Single().StagedRule);
        state.AssignManual("a", "Manual/Boots");
        Assert.Null(state.Mods.Single().StagedRule);

        Sort(state, Rules(("name = 'boots' -> R", true)));
        Assert.NotNull(state.Mods.Single().StagedRule);
        Sort(state, null);
        Assert.Null(state.Mods.Single().StagedRule);
    }
}
```

Fix-up to apply while writing: replace `SortScope.RootOnlyScope()` with the real root-only scope factory (`grep -n "RootOnly" PenumbraOrganizer.Plugin/Organizer/SortScope.cs`).

`SortRulesFingerprintTests.cs`:

```csharp
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Rules;
using PenumbraOrganizer.Plugin.Windows;

namespace PenumbraOrganizer.Plugin.Tests.Windows;

public class SortRulesFingerprintTests
{
    private static SortSelection Selection(RuleSet? rules, RaceSplitOptions? race = null) =>
        new(SortStrategy.TypeThenCreator, true, true, false, Layout: new LayoutOptions(Rules: rules, Race: race));

    private static RuleSet Set(params (string, bool)[] r) =>
        RuleSet.Compile(r.Select(x => new RuleEntry(x.Item1, x.Item2)).ToList(), RaceSplitOptions.Off);

    [Fact]
    public void Empty_or_null_rules_keep_todays_exact_fingerprint()
    {
        var today = Selection(null).Fingerprint();
        Assert.Equal(today, Selection(RuleSet.Empty).Fingerprint());
        Assert.DoesNotContain("rules", today);
    }

    [Fact]
    public void Any_edit_reorder_or_toggle_changes_the_fingerprint()
    {
        var baseline = Selection(Set(("name = 'a' -> X", true), ("name = 'b' -> Y", true))).Fingerprint();
        Assert.NotEqual(baseline, Selection(Set(("name = 'a' -> X", true), ("name = 'c' -> Y", true))).Fingerprint());
        Assert.NotEqual(baseline, Selection(Set(("name = 'b' -> Y", true), ("name = 'a' -> X", true))).Fingerprint());
        Assert.NotEqual(baseline, Selection(Set(("name = 'a' -> X", false), ("name = 'b' -> Y", true))).Fingerprint());
        // an edit to a broken, disabled rule still counts
        Assert.NotEqual(Selection(Set(("zzz", false))).Fingerprint(), Selection(Set(("zzy", false))).Fingerprint());
    }

    [Fact]
    public void Race_options_count_only_when_a_rule_uses_race_even_with_the_master_off()
    {
        var off = RaceSplitOptions.Off;
        var changed = off with { MultiRaceToMultiRace = false };

        var usesRace = RuleSet.Compile([new RuleEntry("race = 'Viera' -> X", true)], off);
        var usesRace2 = RuleSet.Compile([new RuleEntry("race = 'Viera' -> X", true)], changed);
        Assert.NotEqual(Selection(usesRace).Fingerprint(), Selection(usesRace2).Fingerprint());

        var plain = RuleSet.Compile([new RuleEntry("name = 'a' -> X", true)], off);
        var plain2 = RuleSet.Compile([new RuleEntry("name = 'a' -> X", true)], changed);
        Assert.Equal(Selection(plain).Fingerprint(), Selection(plain2).Fingerprint());
    }

    [Fact]
    public void A_pipe_in_a_rule_cannot_make_two_rule_sets_collide()
    {
        Assert.NotEqual(
            Selection(Set(("name = 'a|b' -> X", true))).Fingerprint(),
            Selection(Set(("name = 'a' -> X", true), ("b' -> X", true))).Fingerprint());
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "OrganizerStateRulesTests|SortRulesFingerprintTests"`
Expected: build errors (`Rules` parameter, `StagedRule`, `LastSortSummary.Rules`).

- [ ] **Step 3: Model changes**

`LayoutOptions.cs`: add the parameter and a using:

```csharp
using PenumbraOrganizer.Plugin.Organizer.Rules;

public readonly record struct LayoutOptions(
    bool SharedHandsAndFeet = false, bool SplitHairByRace = false, bool SplitEmotes = false,
    RaceSplitOptions? Race = null, RuleSet? Rules = null);
```
Update the remarks: `Rules` is the Sort tab's keyword rule list; null or empty means none.

`OrganizerModRow.cs` add:

```csharp
    /// <summary>
    /// The keyword rule that chose this row's folder in the most recent sort (ordinal and text captured at
    /// staging time), or null. Cleared wherever <see cref="StagedRace"/> is.
    /// </summary>
    public StagedRule? StagedRule { get; set; }
```
and a new record (put at the bottom of `OrganizerModRow.cs`): `public sealed record StagedRule(int Ordinal, string Text);`

`SortSummary.cs`:

```csharp
public sealed record RuleSortSummary(int Applied, IReadOnlyList<string> SkippedRules, IReadOnlyList<string> Unplaced);

public sealed record SortSummary(
    SortScope Scope, int InScope, int Total, int ProtectedAtRoot, RuleSortSummary? Rules = null);
```
and in `Describe`, before `return text;`:

```csharp
        if (summary.Rules is { } rules)
        {
            if (rules.Applied > 0)
                text += $" {rules.Applied} filed by rules.";
            foreach (var skipped in rules.SkippedRules)
                text += $" Skipped {skipped}";
            foreach (var unplaced in rules.Unplaced)
                text += $" Left in place: {unplaced}";
        }
```
(`SkippedRules` entries read `Rule 3: <message> (at 12)`; `Unplaced` entries read `Rule 2 for "<mod>": <reason>`. Build them in `OrganizerState`.)

`OrganizerExportFormatter.cs` after the `StagedRace` lines (line 70-71):

```csharp
            if (mod.StagedRule is { } stagedRule)
                sb.AppendLine($"StagedRule: #{stagedRule.Ordinal} {stagedRule.Text}");
```

`SortPanel.Fingerprint()`: after computing the existing joined string, append the rules (so an empty list keeps today's exact string). Replace the `return string.Join(...)` with:

```csharp
        var core = string.Join(
            '|',
            Strategy, SplitGear, SplitNpc, SplitHair, HairRaceCode ?? "-", MidlanderFallback,
            SplitGearBody, SplitBodyMods, string.Join(',', bodyLabels),
            Layout.SharedHandsAndFeet, Layout.SplitHairByRace, Layout.SplitEmotes, raceText);

        if (Layout.Rules is not { HasRules: true } rules)
            return core;

        // The digest is length-prefixed and goes LAST, so no character inside a rule can blur two sets together.
        var rulesRace = rules.UsesRace
            ? $"{rules.Race.Enabled}:{(int)rules.Race.Rows}:{rules.Race.MultiRaceToMultiRace}:{string.Join(',', rules.Race.Priority.Ranked)}"
            : "-";
        return $"{core}|rr={rulesRace}|rules={rules.Digest}";
```

- [ ] **Step 4: Wire `OrganizerState.Sort`**

Add `using PenumbraOrganizer.Plugin.Organizer.Rules; using PenumbraOrganizer.Plugin.Organizer.Workbook;`. In the long `Sort(...)` overload, replace the body from the `StagedBodyPriority = ...` line through the `return Sort(row => {...}, scope);` with:

```csharp
        StagedBodyPriority = splitGearBody || splitBodyMods ? bodyPriority : null;

        // Keyword rules are matched ONCE per row, before race and body are staged and before the numbered
        // layouts are built: a ruled row takes no body or race, and must not take a layout number either.
        var rules = options?.Rules;
        var ruled = new Dictionary<OrganizerModRow, string>();
        var stay = new HashSet<OrganizerModRow>();
        var ruleSummary = rules is { AnyRuns: true } || rules is { Broken.Count: > 0 }
            ? new RuleSortSummaryBuilder(rules!)
            : null;

        foreach (var row in Mods)
        {
            row.StagedRule = null;
            if (row.Protected || IsResetByScope(row, scope) || rules is not { AnyRuns: true })
                continue;

            var raceOptions = rules.Race;
            var body = RuleFields.BodyLabel(row, bodyPriority, splitGearBody, splitBodyMods);
            var values = RuleFields.For(row, canonicalizeCreator, raceOptions, body);
            if (rules.Match(values) is not { } match)
                continue;

            var tokens = WorkbookTokens.For(row, canonicalizeCreator, raceOptions);
            var folder = DestinationResolver.ResolveRuleFolder(match.Rule.Rule!.FolderTemplate, tokens);
            if (folder.Destination is null)
            {
                stay.Add(row);
                ruleSummary?.Unplaced.Add($"Rule {match.Rule.Ordinal} for \"{row.Name}\": {folder.Error}");
                continue;
            }

            ruled[row] = folder.Destination;
            row.StagedRule = new StagedRule(match.Rule.Ordinal, match.Rule.Text);
            ruleSummary!.Applied++;
        }

        foreach (var row in Mods)
        {
            if (IsResetByScope(row, scope) || ruled.ContainsKey(row) || stay.Contains(row))
            {
                row.StagedBodyResolution = null;
                row.StagedRace = null;
                continue;
            }

            var applies = row.Category == ModCategory.Gear ? splitGearBody
                : row.Category == ModCategory.Body && splitBodyMods;
            row.StagedBodyResolution = applies && bodyPriority is not null
                ? BodyPriorityResolver.Resolve(row.BodyCandidates, bodyPriority, nameFirst: row.Category == ModCategory.Body)
                : null;

            row.StagedRace = RaceSplitRouting.Resolve(row, options?.Race, splitBodyMods);
        }

        // One numbered layout per anchor root. A tool's mods kept under their own folder (Heliosphere,
        // sort-inside) are numbered among themselves, so they do not leave gaps in the plain folders.
        // Ruled and left-in-place rows are not numbered.
        var layouts = _mods.Values.Where(m => !m.Protected && !ruled.ContainsKey(m) && !stay.Contains(m))
            .GroupBy(AnchorRootOf, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => (Gear: SortFolderSelectors.BuildGearLayout(g, splitGear, splitGearBody, bodyPriority, options),
                      Body: SortFolderSelectors.BuildBodyLayout(g, splitBodyMods, bodyPriority)),
                StringComparer.OrdinalIgnoreCase);

        var count = Sort(row =>
        {
            if (ruled.TryGetValue(row, out var ruleFolder))
                return (ruleFolder, null);

            var layout = layouts[AnchorRootOf(row)];
            return SortFolderSelectors.Select(strategy, splitGear, splitNpc, row, canonicalizeCreator,
                                              splitHair: splitHair, hairPriority: hairPriority,
                                              splitGearBody: splitGearBody, splitBodyMods: splitBodyMods,
                                              bodyPriority: bodyPriority, gearLayout: layout.Gear,
                                              bodyLayout: layout.Body, options: options);
        }, scope, stay, reserveProtected: ruled.Count > 0);

        if (ruleSummary is not null && LastSortSummary is { } last)
            LastSortSummary = last with { Rules = ruleSummary.Build() };
        return count;
```

Add the helper class inside `OrganizerState.cs` (private nested or file-scoped internal at the bottom):

```csharp
file sealed class RuleSortSummaryBuilder(RuleSet rules)
{
    public int Applied;
    public List<string> Unplaced { get; } = [];

    public RuleSortSummary Build() => new(
        Applied,
        rules.Broken.Select(r => $"Rule {r.Ordinal}: {r.Error!.Message}" + (r.Error.Position > 0 ? $" (at {r.Error.Position})" : string.Empty)).ToList(),
        Unplaced);
}
```
(`file` types cannot be used as a field type of a non-file class but local variables are fine, as used above.)

Change the private `Sort(Func<...> folderSelector, SortScope? scope)` signature to `Sort(Func<...> folderSelector, SortScope? scope, IReadOnlySet<OrganizerModRow>? stay = null, bool reserveProtected = false)`; inside the loop, right after the `IsResetByScope` block add:

```csharp
            if (stay is not null && stay.Contains(row))
            {
                row.ProposedPath = row.CurrentPath;
                untouchedPaths.Add(row.CurrentPath);
                continue;
            }
```
and change the finish call to `FinishProposals(touched, scope is null && !reserveProtected ? null : untouchedPaths);`. The `did-you-mean` text is in `Error.Message`, so `Broken_rules_are_skipped_and_listed` finds "Did you mean 'name'".

Clear `StagedRule` everywhere `StagedRace = null` is set: `AssignManual`, `ApplyTemplate`, `StageMoveUnder` (add `row.StagedRule = null;` next to each). Then `grep -n "StagedRace" PenumbraOrganizer.Plugin/Organizer/OrganizerState.cs` and open `ReplaceScanAtomically`: if scanned rows are the same objects (memory: "LoadScan resets ProposedPath and keeps the same row objects"), set `row.StagedRule = null` where it resets `ProposedPath`; if it creates new rows it needs nothing. Add a test `StagedRule_is_cleared_by_a_rescan` using `LoadScan` with the same row objects.

- [ ] **Step 5: Run the focused tests, then the whole suite**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter "OrganizerStateRulesTests|SortRulesFingerprintTests"` then `dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: PASS; total 2529 plus the new tests, none failing. If any existing Sort/fingerprint test fails, an empty-rule path changed behavior: fix the code, not the old test.

- [ ] **Step 6: Commit**

```bash
git add -A PenumbraOrganizer.Plugin PenumbraOrganizer.Plugin.Tests
git commit -m "feat: sort keyword rules wired into Sort, staged rule, summary and fingerprint"
```

---

### Task 5: Configuration, cache and selection factory

**Files:**
- Create: `Organizer/Rules/RuleSetCache.cs`
- Modify: `Configuration.cs`, `Windows/SortSelectionFactory.cs`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Rules/RulesConfigTests.cs`

**Interfaces:**
- Consumes: `RuleSet.Compile`, `RuleEntry`, `RaceSplitOptions.FromConfig`.
- Produces:
  - `Configuration.SortRules : List<SortRuleEntry>` (default empty); `sealed class SortRuleEntry { string Text = ""; bool Enabled = true; }`; `Configuration.SortRulesVersion : int` (not serialized, bumped by whoever edits the list).
  - `static class RuleSetCache { RuleSet For(Configuration config, RaceSplitOptions race); }`
  - `SortSelectionFactory.FromConfig` fills `LayoutOptions.Rules` from the cache.

- [ ] **Step 1: Write the failing tests**

```csharp
using PenumbraOrganizer.Plugin;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Rules;
using PenumbraOrganizer.Plugin.Windows;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Rules;

public class RulesConfigTests
{
    [Fact]
    public void Sort_rules_default_empty_and_survive_a_serializer_round_trip()
    {
        var config = new Configuration();
        Assert.Empty(config.SortRules);

        config.SortRules.Add(new SortRuleEntry { Text = "name = 'a' -> X", Enabled = false });
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(config);
        var back = Newtonsoft.Json.JsonConvert.DeserializeObject<Configuration>(json)!;
        Assert.Equal("name = 'a' -> X", back.SortRules.Single().Text);
        Assert.False(back.SortRules.Single().Enabled);
        Assert.DoesNotContain("SortRulesVersion", json);
    }

    [Fact]
    public void A_legacy_config_without_the_property_loads_an_empty_list()
    {
        var back = Newtonsoft.Json.JsonConvert.DeserializeObject<Configuration>("{}")!;
        Assert.Empty(back.SortRules);
    }

    [Fact]
    public void Cache_returns_the_same_instance_until_the_version_or_race_changes()
    {
        var config = new Configuration();
        config.SortRules.Add(new SortRuleEntry { Text = "name = 'a' -> X" });
        var race = RaceSplitOptions.Off;
        var first = RuleSetCache.For(config, race);
        Assert.Same(first, RuleSetCache.For(config, race));

        config.SortRules.Add(new SortRuleEntry { Text = "name = 'b' -> Y" });
        config.SortRulesVersion++;
        var second = RuleSetCache.For(config, race);
        Assert.NotSame(first, second);
        Assert.Equal(2, second.Rules.Count);

        Assert.NotSame(second, RuleSetCache.For(config, race with { MultiRaceToMultiRace = false }));
    }

    [Fact]
    public void FromConfig_carries_the_rules_and_an_empty_list_leaves_layout_rules_unset_or_empty()
    {
        var config = new Configuration();
        var empty = SortSelectionFactory.FromConfig(config, []);
        Assert.False(empty.Layout.Rules?.HasRules ?? false);

        config.SortRules.Add(new SortRuleEntry { Text = "name = 'a' -> X" });
        config.SortRulesVersion++;
        var withRules = SortSelectionFactory.FromConfig(config, []);
        Assert.Equal(1, withRules.Layout.Rules!.Rules.Count);
        Assert.NotEqual(empty.Fingerprint(), withRules.Fingerprint());
    }
}
```

Match the serializer the project really uses (`grep -rn "JsonConvert\|System.Text.Json" PenumbraOrganizer.Plugin.Tests | head`); if configuration tests use another helper, use that one.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter RulesConfigTests`
Expected: build errors.

- [ ] **Step 3: Implement**

`Configuration.cs`, next to `WorkbookCustomCodes`:

```csharp
    /// <summary>The Sort tab's keyword rules, in priority order. Empty means no rules.</summary>
    public List<SortRuleEntry> SortRules { get; set; } = [];

    /// <summary>
    /// Bumped by whoever edits <see cref="SortRules"/>; the compiled-rule cache keys on it so the Review tab's
    /// per-frame selection never re-parses. Not saved: a loaded config starts at 0 and the cache is cold.
    /// </summary>
    [Newtonsoft.Json.JsonIgnore]
    public int SortRulesVersion { get; set; }
```
and at the bottom beside `WorkbookCustomCode`:

```csharp
/// <summary>One saved Sort keyword rule. A plain class so any config serializer can read it.</summary>
public sealed class SortRuleEntry
{
    public string Text { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}
```
(Use `[JsonIgnore]` from whichever serializer namespace `Configuration.cs` already imports; if it imports none, keep Newtonsoft fully qualified as shown, which is what Dalamud's `IPluginConfiguration` serializes with.)

`RuleSetCache.cs`:

```csharp
namespace PenumbraOrganizer.Plugin.Organizer.Rules;

/// <summary>
/// One compiled <see cref="RuleSet"/>, reused until the config's rule version or the race options change.
/// The Review tab asks for the selection every frame, so parsing must never happen per call.
/// </summary>
public static class RuleSetCache
{
    private static readonly object Gate = new();
    private static Configuration? _config;
    private static int _version = -1;
    private static RaceSplitOptions? _race;
    private static RuleSet _set = RuleSet.Empty;

    public static RuleSet For(Configuration config, RaceSplitOptions race)
    {
        lock (Gate)
        {
            if (ReferenceEquals(_config, config) && _version == config.SortRulesVersion && race.Equals(_race))
                return _set;

            _set = config.SortRules.Count == 0
                ? RuleSet.Empty
                : RuleSet.Compile(config.SortRules.Select(r => new RuleEntry(r.Text, r.Enabled)).ToList(), race);
            _config = config;
            _version = config.SortRulesVersion;
            _race = race;
            return _set;
        }
    }
}
```

`SortSelectionFactory.FromConfig`: change the `new LayoutOptions(...)` to add `Rules: RuleSetCache.For(config, race)` where `race` is the already-computed `RaceSplitOptions.FromConfig(config)` (the full value, not `NullIfNoEffect()`): 

```csharp
            new LayoutOptions(
                config.SortSharedHandsFeet, race.InEffect(RaceRows.Hair, config.SortSplitBodyMods),
                config.SortSplitEmotes, race.NullIfNoEffect(),
                Rules: RuleSetCache.For(config, race)),
```
Add `using PenumbraOrganizer.Plugin.Organizer.Rules;`.

- [ ] **Step 4: Run tests and the full suite**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: PASS. The empty-list fingerprint tests from Task 4 plus the existing fingerprint tests confirm byte-identical output.

- [ ] **Step 5: Commit**

```bash
git add -A PenumbraOrganizer.Plugin PenumbraOrganizer.Plugin.Tests
git commit -m "feat: sort rules in configuration with a shared compiled cache"
```

---

### Task 6: Rules editor state, panel section and help

**Files:**
- Create: `Organizer/Rules/RulesEditor.cs`
- Modify: `Windows/SortPanel.cs`, `Resources/help-content.json`, `docs/USER_GUIDE.md`
- Test: `PenumbraOrganizer.Plugin.Tests/Organizer/Rules/RulesEditorTests.cs`

**Interfaces:**
- Consumes: `RuleParser`, `RuleSet`, `RuleFields`, `WorkbookTokens`, `DestinationResolver.ResolveRuleFolder`, `Configuration.SortRules`/`SortRulesVersion`.
- Produces: `sealed class RulesEditor` (pure, no ImGui):
  - `RulesEditor(IReadOnlyList<SortRuleEntry> saved, Func<long> clockMs)`
  - `IReadOnlyList<RuleRow> Rows` where `sealed class RuleRow { string Text; bool Enabled; }`
  - `bool CanAdd` (fewer than 100); `bool Add()`; `void Remove(int index)`; `bool MoveUp(int index)`; `bool MoveDown(int index)`; `void SetText(int index, string text)`; `void SetEnabled(int index, bool enabled)`
  - `RuleStatus Status(int index)` where `sealed record RuleStatus(bool IsError, string Text)`: error text `"<message> (at <pos>)"` or `"Wins for N mods"`/`"Wins for N mods"` pending text `"Counting..."`
  - `List<SortRuleEntry> ToConfig()`
  - `void Observe(IReadOnlyList<OrganizerModRow> rows, int scanGeneration, Func<string,string> canonicalize, RaceSplitOptions race, BodyPriority? priority, bool splitGearBody, bool splitBodyMods)` then `void Tick()` to publish a count after a 300 ms debounce.

- [ ] **Step 1: Write the failing tests**

```csharp
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Rules;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Rules;

public class RulesEditorTests
{
    private long _now;

    private RulesEditor Editor(params (string, bool)[] saved) =>
        new(saved.Select(s => new SortRuleEntry { Text = s.Item1, Enabled = s.Item2 }).ToList(), () => _now);

    private static OrganizerModRow Row(string id, string name, bool prot = false) => new()
    {
        Identifier = id, Name = name, Author = "Ann", CurrentPath = name, ProposedPath = name,
        Category = ModCategory.Gear, Protected = prot,
    };

    private void Observe(RulesEditor e, int gen, params OrganizerModRow[] rows) =>
        e.Observe(rows, gen, s => s, RaceSplitOptions.Off, null, false, false);

    [Fact]
    public void Add_remove_move_and_toggle()
    {
        var e = Editor(("name = 'a' -> A", true), ("name = 'b' -> B", true));
        Assert.True(e.Add());
        Assert.Equal(3, e.Rows.Count);
        e.SetText(2, "name = 'c' -> C");
        Assert.True(e.MoveUp(2));
        Assert.Equal("name = 'c' -> C", e.Rows[1].Text);
        Assert.False(e.MoveUp(0));
        Assert.True(e.MoveDown(0));
        Assert.Equal("name = 'c' -> C", e.Rows[0].Text);
        e.SetEnabled(0, false);
        Assert.False(e.ToConfig()[0].Enabled);
        e.Remove(0);
        Assert.Equal(2, e.Rows.Count);
    }

    [Fact]
    public void Add_stops_at_one_hundred_rules()
    {
        var e = Editor();
        for (var i = 0; i < 100; i++)
            Assert.True(e.Add());
        Assert.False(e.CanAdd);
        Assert.False(e.Add());
    }

    [Fact]
    public void Status_is_a_red_error_with_position_for_a_bad_rule()
    {
        var e = Editor(("nmae = 'a' -> X", true));
        var s = e.Status(0);
        Assert.True(s.IsError);
        Assert.Equal("Unknown field 'nmae'. Did you mean 'name'? (at 0)", s.Text);
    }

    [Fact]
    public void Wins_count_excludes_protected_mods_and_mods_an_earlier_rule_won_and_waits_for_the_debounce()
    {
        var e = Editor(("name contains 'boots' -> A", true), ("name contains 'boots' -> B", true));
        Observe(e, 1, Row("1", "Red Boots"), Row("2", "Blue Boots"), Row("3", "Prot Boots", prot: true));

        Assert.Equal("Counting...", e.Status(0).Text);
        _now += 299;
        e.Tick();
        Assert.Equal("Counting...", e.Status(0).Text);
        _now += 2;
        e.Tick();
        Assert.False(e.Status(0).IsError);
        Assert.Equal("Wins for 2 mods", e.Status(0).Text);
        Assert.Equal("Wins for 0 mods", e.Status(1).Text);
    }

    [Fact]
    public void A_change_in_any_input_restarts_the_count_and_a_stale_result_is_not_published()
    {
        var e = Editor(("name contains 'boots' -> A", true));
        Observe(e, 1, Row("1", "Boots"));
        _now += 400; e.Tick();
        Assert.Equal("Wins for 1 mods", e.Status(0).Text);

        e.SetText(0, "name contains 'zzz' -> A");
        Assert.Equal("Counting...", e.Status(0).Text);
        _now += 400; e.Tick();
        Assert.Equal("Wins for 0 mods", e.Status(0).Text);

        // a rescan (new generation) invalidates the count again
        Observe(e, 2, Row("1", "Boots"), Row("2", "zzz"));
        Assert.Equal("Counting...", e.Status(0).Text);
        _now += 400; e.Tick();
        Assert.Equal("Wins for 1 mods", e.Status(0).Text);
    }

    [Fact]
    public void Disabled_rules_show_off_and_do_not_win()
    {
        var e = Editor(("name contains 'boots' -> A", false), ("name contains 'boots' -> B", true));
        Observe(e, 1, Row("1", "Boots"));
        _now += 400; e.Tick();
        Assert.Equal("Off", e.Status(0).Text);
        Assert.Equal("Wins for 1 mods", e.Status(1).Text);
    }

    [Fact]
    public void A_rule_whose_folder_would_not_match_its_own_folder_condition_warns()
    {
        var e = Editor(("folder starts with 'Old' -> Old/Moved", true));
        Assert.Contains("matches its own", e.Warning(0));
        var ok = Editor(("folder starts with 'Old' -> New", true));
        Assert.Null(ok.Warning(0));
    }
}
```

The warning test needs a precise definition so it is testable: `Warning(int)` returns text when the rule's condition mentions `folder` and the expanded literal folder (tokens left unexpanded, so only rules whose folder has no `{...}` token are checked) would itself satisfy the same `folder` condition, because then the mod matches again after it moves (the rule keeps re-filing it). Add `string? Warning(int index)` to the interface, returning `"This rule's folder matches its own folder condition, so the mod would match it again after moving."`. Rewrite both assertions: the first rule above (`folder starts with 'Old' -> Old/Moved`) warns; the second (`-> New`) does not. Use `Assert.Contains("match", ...)`/`Assert.Null`.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter RulesEditorTests`
Expected: build errors.

- [ ] **Step 3: Implement `RulesEditor.cs`**

```csharp
using PenumbraOrganizer.Plugin.Organizer.BodyTypes;
using PenumbraOrganizer.Plugin.Organizer.Workbook;

namespace PenumbraOrganizer.Plugin.Organizer.Rules;

public sealed class RuleRow
{
    public string Text { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}

public sealed record RuleStatus(bool IsError, string Text);

/// <summary>
/// The Sort tab's rules list as plain state: edits, per-rule status and a debounced "Wins for N mods"
/// count. No ImGui, so it is tested directly. The count is recomputed only after text, order, enabled
/// flags, scan generation, race options, body priority or the creator canonicalizer change AND 300 ms
/// have passed, never per frame.
/// </summary>
public sealed class RulesEditor
{
    public const int DebounceMs = 300;

    private readonly List<RuleRow> _rows;
    private readonly Func<long> _clock;
    private readonly Dictionary<string, RuleParseResult> _parseCache = new(StringComparer.Ordinal);

    private IReadOnlyList<OrganizerModRow> _mods = [];
    private int _generation;
    private Func<string, string> _canonicalize = s => s;
    private RaceSplitOptions _race = RaceSplitOptions.Off;
    private BodyPriority? _priority;
    private bool _splitGearBody, _splitBodyMods;

    private long _inputsVersion;          // bumps on every change to anything the count depends on
    private long _countedVersion = -1;
    private long _dueAt;
    private int[] _wins = [];

    public RulesEditor(IReadOnlyList<SortRuleEntry> saved, Func<long> clockMs)
    {
        _clock = clockMs;
        _rows = saved.Select(s => new RuleRow { Text = s.Text, Enabled = s.Enabled }).ToList();
        Touch();
    }

    public IReadOnlyList<RuleRow> Rows => _rows;
    public bool CanAdd => _rows.Count < RuleSet.MaxRules;

    public List<SortRuleEntry> ToConfig() =>
        _rows.Select(r => new SortRuleEntry { Text = r.Text, Enabled = r.Enabled }).ToList();

    public bool Add()
    {
        if (!CanAdd) return false;
        _rows.Add(new RuleRow());
        Touch();
        return true;
    }

    public void Remove(int index)
    {
        if (index < 0 || index >= _rows.Count) return;
        _rows.RemoveAt(index);
        Touch();
    }

    public bool MoveUp(int index) => Move(index, index - 1);
    public bool MoveDown(int index) => Move(index, index + 1);

    private bool Move(int from, int to)
    {
        if (from < 0 || from >= _rows.Count || to < 0 || to >= _rows.Count) return false;
        (_rows[from], _rows[to]) = (_rows[to], _rows[from]);
        Touch();
        return true;
    }

    public void SetText(int index, string text)
    {
        if (index < 0 || index >= _rows.Count || _rows[index].Text == text) return;
        _rows[index].Text = text;
        Touch();
    }

    public void SetEnabled(int index, bool enabled)
    {
        if (index < 0 || index >= _rows.Count || _rows[index].Enabled == enabled) return;
        _rows[index].Enabled = enabled;
        Touch();
    }

    /// <summary>Tells the editor what the count depends on; a change in any of it restarts the debounce.</summary>
    public void Observe(
        IReadOnlyList<OrganizerModRow> rows, int scanGeneration, Func<string, string> canonicalize,
        RaceSplitOptions race, BodyPriority? priority, bool splitGearBody, bool splitBodyMods)
    {
        var changed = !ReferenceEquals(rows, _mods) && rows.Count != _mods.Count
            || scanGeneration != _generation
            || !race.Equals(_race)
            || !Equals(priority?.Labels is null ? null : string.Join(',', priority.Labels),
                       _priority?.Labels is null ? null : string.Join(',', _priority.Labels))
            || splitGearBody != _splitGearBody || splitBodyMods != _splitBodyMods
            || !ReferenceEquals(canonicalize, _canonicalize);

        _mods = rows;
        _generation = scanGeneration;
        _canonicalize = canonicalize;
        _race = race;
        _priority = priority;
        _splitGearBody = splitGearBody;
        _splitBodyMods = splitBodyMods;
        if (changed) Touch();
    }

    private void Touch()
    {
        _inputsVersion++;
        _dueAt = _clock() + DebounceMs;
    }

    /// <summary>Publishes the count once the debounce has passed. Cheap when nothing changed.</summary>
    public void Tick()
    {
        if (_countedVersion == _inputsVersion || _clock() < _dueAt) return;

        var version = _inputsVersion;
        var set = RuleSet.Compile(_rows.Select(r => new RuleEntry(r.Text, r.Enabled)).ToList(), _race);
        var wins = new int[_rows.Count];
        foreach (var row in _mods)
        {
            if (row.Protected) continue;
            var body = RuleFields.BodyLabel(row, _priority, _splitGearBody, _splitBodyMods);
            var values = RuleFields.For(row, _canonicalize, _race, body);
            if (set.Match(values) is { } match)
                wins[match.Rule.Ordinal - 1]++;
        }

        // Only publish if nothing changed while counting (always true single-threaded; kept so the same
        // check holds if the count is ever moved off the render thread).
        if (version != _inputsVersion) return;
        _wins = wins;
        _countedVersion = version;
    }

    public RuleStatus Status(int index)
    {
        var row = _rows[index];
        var parsed = Parse(row.Text);
        if (parsed.Error is { } error)
            return new RuleStatus(true, $"{error.Message} (at {error.Position})");
        if (!row.Enabled)
            return new RuleStatus(false, "Off");
        if (_countedVersion != _inputsVersion || index >= _wins.Length)
            return new RuleStatus(false, "Counting...");
        return new RuleStatus(false, $"Wins for {_wins[index]} mods");
    }

    /// <summary>
    /// Non-null when the rule tests `folder` and its own literal folder (no tokens) would satisfy that same
    /// test, so the mod matches it again after it has moved there.
    /// </summary>
    public string? Warning(int index)
    {
        if (Parse(_rows[index].Text).Rule is not { } rule || !MentionsFolder(rule.Condition)) return null;
        var folder = DestinationResolver.NormalizePath(rule.FolderTemplate);
        if (folder.Contains('{')) return null;

        var values = new RuleFieldValues(string.Empty, string.Empty, folder, string.Empty, string.Empty, string.Empty, string.Empty);
        return RuleEvaluator.Matches(rule.Condition, values)
            ? "This rule's folder matches its own folder condition, so the mod would match it again after moving."
            : null;
    }

    private static bool MentionsFolder(RuleExpr e) => e switch
    {
        RuleCompare c => c.Field == RuleField.Folder,
        RuleNot n => MentionsFolder(n.Inner),
        RuleAnd a => MentionsFolder(a.Left) || MentionsFolder(a.Right),
        RuleOr o => MentionsFolder(o.Left) || MentionsFolder(o.Right),
        _ => false,
    };

    private RuleParseResult Parse(string text)
    {
        if (!_parseCache.TryGetValue(text, out var result))
        {
            if (_parseCache.Count > 512) _parseCache.Clear();
            result = RuleParser.Parse(text);
            _parseCache[text] = result;
        }

        return result;
    }
}
```

`Observe`'s `changed` expression for `rows` is deliberately loose; simplify it to `scanGeneration != _generation || rows.Count != _mods.Count || ...` (the scan generation is the real key). Use `rows.Count != _mods.Count` for the first term.

- [ ] **Step 4: Run tests**

Run: `dotnet test PenumbraOrganizer.Plugin.Tests --filter RulesEditorTests`
Expected: PASS. If `BodyPriority.Labels` is not a string list, adapt the comparison (`SortSelection.Fingerprint` uses `BodyPriority?.Labels` as `IEnumerable<string>`, so it is).

- [ ] **Step 5: SortPanel section (ImGui, no unit test; verified by build and the in-game checklist)**

Read the end of `SortPanel.Draw` (`grep -n "Draw\|CollapsingHeader\|ImGui.Checkbox" PenumbraOrganizer.Plugin/Windows/SortPanel.cs`) and add, after the split checkboxes and before the Sort button:

1. Fields: `private RulesEditor? _rules;` and `private int _rulesEditing = -1;`.
2. A method `DrawRules(Configuration config, Action saveConfig, OrganizerState state, Func<string,string> canonicalize)` called once per frame from `Draw`:

```csharp
    private void DrawRules(Configuration config, Action saveConfig, OrganizerState state, Func<string, string> canonicalize)
    {
        _rules ??= new RulesEditor(config.SortRules, () => Environment.TickCount64);
        if (!ImGui.CollapsingHeader($"Rules ({_rules.Rows.Count})##sortRules"))
            return;

        var race = RaceSplitOptions.FromConfig(config);
        _rules.Observe(state.Mods, state.ScanGeneration, canonicalize, race,
            BodyPriority.Repair(config.BodyPriority, state.BodyLabels ?? [], out _),
            config.SortSplitGearBody, config.SortSplitBodyMods);
        _rules.Tick();

        ImGui.TextDisabled("condition -> folder. The first matching rule files the mod; unmatched mods sort as usual.");
        var persist = false;
        for (var i = 0; i < _rules.Rows.Count; i++)
        {
            ImGui.PushID(i);
            var row = _rules.Rows[i];
            var enabled = row.Enabled;
            if (ImGui.Checkbox("##on", ref enabled)) { _rules.SetEnabled(i, enabled); persist = true; }
            ImGui.SameLine();
            var text = row.Text;
            ImGui.SetNextItemWidth(-130);
            if (ImGui.InputText("##text", ref text, RuleParser.MaxLength * 4))   // bytes, not characters
                _rules.SetText(i, text);
            if (ImGui.IsItemDeactivatedAfterEdit()) persist = true;
            ImGui.SameLine();
            if (ImGui.SmallButton("Up") && _rules.MoveUp(i)) persist = true;
            ImGui.SameLine();
            if (ImGui.SmallButton("Dn") && _rules.MoveDown(i)) persist = true;
            ImGui.SameLine();
            if (ImGui.SmallButton("X")) { _rules.Remove(i); persist = true; ImGui.PopID(); break; }

            var status = _rules.Status(i);
            if (status.IsError) ImGui.TextColored(new System.Numerics.Vector4(1f, 0.35f, 0.35f, 1f), status.Text);
            else ImGui.TextDisabled(status.Text);
            if (_rules.Warning(i) is { } warning)
                ImGui.TextColored(new System.Numerics.Vector4(1f, 0.8f, 0.3f, 1f), warning);
            ImGui.PopID();
        }

        if (ImGui.Button("Add rule") && _rules.Add()) persist = true;
        if (!_rules.CanAdd) { ImGui.SameLine(); ImGui.TextDisabled("100 rules is the limit."); }

        if (persist)
        {
            config.SortRules = _rules.ToConfig();
            config.SortRulesVersion++;
            saveConfig();
        }
    }
```
`state.ScanGeneration` does not exist yet: add `public int ScanGeneration { get; private set; }` to `OrganizerState`, incremented at the end of `ReplaceScanAtomically` (after the swap succeeds). Add a one-line test in `OrganizerStateRulesTests`: two `LoadScan` calls give a larger `ScanGeneration`. Use the `Draw` method's existing parameter names for `config`, `saveConfig`, `state`, `canonicalize` (read the signature; adapt, do not invent). Do not call `DrawRules` while the Sort tab shows an activity gate if the existing code disables the controls then; mirror how the checkboxes are gated (`ImGui.BeginDisabled`).

3. `PersistSortChoices` must not compare the rule list (rules persist on edit deactivation only), so no change there.

- [ ] **Step 6: Help topic and user guide**

Open `Resources/help-content.json`, copy the structure of an existing topic exactly (id, title, body shape), and add a topic `sort-rules` titled "Sort rules". Body (plain text the file's format allows): what a rule is; the example `name contains 'hrothgar' and type = 'Body' -> Body/Hrothgar/{creator}`; the seven fields with the valid `type` values (Gear, Hair, Face, Skin, Body, NPC, Animation and VFX, Review, plus the others in `WorkbookCatalog` first segments: list them by reading `WorkbookCatalog.cs`); operators; `{type}`, `{creator}`, `{race}`; "first match wins, protected mods are never touched, a rule's folder replaces the whole path, rules apply to every Sort including the automatic new-mod sort, but not to template fallback". Add a matching `docs/USER_GUIDE.md` section "Sort rules". Run the help-content tests (`dotnet test --filter Help`) to make sure the JSON still parses and topic ids are unique.

- [ ] **Step 7: Build and full test run, then commit**

Run: `dotnet build PenumbraOrganizer.Plugin.sln -c Release` then `dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: build succeeds, tests pass.

```bash
git add -A PenumbraOrganizer.Plugin PenumbraOrganizer.Plugin.Tests docs
git commit -m "feat: Rules section on the Sort tab with live status, count and help"
```

---

### Task 7: Harness pass, in-game checklist, wrap-up

**Files:**
- Modify: `PenumbraOrganizer.Plugin.Tests/Harness/RealLibraryHarnessTests.cs` (exists only on the unmerged `test/real-library-harness` branch of PR 8; if it is not on this branch, do this step after PR 8 merges, or cherry-pick that commit first, and say so in the report)
- Create: `docs/testing/2026-11-23-sort-rules-checklist.md`
- Memory: add `sort-keyword-rules-status.md` and an index line.

- [ ] **Step 1: Harness rules pass**

In the harness (opt-in via `PENUMBRA_HARNESS_LIBS`, returns immediately when unset), after the existing Sort variants add a pass that builds a `RuleSet` of five example rules (`name contains 'hair' -> Hair/By name`, `author = '<first author in the library>' -> {creator}/{type}`, `type = 'Gear' and subtype in ('Top','Legs') -> Gear/Outfits`, `race = '' and type = 'Body' -> Body/Unknown race`, `not type in ('Gear','Hair') and name starts with 'a' -> A/{type}`), calls `OrganizerState.Sort` with `LayoutOptions(Rules: set)`, and records per rule: wins, and total time. Assert: no exception, every ruled row's `ProposedPath` is non-empty and has no `..`, an empty `RuleSet` gives the same proposals as no rules (compare path lists), and at 2,557 mods the pass takes under 2 seconds. Print the match counts and timing into the test output the way the existing harness prints.

- [ ] **Step 2: Checklist**

Write `docs/testing/2026-11-23-sort-rules-checklist.md` with unticked in-game items: type a bad rule and see the red message and position; fix it and see "Wins for N mods" after a pause; the count updates after a rescan; toggle a rule off ("Off") and on; Up/Dn reorder changes which rule wins; Sort files a matching mod at the rule's folder and leaves protected mods alone; Review tab diagnostics export shows `StagedRule:`; Sort summary lists a skipped broken rule; Review tab "As suggested by Sort" turns unavailable after editing a rule and available after Sorting again; restart the plugin and the rules are still there; a rule using `{race}` with the race master off still works; a folder rule that matches its own folder shows the amber warning; the new-mod popup sort applies a rule.

- [ ] **Step 3: Final verification**

Run: `dotnet build PenumbraOrganizer.Plugin.sln -c Release` and `dotnet test PenumbraOrganizer.Plugin.Tests`
Expected: all green, count = 2529 plus the new tests. No version number changed (`git diff main --stat -- '*.csproj' '*.json' | grep -i version` shows nothing relevant).

- [ ] **Step 4: Memory and commit**

Write the memory file `sort-keyword-rules-status.md` (project type): branch, spec/plan paths, what is verified (unit tests, harness) and what is not (in-game checklist), the decisions (first match consumes the mod, rules apply to new-mod sort, not to templates). Add the one-line pointer to `MEMORY.md`.

```bash
git add docs/testing/2026-11-23-sort-rules-checklist.md PenumbraOrganizer.Plugin.Tests
git commit -m "test: rules pass in the real-library harness and an in-game checklist"
```

Then use superpowers:finishing-a-development-branch. Do not release or announce.

---

## Self-Review (done while writing)

- **Spec coverage:** language and errors (Task 1); fields, matching semantics (Task 2); rule set, equality, digest, folder expansion rules (Task 3); first-match consumes, invalid expansion stays put, matching before staging and layouts, `StagedRule`, summary, diagnostics, protected-path reservation, anchor prefix and Proteus-at-root (through the existing `ToolAdjustedPath`), scoped and new-mod runs (Task 4; new-mod uses the same `SortRunner` so it needs no extra code, covered by the factory test in Task 5); fingerprint with race-when-used (Task 4); config, cache, no per-frame parse, load-time limit (Tasks 5, 3); panel, debounced "Wins" count, warning, persist on deactivation, help (Task 6); harness and checklist (Task 7). Template planner is untouched by design.
- **Known soft spots for the implementer:** the parser's hand-counted error offsets in the Task 1 table must be reconciled against the real code by recounting the test strings (fix tests, not grammar); `SortScope`'s root-only factory name and `SortPanel.Draw`'s parameter names must be read, not guessed; the new-mod pipeline test is not written as code because `PluginNewModHost` takes host dependencies, so add one only if its existing tests show an easy seam (otherwise rely on the shared `SortRunner` and the checklist item).
- **Types consistent:** `RuleSet.Compile(IReadOnlyList<RuleEntry>, RaceSplitOptions)`, `RuleMatch.Rule.Ordinal/Text/Rule`, `StagedRule(int, string)`, `RuleSortSummary(int, IReadOnlyList<string>, IReadOnlyList<string>)`, `DestinationResolver.ResolveRuleFolder(string, DestinationTokens)`, `RuleSetCache.For(Configuration, RaceSplitOptions)` are used identically in every task.

---

## Review amendments (Opus and GPT-6.2 Astra, 2026-11-23)

Both reviews were checked against the code. **These amendments override the task text above wherever they differ.** Do each one inside the task named in brackets.

**R1 [Task 5] The panel builds its own selection.** `SortPanel` (around lines 389 to 397) constructs `new LayoutOptions(...)` itself, so Sort would run without rules and the workbook gate (which compares the panel's fingerprint with `FromConfig(...).Fingerprint()`) would stay locked once any rule exists. Add `public static RuleSet? RulesFor(Configuration config)` to `SortSelectionFactory`: `var set = RuleSetCache.For(config, RaceSplitOptions.FromConfig(config)); return set.HasRules ? set : null;`. Use it in `FromConfig` AND in the panel's `LayoutOptions` (`Rules: SortSelectionFactory.RulesFor(config)`). Add a test that the panel-built selection and `FromConfig` give the same fingerprint when rules exist (extract the panel's selection building into a public static method if needed, as `FromConfig` already is).

**R2 [Task 5] Empty list must leave `Layout` equal to default.** `FromConfig` must pass `Rules: RulesFor(config)` (null when there are no rules), never `RuleSet.Empty`, or the existing `SortSelectionFactoryTests` (`Assert.Equal(default, s.Layout)`) fail. Change the Task 5 test accordingly (`Assert.Null(empty.Layout.Rules)`).

**R3 [Task 4] Protected rows in tests.** `LoadScan` recomputes `Protected`. In the test helper `State(...)` call `state.LoadScan(rows, rows.Where(r => r.Protected).Select(r => r.Identifier).ToHashSet())`, and assert the fixture row is `Protected` before sorting. The collision test then expects the ruled row at `R/Boots (2)`.

**R4 [Task 4] Numbered gear test.** Gear body folders start at 10: assert `StartsWith("Gear/10. Gen3/", kept)` (it would be `Gear/11.` if the ruled row were still counted) and that the ruled row is `Out/Ruled`.

**R5 [Task 6] `ScanGeneration` already exists** (`OrganizerState.cs`, incremented on a successful publish). Delete the instruction to add it and its test; reuse it.

**R6 [Task 6] Observe must not restart every frame.** The Sort tab passes `_creatorCanonicalizer.Canonicalize` (an instance method), which makes a new delegate each frame, so `!ReferenceEquals(canonicalize, _canonicalize)` never settles. Use `!Equals(canonicalize, _canonicalize)` (delegate value equality). Also do NOT call `BodyPriority.Repair(...)` per frame in `DrawRules` (with an empty label list it drops every entry): pass the panel's own `_bodyPriority` and compare priorities with their `Labels` sequence. Add a test where `Observe` is called twice with two separately created but equal delegates and the debounce still completes.

**R7 [Task 6] The warning is the opposite case.** Spec section 7: warn when the rule's literal folder would NOT satisfy its own `folder` condition (the mod leaves the condition, so the next Sort re-files it elsewhere). Invert the check in `RulesEditor.Warning` and its message ("This rule's folder no longer matches its own folder condition, so the next Sort may file the mod somewhere else."), and in the test `-> New` warns and `-> Old/Moved` does not (for `folder starts with 'Old'`).

**R8 [Task 1] Four parser positions.** Use `name contains '' -> X` at 14, `name starts with '' -> X` at 17, `name ends with '' -> X` at 15 and `name in ('a',) -> X` at 13 (the `)` token). All messages stay as written. Every other row was hand-traced correct.

**R9 [Task 4] Root-only scope.** Use `new SortScope(RootOnly: true)` (or `SortPanel.ScopeFor(true)`); there is no `RootOnlyScope()`.

**R10 [Task 5] Config serializer.** The test project uses `System.Text.Json` (see `ConfigurationTests`), not Newtonsoft. Put both `[Newtonsoft.Json.JsonIgnore]` and `[System.Text.Json.Serialization.JsonIgnore]` on `SortRulesVersion` (the file already does this for other properties), and write the round-trip tests with `System.Text.Json`.

**R11 [Task 6] Help content tests.** `HelpContentTests` reject an id with no `HelpTopics` constant, require every "short" to be listed in `HelpTopicUsage.ReferencedByControls`, and require ASCII, shorts of at most 200 characters, and no newline in a short. Add the `HelpTopics` constant for `sort-rules`, a short on the Rules header with its `ReferencedByControls` entry, and keep all help text ASCII.

**R12 [Task 5] Cache must not be one static slot.** xUnit runs classes in parallel and `FromConfig` is called by many tests, so a single static entry makes `Assert.Same` flaky. Key the cache per config with `ConditionalWeakTable<Configuration, Entry>` (Entry holds version, race, set), under a per-entry lock. The Task 5 test then uses its own `Configuration` instances.

**R13 [Task 6/7] Spec items the plan missed.** (a) Review row tooltip for `StagedRule`: find where the Review tab shows a row's staged race (`grep -n "StagedRace" Windows/`) and add the rule beside it. (b) Warn in the rule row when the rule's literal folder sits under a `FolderProtectionRules` protect entry (the panel has the rules via the state; add `string? ProtectionWarning(int index, Func<string,bool> isProtectedFolder)` to `RulesEditor` and a test). (c) The panel reads the shared compiled set for matching through `RuleSetCache`; the editor keeps its own compile only for the live text being edited, which is deliberate (it must show unsaved text). (d) Add tests: a rule wins under `CreatorOnly`; a rule for a sort-inside-anchored row gives `Anchor/<rule folder>/<leaf>`; a Proteus-managed row with managed-at-root on is left at root and gets NO `StagedRule` (see R15); the numbered Body layout skips ruled rows; root-only and new-mod runs apply rules; the Review per-frame gate does no parsing (call `FromConfig` twice and assert the same `RuleSet` instance); `Type_merges_vfx` asserts a VFX row gives `Animation and VFX`.

**R14 [Task 4] Sort-twice test must be able to fail.** Replace the weak `NotEqual("Old/Boots")` with: after the move, a no-rule sort of the moved row gives path X; assert the second pass with rules equals X when the rule no longer matches, and use a second rule `folder starts with 'New' -> New` (its folder satisfies its own condition) and assert the path is unchanged across two sorts.

**R15 [Task 4] Smaller fixes.** (a) Skip rule matching for rows with `ProteusRole.Managed` while `_toolPolicy.ProteusManagedAtRoot` is on (the tool placement overrides any path, so no `StagedRule` and no "applied" count). (b) Reserve protected paths when `ruled.Count > 0 || stay.Count > 0`. (c) State in the code comment on that reservation that, in a whole-library sort with any rule firing, other mods also see protected paths reserved (renamed instead of caught later by `Validate`); this is accepted. (d) `Unplaced` in the Sort summary shows the count and the first 3 entries ("and N more") on the summary line; the full list goes in the diagnostics export under a `RulesUnplaced:` line.
