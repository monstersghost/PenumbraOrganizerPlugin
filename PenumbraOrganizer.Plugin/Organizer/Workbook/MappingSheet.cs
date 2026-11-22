using System.Globalization;
using System.Text.RegularExpressions;

namespace PenumbraOrganizer.Plugin.Organizer.Workbook;

/// <summary>
/// One data row of the Category Mapping sheet, as text. The file layer produces these. The column letters
/// are where the code and folder were found (they are discovered by header), so errors name the real cell.
/// </summary>
public sealed record MappingSheetRow(
    int RowNumber, string CodeText, string Name, string Folder, bool HasFormula,
    string CodeColumn = "A", string FolderColumn = "D");

public sealed record CustomCode(int Code, string Folder);

/// <summary>
/// The whole mapping table, validated. <see cref="Custom"/> holds only codes with exactly one valid
/// definition; <see cref="Unusable"/> says why a code that appeared cannot be used.
/// </summary>
public sealed record MappingParseResult(
    IReadOnlyList<CustomCode> Custom,
    IReadOnlyDictionary<int, string> Unusable,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings)
{
    public bool HasErrors => Errors.Count > 0;

    public string? FolderFor(int code) => Custom.FirstOrDefault(c => c.Code == code)?.Folder;
}

/// <summary>Pure parser for the Category Mapping sheet (workbook spec, section 3).</summary>
public static class MappingSheet
{
    public const string SheetName = "Category Mapping";
    public const int FirstBuiltInRow = 6;
    public const int FirstCustomRow = 22;
    public const int FirstCustomCode = 17;
    public const int LastCustomCode = 9999;
    public const int MaxCustomRows = 200;

