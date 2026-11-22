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
