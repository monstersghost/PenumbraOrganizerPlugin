namespace PenumbraOrganizer.Plugin.Organizer.Workbook;

using ClosedXML.Excel;
using System.IO.Compression;
using Microsoft.Extensions.Logging;
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Core.Identity;
using PenumbraOrganizer.Core.Interfaces;
using PenumbraOrganizer.Core.Models;

public sealed class WorkbookService
{
    private const string EditableSheetName = "Edit Destinations";
    private const string CategorySheetName = "Category Mapping";
    private const string MetadataSheetName = "_Metadata";
    private readonly ICreatorCanonicalizer _creatorCanonicalizer;
    private readonly ILogger<WorkbookService> _logger;

    public WorkbookService(ICreatorCanonicalizer creatorCanonicalizer, ILogger<WorkbookService> logger)
    {
        _creatorCanonicalizer = creatorCanonicalizer;
        _logger = logger;
    }

    public Task<WorkbookExportResult> ExportAsync(
        ScanInventory inventory,
        IReadOnlyList<OrganizerModProposal> proposals,
        OrganizationPreferences organizationPreferences,
        string workbookPath,
        CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var workbookDirectory = Path.GetDirectoryName(workbookPath);
            if (string.IsNullOrWhiteSpace(workbookDirectory))
                throw new InvalidOperationException("The workbook export path is invalid.");
            Directory.CreateDirectory(workbookDirectory);

            var generatedAtUtc = DateTimeOffset.UtcNow;
            var sourceExportId = $"workbook-{generatedAtUtc:yyyyMMddTHHmmssZ}-{Guid.NewGuid():N}";
            var scanIdentity = ScanIdentity.BuildScanIdentity(inventory);
            var installationIdentity = ScanIdentity.BuildInstallationIdentity(inventory.Installation);

            using var workbook = new XLWorkbook();
            BuildEditableSheet(workbook, inventory, proposals, organizationPreferences);
            BuildCategorySheet(workbook);
            BuildMetadataSheet(workbook, sourceExportId, generatedAtUtc, scanIdentity, installationIdentity, organizationPreferences);
            workbook.SaveAs(workbookPath);

            var strategyLabel = StrategyLabel(organizationPreferences.Strategy);
            var summary = $"Workbook exported with {inventory.Mods.Count} mod row(s) using {strategyLabel} suggestions.";
            _logger.LogInformation("Exported workbook workflow file to {WorkbookPath}", workbookPath);
            return new WorkbookExportResult(
                workbookPath,
                sourceExportId,
                generatedAtUtc,
                scanIdentity,
                installationIdentity,
                strategyLabel,
                inventory.Mods.Count,
                summary);
        }, cancellationToken);

    public Task<WorkbookImportResult> ImportAsync(
        string workbookPath,
        ScanInventory inventory,
        CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(workbookPath))
                throw new InvalidOperationException("The selected workbook could not be found.");
            if (!string.Equals(Path.GetExtension(workbookPath), ".xlsx", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Only .xlsx workbooks are supported for import.");

            ValidateWorkbookPackage(workbookPath);

            using var workbook = new XLWorkbook(workbookPath);
            var editable = workbook.Worksheets.FirstOrDefault(sheet => string.Equals(sheet.Name, EditableSheetName, StringComparison.Ordinal))
                           ?? throw new InvalidOperationException("The workbook is missing the Edit Destinations sheet.");
            var metadata = workbook.Worksheets.FirstOrDefault(sheet => string.Equals(sheet.Name, MetadataSheetName, StringComparison.Ordinal))
                           ?? throw new InvalidOperationException("The workbook is missing its metadata sheet.");

            var meta = ReadMetadata(metadata);
            var rows = new List<WorkbookImportRow>();
            var errors = new List<string>();
            var warnings = new List<string>();

            ValidateMetadata(meta, inventory, errors, warnings);
            if (errors.Count > 0)
                return BuildImportResult(workbookPath, meta, rows, errors, warnings);

            var inventoryById = inventory.Mods.ToDictionary(mod => mod.StableScanId, StringComparer.Ordinal);
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            var headerMap = ReadHeaderMap(editable);
            var requiredHeaders = new[] { "#", "mod name", "author", "current folder", "mod type", "protected", "destination", "_internal_key" };
            foreach (var header in requiredHeaders)
            {
                if (!headerMap.ContainsKey(header))
                    errors.Add($"The workbook is missing the required column '{header}'.");
            }

            if (errors.Count > 0)
                return BuildImportResult(workbookPath, meta, rows, errors, warnings);

            var lastRow = editable.LastRowUsed()?.RowNumber() ?? 1;
            for (var rowNumber = 2; rowNumber <= lastRow; rowNumber++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                foreach (var header in requiredHeaders)
                {
                    if (editable.Cell(rowNumber, headerMap[header]).HasFormula)
                        errors.Add($"Row {rowNumber} contains a formula in '{header}'. Replace formulas with plain text before importing.");
                }

                var stableScanId = editable.Cell(rowNumber, headerMap["_internal_key"]).GetString().Trim();
                var modName = editable.Cell(rowNumber, headerMap["mod name"]).GetString().Trim();
                var author = editable.Cell(rowNumber, headerMap["author"]).GetString().Trim();
                var currentFolder = editable.Cell(rowNumber, headerMap["current folder"]).GetString();
                var modType = editable.Cell(rowNumber, headerMap["mod type"]).GetString().Trim();
                var protectedRaw = editable.Cell(rowNumber, headerMap["protected"]).GetString().Trim();
                var destination = editable.Cell(rowNumber, headerMap["destination"]).GetString().Trim();

                if (string.IsNullOrWhiteSpace(stableScanId) &&
                    string.IsNullOrWhiteSpace(modName) &&
                    string.IsNullOrWhiteSpace(author) &&
                    string.IsNullOrWhiteSpace(currentFolder) &&
                    string.IsNullOrWhiteSpace(modType) &&
                    string.IsNullOrWhiteSpace(destination))
                {
                    continue;
                }

                if (errors.Any(error => error.StartsWith($"Row {rowNumber} ", StringComparison.Ordinal)))
                    continue;

                if (string.IsNullOrWhiteSpace(stableScanId))
                {
                    errors.Add($"Row {rowNumber} is missing the stable internal id.");
                    continue;
                }

                if (!seenIds.Add(stableScanId))
                {
                    errors.Add($"Row {rowNumber} contains a duplicate id: {stableScanId}.");
                    continue;
                }

                if (!inventoryById.TryGetValue(stableScanId, out var mod))
                {
                    errors.Add($"Row {rowNumber} references an unknown mod id: {stableScanId}.");
                    continue;
                }

                if (!string.Equals(currentFolder, mod.CurrentVirtualFolder, StringComparison.Ordinal))
                {
                    warnings.Add($"Row {rowNumber} no longer matches the exported current folder for {stableScanId} and was skipped. Scan and export a new workbook to include its edits.");
                    continue;
                }

                if (!TryParseProtected(protectedRaw, mod.Protected, out var protectedValue))
                {
                    errors.Add($"Row {rowNumber} has an invalid protected value for {stableScanId}. Use TRUE or FALSE.");
                    continue;
                }

                if (!TryResolveModType(modType, WorkbookCategoryCatalog.Detect(mod).Name, out var resolvedModType, out var modTypeError))
                {
                    errors.Add($"Row {rowNumber} has an invalid mod type for {stableScanId}: {modTypeError}");
                    continue;
                }

                if (!TryResolveDestination(destination, out var resolvedDestination, out var destinationError))
                {
                    errors.Add($"Row {rowNumber} has an invalid destination for {stableScanId}: {destinationError}");
                    continue;
                }

                if (protectedValue && resolvedDestination is not null && !string.Equals(resolvedDestination, mod.CurrentVirtualFolder, StringComparison.Ordinal))
                {
                    errors.Add($"Row {rowNumber} tries to move protected mod {stableScanId} to a different destination.");
                    continue;
                }

                rows.Add(new WorkbookImportRow(
                    rowNumber,
                    stableScanId,
                    string.IsNullOrWhiteSpace(modName) ? mod.Name : modName,
                    string.IsNullOrWhiteSpace(author) ? mod.Author : author,
                    currentFolder,
                    string.IsNullOrWhiteSpace(modType) ? WorkbookCategoryCatalog.Detect(mod).Name : modType,
                    protectedValue,
                    destination,
                    resolvedModType,
                    resolvedDestination));
            }

            return BuildImportResult(workbookPath, meta, rows, errors, warnings);
        }, cancellationToken);

    private void BuildEditableSheet(
        XLWorkbook workbook,
        ScanInventory inventory,
        IReadOnlyList<OrganizerModProposal> proposals,
        OrganizationPreferences organizationPreferences)
    {
        var protectedByStableScanId = proposals.ToDictionary(p => p.StableScanId, p => p.Protected, StringComparer.Ordinal);

        var sheet = workbook.Worksheets.Add(EditableSheetName);
        var headers = new[] { "#", "mod name", "author", "current folder", "mod type", "protected", "destination", "_internal_key" };
        for (var index = 0; index < headers.Length; index++)
            sheet.Cell(1, index + 1).Value = headers[index];

        sheet.Row(1).Style.Font.Bold = true;
        sheet.Row(1).Style.Fill.BackgroundColor = XLColor.FromHtml("#E9EEF4");
        for (var editableColumn = 5; editableColumn <= 7; editableColumn++)
            sheet.Cell(1, editableColumn).Style.Fill.BackgroundColor = XLColor.FromHtml("#DFF3E4");
        sheet.SheetView.FreezeRows(1);

        var row = 2;
        var displayNumber = 1;
        foreach (var mod in inventory.Mods.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            var category = WorkbookCategoryCatalog.Detect(mod);
            var isProtected = protectedByStableScanId.TryGetValue(mod.StableScanId, out var liveProtected)
                ? liveProtected
                : mod.Protected;
            sheet.Cell(row, 1).Value = displayNumber++;
            sheet.Cell(row, 2).Value = mod.Name;
            sheet.Cell(row, 3).Value = mod.Author;
            sheet.Cell(row, 4).Value = mod.CurrentVirtualFolder;
            sheet.Cell(row, 5).Value = category.Name;
            sheet.Cell(row, 6).Value = isProtected ? "TRUE" : "FALSE";
            sheet.Cell(row, 7).Value = BuildSuggestedDestination(mod, category, organizationPreferences);
            sheet.Cell(row, 8).Value = mod.StableScanId;
            row++;
        }

        var range = sheet.Range(1, 1, Math.Max(row - 1, 1), headers.Length);
        range.CreateTable();
        sheet.SheetView.FreezeRows(1);
        sheet.Columns(1, headers.Length).Style.NumberFormat.Format = "@";
        sheet.Column(8).Hide();
        sheet.Column(1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        sheet.Range(2, 1, Math.Max(row - 1, 2), 4).Style.Protection.SetLocked(true);
        sheet.Range(2, 8, Math.Max(row - 1, 2), 8).Style.Protection.SetLocked(true);
        sheet.Range(2, 5, Math.Max(row - 1, 2), 7).Style.Protection.SetLocked(false);
        sheet.Protect("PenumbraOrganizer");
        sheet.Columns().AdjustToContents();
    }

    private static void ValidateWorkbookPackage(string workbookPath)
    {
        using var archive = ZipFile.OpenRead(workbookPath);
        if (archive.Entries.Any(entry => entry.FullName.Equals("xl/vbaProject.bin", StringComparison.OrdinalIgnoreCase) ||
                                         entry.FullName.StartsWith("xl/activeX/", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Macro-enabled or ActiveX workbook content is not supported.");
        }

        if (archive.Entries.Any(entry => entry.FullName.StartsWith("xl/externalLinks/", StringComparison.OrdinalIgnoreCase) ||
                                         entry.FullName.Equals("xl/connections.xml", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Workbooks with external links or connections are not supported.");
        }
    }

    private static void BuildCategorySheet(XLWorkbook workbook)
    {
        var sheet = workbook.Worksheets.Add(CategorySheetName);
        sheet.Cell(1, 1).Value = "This sheet explains category shorthand and the blank-destination rule.";
        sheet.Cell(2, 1).Value = WorkbookCategoryCatalog.BlankDestinationRule;
        sheet.Cell(3, 1).Value = WorkbookCategoryCatalog.ReviewRule;
        sheet.Cell(5, 1).Value = "code";
        sheet.Cell(5, 2).Value = "name";
        sheet.Cell(5, 3).Value = "description";
        sheet.Cell(5, 4).Value = "example destination";

        var row = 6;
        foreach (var category in WorkbookCategoryCatalog.Definitions)
        {
            sheet.Cell(row, 1).Value = category.Code;
            sheet.Cell(row, 2).Value = category.Name;
            sheet.Cell(row, 3).Value = category.Description;
            sheet.Cell(row, 4).Value = category.ExampleDestination;
            row++;
        }

        sheet.Columns().AdjustToContents();
    }

    private static void BuildMetadataSheet(
        XLWorkbook workbook,
        string sourceExportId,
        DateTimeOffset generatedAtUtc,
        string scanIdentity,
        string installationIdentity,
        OrganizationPreferences organizationPreferences)
    {
        var sheet = workbook.Worksheets.Add(MetadataSheetName);
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["formatVersion"] = WorkbookCategoryCatalog.CurrentFormatVersion.ToString(),
            ["sourceExportId"] = sourceExportId,
            ["generatedAtUtc"] = generatedAtUtc.ToString("O"),
            ["scanIdentity"] = scanIdentity,
            ["installationIdentity"] = installationIdentity,
            ["strategy"] = organizationPreferences.Strategy.ToString(),
            ["blankDestinationRule"] = WorkbookCategoryCatalog.BlankDestinationRule,
            ["reviewRule"] = WorkbookCategoryCatalog.ReviewRule,
        };

        var row = 1;
        foreach (var pair in values)
        {
            sheet.Cell(row, 1).Value = pair.Key;
            sheet.Cell(row, 2).Value = pair.Value;
            row++;
        }

        sheet.Visibility = XLWorksheetVisibility.VeryHidden;
    }

    private Dictionary<string, string> ReadMetadata(IXLWorksheet sheet)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 0;
        for (var row = 1; row <= lastRow; row++)
        {
            var key = sheet.Cell(row, 1).GetString().Trim();
            if (string.IsNullOrWhiteSpace(key))
                continue;

            values[key] = sheet.Cell(row, 2).GetString().Trim();
        }

        return values;
    }

    private static Dictionary<string, int> ReadHeaderMap(IXLWorksheet sheet)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var lastColumn = sheet.Row(1).LastCellUsed()?.Address.ColumnNumber ?? 0;
        for (var column = 1; column <= lastColumn; column++)
        {
            var name = sheet.Cell(1, column).GetString().Trim();
            if (!string.IsNullOrWhiteSpace(name))
                map[name] = column;
        }

        return map;
    }

    private static void ValidateMetadata(Dictionary<string, string> meta, ScanInventory inventory, List<string> errors, List<string> warnings)
    {
        if (!meta.TryGetValue("formatVersion", out var formatVersion) ||
            !int.TryParse(formatVersion, out var parsedVersion) ||
            parsedVersion != WorkbookCategoryCatalog.CurrentFormatVersion)
        {
            errors.Add("The workbook format version is unsupported.");
        }

        var currentInstallationIdentity = ScanIdentity.BuildInstallationIdentity(inventory.Installation);
        if (!meta.TryGetValue("installationIdentity", out var installationIdentity) ||
            !string.Equals(installationIdentity, currentInstallationIdentity, StringComparison.Ordinal))
        {
            errors.Add("This workbook belongs to a different Penumbra library.");
        }

        var currentScanIdentity = ScanIdentity.BuildScanIdentity(inventory);
        if (!meta.TryGetValue("scanIdentity", out var scanIdentity) ||
            !string.Equals(scanIdentity, currentScanIdentity, StringComparison.Ordinal))
        {
            warnings.Add("Your mod library changed since this workbook was exported. Rows whose current folder no longer matches will be skipped.");
        }
    }

    private WorkbookImportResult BuildImportResult(
        string workbookPath,
        Dictionary<string, string> meta,
        IReadOnlyList<WorkbookImportRow> rows,
        IReadOnlyList<string> errors,
        IReadOnlyList<string> warnings)
    {
        meta.TryGetValue("sourceExportId", out var sourceExportId);
        meta.TryGetValue("scanIdentity", out var scanIdentity);
        meta.TryGetValue("installationIdentity", out var installationIdentity);
        meta.TryGetValue("generatedAtUtc", out var generatedAtUtcRaw);
        DateTimeOffset.TryParse(generatedAtUtcRaw, out var generatedAtUtc);

        var changedCount = rows.Count(row => row.ResolvedDestination is not null && !string.Equals(row.ResolvedDestination, row.CurrentVirtualFolder, StringComparison.Ordinal));
        var skippedCount = errors.Count + warnings.Count;
        var summary = rows.Count == 0 && errors.Count > 0
            ? $"Workbook import blocked. {errors.Count} validation issue(s) were found."
            : skippedCount > 0
                ? $"Workbook imported with {changedCount} mod destination change(s) ready for review. {skippedCount} row(s) were skipped."
                : $"Workbook imported successfully. {changedCount} mod destination change(s) are ready for review.";

        return new WorkbookImportResult(
            workbookPath,
            sourceExportId ?? string.Empty,
            generatedAtUtc,
            scanIdentity ?? string.Empty,
            installationIdentity ?? string.Empty,
            rows,
            errors,
            warnings,
            summary);
    }

    private string BuildSuggestedDestination(ModScanResult mod, WorkbookCategoryDefinition category, OrganizationPreferences organizationPreferences)
    {
        if (mod.Protected)
            return string.Empty;

        var creator = BuildCreatorFolder(mod.Author);
        var code = category.Code.ToString();
        return organizationPreferences.Strategy switch
        {
            OrganizationStrategy.CreatorOnly => string.IsNullOrWhiteSpace(creator) ? WorkbookCategoryCatalog.GetRequiredByCode((int)ModCategory.Others).Code.ToString() : creator,
            OrganizationStrategy.TypeOnly => code,
            OrganizationStrategy.TypeThenCreator => string.IsNullOrWhiteSpace(creator) ? code : $"{code}/{creator}",
            OrganizationStrategy.CreatorThenType => string.IsNullOrWhiteSpace(creator) ? code : $"{creator}/{code}",
            OrganizationStrategy.PreserveAndClean => mod.CurrentVirtualFolder,
            OrganizationStrategy.Custom => string.IsNullOrWhiteSpace(creator) ? code : $"{code}/{creator}",
            _ => string.Empty,
        };
    }

    private string BuildCreatorFolder(string author)
    {
        if (string.IsNullOrWhiteSpace(author))
            return string.Empty;

        var canonical = _creatorCanonicalizer.Canonicalize(author).Trim();
        return string.Join(
            " ",
            canonical
                .Split(Path.GetInvalidFileNameChars().Append('/').Append('\\').Distinct().ToArray(), StringSplitOptions.RemoveEmptyEntries))
            .Trim();
    }

    private static bool TryParseProtected(string raw, bool fallback, out bool value)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            value = fallback;
            return true;
        }

        if (bool.TryParse(raw, out value))
            return true;

        if (raw.Equals("yes", StringComparison.OrdinalIgnoreCase) || raw == "1")
        {
            value = true;
            return true;
        }

        if (raw.Equals("no", StringComparison.OrdinalIgnoreCase) || raw == "0")
        {
            value = false;
            return true;
        }

        value = fallback;
        return false;
    }

    private static bool TryResolveModType(string raw, string fallback, out string resolvedType, out string error)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            resolvedType = fallback;
            error = string.Empty;
            return true;
        }

        var normalized = raw.Trim();
        if (int.TryParse(normalized, out var code))
        {
            if (!WorkbookCategoryCatalog.TryGetByCode(code, out var category))
            {
                resolvedType = fallback;
                error = $"Category code {code} is not defined in the workbook mapping sheet.";
                return false;
            }

            resolvedType = category.Name;
            error = string.Empty;
            return true;
        }

        if (!WorkbookCategoryCatalog.TryGetByName(normalized, out var namedCategory))
        {
            resolvedType = fallback;
            error = $"Use a category name or code from the workbook mapping sheet.";
            return false;
        }

        resolvedType = namedCategory.Name;
        error = string.Empty;
        return true;
    }

    private static bool TryResolveDestination(string raw, out string? resolvedDestination, out string error)
    {
        resolvedDestination = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = string.Empty;
            return true;
        }

        var normalized = NormalizeRelativePath(raw);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            error = "Destination is empty.";
            return false;
        }

        if (Path.IsPathRooted(normalized) || normalized.Contains(':', StringComparison.Ordinal) || normalized.Any(char.IsControl))
        {
            error = "Use a relative Penumbra folder path.";
            return false;
        }

        var parts = normalized.Split('/', StringSplitOptions.None);
        if (parts.Any(part => string.IsNullOrWhiteSpace(part) || part is "." or ".."))
        {
            error = "Destination cannot contain empty segments, . or ..";
            return false;
        }

        // Category codes are always positive (see WorkbookCategoryCatalog.Definitions), so a
        // negative number can never be an attempted code shorthand -- it can only be a literal
        // folder name that happens to start with a negative number (e.g. "-16/SomeCreator").
        if (int.TryParse(parts[0], out var code) && code > 0)
        {
            if (!WorkbookCategoryCatalog.TryGetByCode(code, out var category))
            {
                error = $"Category code {code} is not defined in the workbook mapping sheet.";
                return false;
            }

            parts[0] = category.Name;
        }
        else if (WorkbookCategoryCatalog.TryGetByName(parts[0], out var namedCategory))
        {
            parts[0] = namedCategory.Name;
        }

        resolvedDestination = string.Join('/', parts);
        error = string.Empty;
        return true;
    }

    private static string NormalizeRelativePath(string path)
        => path.Trim().Replace('\\', '/').Trim('/');

    private static string StrategyLabel(OrganizationStrategy strategy)
        => strategy switch
        {
            OrganizationStrategy.CreatorOnly => "author only",
            OrganizationStrategy.TypeOnly => "type",
            OrganizationStrategy.TypeThenCreator => "type/author",
            OrganizationStrategy.CreatorThenType => "author/type",
            OrganizationStrategy.PreserveAndClean => "keep current",
            OrganizationStrategy.Custom => "custom",
            _ => "blank template",
        };
}