    // A whole number as Excel may have stored it: "17", "0017", "17.0", with surrounding spaces.
    private static readonly Regex CodePattern = new(
        @"^\s*([0-9]{1,5})(\.0+)?\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static bool IsBuiltInRow(int row) =>
        row >= FirstBuiltInRow && row < FirstBuiltInRow + WorkbookCatalog.BuiltIn.Count;

    public static MappingParseResult Parse(IReadOnlyList<MappingSheetRow> rows)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var definitions = new Dictionary<int, List<(int Row, string? Folder)>>();
        var invalidReasons = new Dictionary<int, string>();
        var customRows = 0;

        foreach (var row in rows)
        {
            // Rows 6-21 are the built-in rows, by position: whatever the code cell now says, they are never
            // definitions, and a cleared or changed value warns.
            if (IsBuiltInRow(row.RowNumber))
            {
                if (row.HasFormula)
                    errors.Add($"{SheetName}!{row.CodeColumn}{row.RowNumber}: this row contains a formula. Replace it with plain text before importing.");
                else
                    WarnIfBuiltInEdited(row, warnings);
                continue;
            }

            if (row.HasFormula)
            {
                if (!string.IsNullOrWhiteSpace(row.CodeText) || !string.IsNullOrWhiteSpace(row.Folder) || !string.IsNullOrWhiteSpace(row.Name))
                    errors.Add($"{SheetName}!{row.CodeColumn}{row.RowNumber}: this row contains a formula. Replace it with plain text before importing.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(row.CodeText) && string.IsNullOrWhiteSpace(row.Folder))
                continue;

            var match = CodePattern.Match(row.CodeText ?? string.Empty);
            if (!match.Success
                || !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var code))
            {
                errors.Add($"{SheetName}!{row.CodeColumn}{row.RowNumber}: \"{row.CodeText}\" is not a whole number from {FirstCustomCode} to {LastCustomCode}.");
                continue;
            }

            if (code is >= 1 and < FirstCustomCode)
            {
                errors.Add($"{SheetName}!{row.CodeColumn}{row.RowNumber}: code {code} is built in and cannot be redefined. Use {FirstCustomCode} or higher.");
                continue;
            }

            if (code is < FirstCustomCode or > LastCustomCode)
            {
                errors.Add($"{SheetName}!{row.CodeColumn}{row.RowNumber}: code {code} is outside {FirstCustomCode} to {LastCustomCode}.");
                continue;
            }

            customRows++;
            if (!definitions.TryGetValue(code, out var list))
                definitions[code] = list = [];

            var normalized = DestinationResolver.NormalizePath(row.Folder ?? string.Empty);
            if (normalized.Length == 0)
            {
                errors.Add($"{SheetName}!{row.FolderColumn}{row.RowNumber}: the folder is empty for code {code}.");
                invalidReasons.TryAdd(code, $"code {code} has an empty folder");
                list.Add((row.RowNumber, null));
            }
            else if (DestinationResolver.StructureError(normalized) is { } structure)
            {
                errors.Add($"{SheetName}!{row.FolderColumn}{row.RowNumber}: {structure}");
                invalidReasons.TryAdd(code, $"code {code} has an invalid folder: {structure}");
                list.Add((row.RowNumber, null));
            }
            else if (DestinationResolver.UnknownTokens(normalized) is { Count: > 0 } unknown)
            {
                errors.Add($"{SheetName}!{row.FolderColumn}{row.RowNumber}: unknown token {{{unknown[0]}}}. Use {{type}}, {{creator}} or {{race}}.");
                invalidReasons.TryAdd(code, $"code {code} has an unknown token {{{unknown[0]}}}");
                list.Add((row.RowNumber, null));
            }
            else
            {
                list.Add((row.RowNumber, normalized));
            }
        }

        var overCap = customRows > MaxCustomRows;
        if (overCap)
            errors.Add($"{SheetName}: more than {MaxCustomRows} custom categories (found {customRows}). None of them were used.");

        var custom = new List<CustomCode>();
        var unusable = new Dictionary<int, string>();
        foreach (var (code, defs) in definitions.OrderBy(pair => pair.Key))
        {
            if (overCap)
            {
                unusable[code] = $"the table has more than {MaxCustomRows} custom categories";
                continue;
            }

            if (defs.Count > 1)
            {
                unusable[code] = $"code {code} is defined more than once";
                foreach (var def in defs)
                    errors.Add($"{SheetName}!A{def.Row}: code {code} is used twice.");
                continue;
            }

            if (defs[0].Folder is null)
            {
                unusable[code] = invalidReasons.TryGetValue(code, out var reason) ? reason : $"code {code} has an invalid folder";
                continue;
            }

            custom.Add(new CustomCode(code, defs[0].Folder!));
        }

        return new MappingParseResult(custom, unusable, errors, warnings);
    }

    private static void WarnIfBuiltInEdited(MappingSheetRow row, List<string> warnings)
    {
        var builtIn = WorkbookCatalog.BuiltIn[row.RowNumber - FirstBuiltInRow];
        var changes = new List<string>();

        var codeText = (row.CodeText ?? string.Empty).Trim();
        if (codeText != builtIn.Code.ToString(CultureInfo.InvariantCulture))
            changes.Add($"code '{builtIn.Code}' to '{codeText}'");

        var name = (row.Name ?? string.Empty).Trim();
        if (!name.Equals(builtIn.Name, StringComparison.OrdinalIgnoreCase))
            changes.Add($"name '{builtIn.Name}' to '{name}'");

        var folder = DestinationResolver.NormalizePath(row.Folder ?? string.Empty);
        if (!folder.Equals(builtIn.Folder, StringComparison.OrdinalIgnoreCase))
            changes.Add($"folder '{builtIn.Folder}' to '{folder}'");

        if (changes.Count > 0)
            warnings.Add(
                $"{SheetName} row {row.RowNumber}: built-in code {builtIn.Code} was edited ({string.Join(", ", changes)}); the built-in value was used.");
    }
}

/// <summary>Describes how an accepted import changed the saved custom categories.</summary>
public static class WorkbookSeed
{
    public static string? Describe(IReadOnlyList<CustomCode> before, IReadOnlyList<CustomCode> after)
    {
        // A corrupt saved list may repeat a code: the first one counts.
        var oldByCode = before.GroupBy(c => c.Code).ToDictionary(g => g.Key, g => g.First().Folder);
        var newByCode = after.GroupBy(c => c.Code).ToDictionary(g => g.Key, g => g.First().Folder);

        var added = newByCode.Keys.Count(code => !oldByCode.ContainsKey(code));
        var removed = oldByCode.Keys.Count(code => !newByCode.ContainsKey(code));
        var changed = newByCode.Count(pair => oldByCode.TryGetValue(pair.Key, out var folder)
            && !string.Equals(folder, pair.Value, StringComparison.Ordinal));

        if (added == 0 && removed == 0 && changed == 0)
            return null;

        var noun = newByCode.Count == 1 ? "custom category" : "custom categories";
        return $"Saved {newByCode.Count} {noun}: {added} added, {changed} changed, {removed} removed.";
    }
}
