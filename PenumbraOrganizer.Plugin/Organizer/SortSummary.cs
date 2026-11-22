namespace PenumbraOrganizer.Plugin.Organizer;

/// <summary>What the last sort covered: its scope, mods it sorted, mods in the library, protected root mods left alone.</summary>
/// <summary>What the keyword rules did in the last sort.</summary>
public sealed record RuleSortSummary(int Applied, IReadOnlyList<string> SkippedRules, IReadOnlyList<string> Unplaced);

public sealed record SortSummary(
    SortScope Scope, int InScope, int Total, int ProtectedAtRoot, RuleSortSummary? Rules = null);

public static class SortSummaryText
{
    private const int UnplacedShown = 3;

    public static string Describe(SortSummary summary)
    {
        var where = summary.Scope.RootOnly ? "loose in root" : "whole library";
        var text = $"Will sort {summary.InScope} of {summary.Total} mods: {where}.";
        if (summary.Scope.RootOnly && summary.ProtectedAtRoot > 0)
        {
            text += summary.ProtectedAtRoot == 1
                ? " 1 protected root mod stays put."
                : $" {summary.ProtectedAtRoot} protected root mods stay put.";
        }

        if (summary.Rules is { } rules)
        {
            if (rules.Applied > 0)
                text += $" {rules.Applied} filed by rules.";
            foreach (var skipped in rules.SkippedRules)
                text += $" Skipped {skipped}";
            if (rules.Unplaced.Count > 0)
            {
                text += $" {rules.Unplaced.Count} left in place by rules: {string.Join(" ", rules.Unplaced.Take(UnplacedShown).Select(u => u.TrimEnd('.') + "."))}";
                if (rules.Unplaced.Count > UnplacedShown)
                    text += $" and {rules.Unplaced.Count - UnplacedShown} more (see diagnostics).";
            }
        }

        return text;
    }
}
