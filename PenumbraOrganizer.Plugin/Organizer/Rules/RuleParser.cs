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
