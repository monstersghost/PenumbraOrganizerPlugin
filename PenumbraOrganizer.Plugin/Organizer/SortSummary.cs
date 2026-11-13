namespace PenumbraOrganizer.Plugin.Organizer;

/// <summary>What the last sort covered: its scope, mods it sorted, mods in the library, protected root mods left alone.</summary>
public sealed record SortSummary(SortScope Scope, int InScope, int Total, int ProtectedAtRoot);

public static class SortSummaryText
{
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

        return text;
    }
}
