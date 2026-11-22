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
