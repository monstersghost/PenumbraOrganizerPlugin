namespace PenumbraOrganizer.Plugin.Organizer.Workbook;

using ClosedXML.Excel;
using System.IO.Compression;
using System.Xml;
using Microsoft.Extensions.Logging;
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Core.Identity;
using PenumbraOrganizer.Core.Models;

public sealed class WorkbookService
{
    private const string EditableSheetName = "Edit Destinations";
    private const string CategorySheetName = MappingSheet.SheetName;
    private const string MetadataSheetName = "_Metadata";

    private const long MaxWorkbookBytes = 25L * 1024 * 1024;
    private const long MaxUncompressedBytes = 100L * 1024 * 1024;
    private const int RowMargin = 100;
    private const int MinRowCap = 500;
    private const string UnreadableMessage = "This file could not be read as a Penumbra Organizer workbook.";

    private static readonly string[] EditHeaders =
        ["#", "mod name", "author", "current folder", "mod type", "protected", "destination", "_internal_key", "_leaf", "_exported"];

    private static readonly string[] RequiredHeaders =
        ["#", "mod name", "author", "current folder", "mod type", "protected", "destination", "_internal_key"];

    private readonly ILogger<WorkbookService> _logger;

    public WorkbookService(ILogger<WorkbookService> logger)
    {
        _logger = logger;
    }

    /// <summary>The plugin's own, user-readable failures. Anything else that goes wrong reading a file is "unreadable".</summary>
    private sealed class WorkbookUserException(string message) : InvalidOperationException(message);

    private sealed class ImportTally
    {
        public int Stale;
        public int ReadyRows;
        public HashSet<int> BlockedRows { get; } = [];
        public Dictionary<int, int> CodeCounts { get; } = [];
    }

    // ---------------------------------------------------------------- export

    public Task<WorkbookExportResult> ExportAsync(
        WorkbookExportRequest request,
        string workbookPath,
        CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var workbookDirectory = Path.GetDirectoryName(workbookPath);
            if (string.IsNullOrWhiteSpace(workbookDirectory))
                throw new InvalidOperationException("The workbook export path is invalid.");
            Directory.CreateDirectory(workbookDirectory);

            var inventory = request.Inventory;
            var generatedAtUtc = DateTimeOffset.UtcNow;
            var sourceExportId = $"workbook-{generatedAtUtc:yyyyMMddTHHmmssZ}-{Guid.NewGuid():N}";
            var scanIdentity = ScanIdentity.BuildScanIdentity(inventory);
            var installationIdentity = ScanIdentity.BuildInstallationIdentity(inventory.Installation);

            using var workbook = new XLWorkbook();
            BuildEditableSheet(workbook, request);
            BuildCategorySheet(workbook, request.CustomCodes);
            BuildMetadataSheet(workbook, sourceExportId, generatedAtUtc, scanIdentity, installationIdentity, request);
            workbook.SaveAs(workbookPath);

            var label = request.Mode == WorkbookExportMode.AsIs ? "current folders" : "the staged Sort";
            var summary = $"Workbook exported with {inventory.Mods.Count} mod row(s) using {label}.";
            _logger.LogInformation("Exported workbook workflow file to {WorkbookPath}", workbookPath);
            return new WorkbookExportResult(
                workbookPath,
                sourceExportId,
                generatedAtUtc,
                scanIdentity,
                installationIdentity,
                label,
                inventory.Mods.Count,
                summary);
        }, cancellationToken);

    private static void BuildEditableSheet(XLWorkbook workbook, WorkbookExportRequest request)
    {
        var inventory = request.Inventory;
        var liveProtected = request.Proposals.ToDictionary(p => p.StableScanId, p => p.Protected, StringComparer.Ordinal);
        var stagedFolders = request.Proposals.ToDictionary(p => p.StableScanId, p => p.ProposedVirtualFolder, StringComparer.Ordinal);

        var sheet = workbook.Worksheets.Add(EditableSheetName);
        for (var index = 0; index < EditHeaders.Length; index++)
            sheet.Cell(1, index + 1).Value = EditHeaders[index];

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
            var isProtected = liveProtected.TryGetValue(mod.StableScanId, out var liveValue) ? liveValue : mod.Protected;

            // A protected mod is always blank ("leave alone"). As is writes the current folder (a top-level
            // mod's is empty, so it is blank too). The staged Sort writes the staged folder for every
            // unprotected mod, top-level moves included.
            var destination = string.Empty;
            if (!isProtected)
            {
                destination = request.Mode == WorkbookExportMode.AsIs
                    ? mod.CurrentVirtualFolder
                    : stagedFolders.GetValueOrDefault(mod.StableScanId, string.Empty);
            }

            // The real last path segment, so an unedited import keeps it (a duplicate install's "Name (2)",
            // or a leaf that differs from the display name).
            var leaf = request.Leaves.GetValueOrDefault(mod.StableScanId, string.Empty);

            sheet.Cell(row, 1).Value = displayNumber++;
            sheet.Cell(row, 2).Value = mod.Name;
            sheet.Cell(row, 3).Value = mod.Author;
            sheet.Cell(row, 4).Value = mod.CurrentVirtualFolder;
            sheet.Cell(row, 5).Value = category.Name;
            sheet.Cell(row, 6).Value = isProtected ? "TRUE" : "FALSE";
            sheet.Cell(row, 7).Value = destination;
            sheet.Cell(row, 8).Value = mod.StableScanId;
            sheet.Cell(row, 9).Value = leaf;
            sheet.Cell(row, 10).Value = destination;
            row++;
        }

        var range = sheet.Range(1, 1, Math.Max(row - 1, 1), EditHeaders.Length);
        range.CreateTable();
        sheet.SheetView.FreezeRows(1);
        sheet.Columns(1, EditHeaders.Length).Style.NumberFormat.Format = "@";
        sheet.Column(8).Hide();
        sheet.Column(9).Hide();
        sheet.Column(10).Hide();
        sheet.Column(1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        sheet.Range(2, 1, Math.Max(row - 1, 2), 4).Style.Protection.SetLocked(true);
        sheet.Range(2, 8, Math.Max(row - 1, 2), 10).Style.Protection.SetLocked(true);
        sheet.Range(2, 5, Math.Max(row - 1, 2), 7).Style.Protection.SetLocked(false);
        sheet.Protect("PenumbraOrganizer");
        sheet.Columns().AdjustToContents();
    }

    private static void BuildCategorySheet(XLWorkbook workbook, IReadOnlyList<CustomCode> customCodes)
    {
        var sheet = workbook.Worksheets.Add(CategorySheetName);
        sheet.Cell(1, 1).Value = "This sheet explains category shorthand and lets you add your own categories (code 17 and up).";
        sheet.Cell(2, 1).Value = WorkbookCategoryCatalog.BlankDestinationRule;
        sheet.Cell(3, 1).Value = WorkbookCategoryCatalog.ReviewRule;
        sheet.Cell(4, 1).Value = "Rows 1-16 are built in. Do not edit them: edits are ignored.";
        var banner = sheet.Range(4, 1, 4, 5);
        banner.Style.Font.Bold = true;
        banner.Style.Font.FontColor = XLColor.White;
        banner.Style.Fill.BackgroundColor = XLColor.FromHtml("#C0392B");

        sheet.Cell(5, 1).Value = "code";
        sheet.Cell(5, 2).Value = "name";
        sheet.Cell(5, 3).Value = "description";
        sheet.Cell(5, 4).Value = "folder";
        sheet.Cell(5, 5).Value = "example";

        var row = MappingSheet.FirstBuiltInRow;
        foreach (var category in WorkbookCatalog.BuiltIn)
        {
            sheet.Cell(row, 1).Value = category.Code;
            sheet.Cell(row, 2).Value = category.Name;
            sheet.Cell(row, 3).Value = category.Description;
            sheet.Cell(row, 4).Value = category.Folder;
            sheet.Cell(row, 5).Value = $"{category.Code}/{{creator}}";
            row++;
        }

        // A corrupt saved list may repeat a code or hold a bad folder: write only the first clean row per
        // code, in order, and never more than the cap.
        var written = new HashSet<int>();
        foreach (var custom in customCodes.OrderBy(c => c.Code))
        {
            if (written.Count >= MappingSheet.MaxCustomRows)
                break;
            if (custom.Code is < MappingSheet.FirstCustomCode or > MappingSheet.LastCustomCode || !written.Add(custom.Code))
                continue;
            var folder = DestinationResolver.NormalizePath(custom.Folder ?? string.Empty);
            if (folder.Length == 0 || DestinationResolver.StructureError(folder) is not null
                || DestinationResolver.UnknownTokens(folder).Count > 0)
            {
                written.Remove(custom.Code);
                continue;
            }

            sheet.Cell(row, 1).Value = custom.Code;
            sheet.Cell(row, 2).Value = "custom";
            sheet.Cell(row, 3).Value = "Your own category. Only code and folder are used.";
            sheet.Cell(row, 4).Value = folder;
            sheet.Cell(row, 5).Value = $"{custom.Code}/{{creator}}";
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
        WorkbookExportRequest request)
    {
        var sheet = workbook.Worksheets.Add(MetadataSheetName);
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["formatVersion"] = WorkbookCatalog.FormatVersion.ToString(),
            ["sourceExportId"] = sourceExportId,
            ["generatedAtUtc"] = generatedAtUtc.ToString("O"),
            ["scanIdentity"] = scanIdentity,
            ["installationIdentity"] = installationIdentity,
            ["strategy"] = request.Mode.ToString(),
            ["exportMode"] = request.Mode.ToString(),
            ["sortFingerprint"] = request.SortFingerprint ?? string.Empty,
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

    // ---------------------------------------------------------------- import

    public Task<WorkbookImportResult> ImportAsync(
        string workbookPath,
        ScanInventory inventory,
        WorkbookImportContext context,
        CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(workbookPath))
                throw new WorkbookUserException("The selected workbook could not be found.");
            if (!string.Equals(Path.GetExtension(workbookPath), ".xlsx", StringComparison.OrdinalIgnoreCase))
                throw new WorkbookUserException("Only .xlsx workbooks are supported for import.");
            if (new FileInfo(workbookPath).Length > MaxWorkbookBytes)
                throw new WorkbookUserException(
                    $"This file is larger than {MaxWorkbookBytes / (1024 * 1024)} MB, which is more than a workbook of any library needs.");

            try
            {
                return ReadWorkbook(workbookPath, inventory, context, cancellationToken);
            }
            catch (WorkbookUserException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A corrupt, truncated or hostile file never surfaces its internals.
                _logger.LogWarning(ex, "Workbook import could not parse {WorkbookPath}", workbookPath);
                throw new InvalidOperationException(UnreadableMessage);
            }
        }, cancellationToken);

    private WorkbookImportResult ReadWorkbook(
        string workbookPath, ScanInventory inventory, WorkbookImportContext context, CancellationToken cancellationToken)
    {
        // Read the file once into memory and use the same bytes for the package checks and the load, so the
        // file cannot change between them.
        using var stream = new MemoryStream(File.ReadAllBytes(workbookPath));
        var rowCap = Math.Max(inventory.Mods.Count + RowMargin, MinRowCap);
        ValidateWorkbookPackage(stream, rowCap);
        stream.Position = 0;

        using var workbook = new XLWorkbook(stream);
        var editable = workbook.Worksheets.FirstOrDefault(sheet => string.Equals(sheet.Name, EditableSheetName, StringComparison.Ordinal))
                       ?? throw new WorkbookUserException("The workbook is missing the Edit Destinations sheet.");
        var metadata = workbook.Worksheets.FirstOrDefault(sheet => string.Equals(sheet.Name, MetadataSheetName, StringComparison.Ordinal))
                       ?? throw new WorkbookUserException("The workbook is missing its metadata sheet.");

        var meta = ReadMetadata(metadata);
        var rows = new List<WorkbookImportRow>();
        var errors = new List<string>();
        var warnings = new List<string>();
        var tally = new ImportTally();

        ValidateMetadata(meta, inventory, errors, warnings);
        var version = FormatVersionOf(meta);
        if (errors.Count > 0)
            return BuildImportResult(workbookPath, meta, rows, errors, warnings, version, null, tally);

        var isV3 = version >= 3;
        var headerMap = ReadHeaderRow(editable, 1, errors, EditableSheetName, RequiredHeaders.Concat(["_leaf", "_exported"]));
        foreach (var header in RequiredHeaders)
        {
            if (!headerMap.ContainsKey(header))
                errors.Add($"The workbook is missing the required column '{header}'.");
        }

        if (errors.Count > 0)
            return BuildImportResult(workbookPath, meta, rows, errors, warnings, version, null, tally);

        // Version 3 reads the whole mapping table first; version 2 never does (it has no custom rows). A
        // missing sheet or header is structural: nothing from this file is used.
        MappingParseResult? mapping = null;
        if (isV3)
        {
            mapping = ReadMappingSheet(workbook, errors, warnings, out var structural);
            if (structural)
                return BuildImportResult(workbookPath, meta, rows, errors, warnings, version, null, tally);
        }

        var rules = isV3 ? BuildVersionThreeRules(mapping ?? NoMapping) : BuildVersionTwoRules();
        var leafColumn = isV3 && headerMap.TryGetValue("_leaf", out var leafIndex) ? leafIndex : 0;
        var exportedColumn = isV3 && headerMap.TryGetValue("_exported", out var exportedIndex) ? exportedIndex : 0;

        var inventoryById = inventory.Mods.ToDictionary(mod => mod.StableScanId, StringComparer.Ordinal);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var lastRow = editable.LastRowUsed()?.RowNumber() ?? 1;

        for (var rowNumber = 2; rowNumber <= lastRow; rowNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            void Block(string message)
            {
                errors.Add(message);
                tally.BlockedRows.Add(rowNumber);
            }

            string Address(string header) => $"{EditableSheetName}!{XLHelper.GetColumnLetterFromNumber(headerMap[header])}{rowNumber}";

            var consumed = RequiredHeaders.AsEnumerable();
            if (leafColumn > 0)
                consumed = consumed.Append("_leaf");
            if (exportedColumn > 0)
                consumed = consumed.Append("_exported");
            foreach (var header in consumed)
            {
                if (editable.Cell(rowNumber, headerMap[header]).HasFormula)
                    Block($"{Address(header)}: this cell contains a formula. Replace formulas with plain text before importing.");
            }

            if (tally.BlockedRows.Contains(rowNumber))
                continue;

            var stableScanId = editable.Cell(rowNumber, headerMap["_internal_key"]).GetString().Trim();
            var modName = editable.Cell(rowNumber, headerMap["mod name"]).GetString().Trim();
            var author = editable.Cell(rowNumber, headerMap["author"]).GetString().Trim();
            var currentFolder = editable.Cell(rowNumber, headerMap["current folder"]).GetString();
            var modType = editable.Cell(rowNumber, headerMap["mod type"]).GetString().Trim();
            var protectedRaw = editable.Cell(rowNumber, headerMap["protected"]).GetString().Trim();
            var rawDestination = editable.Cell(rowNumber, headerMap["destination"]).GetString();
            var destination = rawDestination.Trim();
            var leaf = leafColumn > 0 ? editable.Cell(rowNumber, leafColumn).GetString().Trim() : string.Empty;
            var exported = exportedColumn > 0 ? editable.Cell(rowNumber, exportedColumn).GetString() : null;

            if (string.IsNullOrWhiteSpace(stableScanId) &&
                string.IsNullOrWhiteSpace(modName) &&
                string.IsNullOrWhiteSpace(author) &&
                string.IsNullOrWhiteSpace(currentFolder) &&
                string.IsNullOrWhiteSpace(modType) &&
                string.IsNullOrWhiteSpace(destination))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(stableScanId))
            {
                Block($"{Address("_internal_key")}: the stable internal id is missing.");
                continue;
            }

            if (!seenIds.Add(stableScanId))
            {
                Block($"{Address("_internal_key")}: duplicate id {stableScanId}.");
                continue;
            }

            if (!inventoryById.TryGetValue(stableScanId, out var mod))
            {
                Block($"{Address("_internal_key")}: unknown mod id {stableScanId}.");
                continue;
            }

            if (!string.Equals(currentFolder, mod.CurrentVirtualFolder, StringComparison.Ordinal))
            {
                tally.Stale++;
                warnings.Add($"Row {rowNumber} no longer matches the exported current folder for {stableScanId} and was skipped. Scan and export a new workbook to include its edits.");
                continue;
            }

            if (!TryParseProtected(protectedRaw, mod.Protected, out var protectedValue))
            {
                Block($"{Address("protected")}: invalid protected value for {stableScanId}. Use TRUE or FALSE.");
                continue;
            }

            if (!TryResolveModType(modType, WorkbookCategoryCatalog.Detect(mod).Name, out var resolvedModType, out var modTypeError))
            {
                // The mod-type column has no effect on the import. Version 2 always treated a bad value as a
                // reason to block the row; version 3 only warns.
                if (!isV3)
                {
                    Block($"{Address("mod type")}: invalid mod type for {stableScanId}: {modTypeError}");
                    continue;
                }

                warnings.Add($"{Address("mod type")}: mod type \"{modType}\" was not recognised and was ignored ({modTypeError}).");
            }

            string? resolvedDestination;
            int? codeUsed = null;
            if (isV3)
            {
                // A cell unchanged from what the export wrote is taken exactly as written: a folder that is
                // named like a category or a token (a creator "VFX", "17" or "A/B") must not be read again.
                var result = exported is not null && rawDestination.Length > 0 && string.Equals(rawDestination, exported, StringComparison.Ordinal)
                    ? DestinationResolver.Literal(rawDestination)
                    : DestinationResolver.Resolve(rawDestination, rules, context.TokensFor(stableScanId));
                if (result.Error is { } destinationError)
                {
                    Block($"{Address("destination")}: invalid destination for {stableScanId}: {ExplainCodeError(destinationError, destination, mapping)}");
                    continue;
                }

                resolvedDestination = result.Destination;
                codeUsed = result.CodeUsed;
            }
            else if (!TryResolveLegacyDestination(destination, out resolvedDestination, out var legacyError))
            {
                Block($"{Address("destination")}: invalid destination for {stableScanId}: {legacyError}");
                continue;
            }

            var currentLeaf = context.CurrentLeafFor?.Invoke(stableScanId);
            string? stagedLeaf = null;
            if (leaf.Length > 0)
            {
                if (leaf.Contains('/') || leaf.Any(char.IsControl) || leaf is "." or "..")
                {
                    Block($"{EditableSheetName}!{XLHelper.GetColumnLetterFromNumber(leafColumn)}{rowNumber}: the staged name is not a valid folder name.");
                    continue;
                }

                if (resolvedDestination is not null
                    && DestinationResolver.StructureError(WorkbookAdapter.JoinPath(resolvedDestination, leaf)) is { } joinedError)
                {
                    Block($"{Address("destination")}: invalid destination for {stableScanId}: {joinedError}");
                    continue;
                }

                stagedLeaf = leaf;
            }

            var leafChanged = stagedLeaf is not null && currentLeaf is not null
                && !string.Equals(stagedLeaf, currentLeaf, StringComparison.Ordinal);
            var pathChanged = resolvedDestination is not null
                && (!string.Equals(resolvedDestination, mod.CurrentVirtualFolder, StringComparison.Ordinal) || leafChanged);

            if (protectedValue && pathChanged)
            {
                Block($"{Address("destination")}: tries to move protected mod {stableScanId} to a different destination.");
                continue;
            }

            if (codeUsed is int usedCode && usedCode >= MappingSheet.FirstCustomCode && resolvedDestination is not null)
                tally.CodeCounts[usedCode] = tally.CodeCounts.GetValueOrDefault(usedCode) + 1;

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
                resolvedDestination,
                stagedLeaf));

            if (pathChanged)
                tally.ReadyRows++;
        }

        return BuildImportResult(workbookPath, meta, rows, errors, warnings, version, mapping, tally);
    }

    private static readonly MappingParseResult NoMapping =
        new([], new Dictionary<int, string>(), [], []);

    private static DestinationRules BuildVersionThreeRules(MappingParseResult mapping) => new(
        TokensEnabled: true,
        CodeFolder: code => code is >= 1 and <= 16 ? WorkbookCatalog.FolderFor(code) : mapping.FolderFor(code),
        NameFolder: name => WorkbookCatalog.TryGetByName(name, out var category) ? category.Folder : null);

    private static DestinationRules BuildVersionTwoRules() => new(
        TokensEnabled: false,
        CodeFolder: code => WorkbookCategoryCatalog.TryGetByCode(code, out var category) ? category.Name : null,
        NameFolder: name => WorkbookCategoryCatalog.TryGetByName(name, out var category) ? category.Name : null);

    // A code that appeared in the mapping sheet but cannot be used gets its own reason, not "not in the sheet".
    private static string ExplainCodeError(string error, string destination, MappingParseResult? mapping)
    {
        if (mapping is not null
            && DestinationResolver.FirstSegmentCode(destination) is { } code
            && mapping.Unusable.TryGetValue(code, out var reason))
            return $"code {code} cannot be used: {reason}";

        return error;
    }

    /// <summary>
    /// The mapping table. Rows 6 to 21 are always read (a cleared last built-in row must warn), columns are
    /// found by header, and a missing sheet, header, or a duplicated header is structural.
    /// </summary>
    private static MappingParseResult? ReadMappingSheet(
        XLWorkbook workbook, List<string> errors, List<string> warnings, out bool structural)
    {
        structural = false;
        var sheet = workbook.Worksheets.FirstOrDefault(s => string.Equals(s.Name, CategorySheetName, StringComparison.Ordinal));
        if (sheet is null)
        {
            errors.Add($"The workbook is missing the {CategorySheetName} sheet.");
            structural = true;
            return null;
        }

        var headerErrors = new List<string>();
        var header = ReadHeaderRow(sheet, 5, headerErrors, CategorySheetName, ["code", "name", "folder"]);
        if (headerErrors.Count > 0)
        {
            errors.AddRange(headerErrors);
            structural = true;
            return null;
        }

        if (!header.TryGetValue("code", out var codeColumn) || !header.TryGetValue("folder", out var folderColumn))
        {
            errors.Add($"{CategorySheetName} is missing its 'code' or 'folder' column in row 5.");
            structural = true;
            return null;
        }

        var nameColumn = header.TryGetValue("name", out var found) ? found : 0;
        var codeLetter = XLHelper.GetColumnLetterFromNumber(codeColumn);
        var folderLetter = XLHelper.GetColumnLetterFromNumber(folderColumn);
        var lastRow = Math.Max(sheet.LastRowUsed()?.RowNumber() ?? 0, MappingSheet.FirstCustomRow - 1);
        var list = new List<MappingSheetRow>();
        for (var row = MappingSheet.FirstBuiltInRow; row <= lastRow; row++)
        {
            var codeCell = sheet.Cell(row, codeColumn);
            var folderCell = sheet.Cell(row, folderColumn);
            var nameCell = nameColumn > 0 ? sheet.Cell(row, nameColumn) : null;
            list.Add(new MappingSheetRow(
                row,
                codeCell.GetString(),
                nameCell?.GetString() ?? string.Empty,
                folderCell.GetString(),
                codeCell.HasFormula || folderCell.HasFormula || (nameCell?.HasFormula ?? false),
                codeLetter,
                folderLetter));
        }

        var parsed = MappingSheet.Parse(list);
        errors.AddRange(parsed.Errors);
        warnings.AddRange(parsed.Warnings);
        return parsed;
    }

    private static int FormatVersionOf(Dictionary<string, string> meta) =>
        meta.TryGetValue("formatVersion", out var text) && int.TryParse(text, out var version) ? version : 2;

    private static void ValidateWorkbookPackage(Stream stream, int rowCap)
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Any(entry => entry.FullName.Equals("xl/vbaProject.bin", StringComparison.OrdinalIgnoreCase) ||
                                         entry.FullName.StartsWith("xl/activeX/", StringComparison.OrdinalIgnoreCase)))
        {
            throw new WorkbookUserException("Macro-enabled or ActiveX workbook content is not supported.");
        }

        if (archive.Entries.Any(entry => entry.FullName.StartsWith("xl/externalLinks/", StringComparison.OrdinalIgnoreCase) ||
                                         entry.FullName.Equals("xl/connections.xml", StringComparison.OrdinalIgnoreCase)))
        {
            throw new WorkbookUserException("Workbooks with external links or connections are not supported.");
        }

        if (archive.Entries.Sum(entry => entry.Length) > MaxUncompressedBytes)
            throw new WorkbookUserException("This file expands to far more data than a workbook of any library needs.");

        // Count the actual rows in every worksheet before ClosedXML builds anything: the optional dimension
        // element can be missing or lie, and a sparse sheet with one cell on row 1,000,000 is not a workbook.
        foreach (var entry in archive.Entries.Where(e =>
                     e.FullName.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase)
                     && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
        {
            using var entryStream = entry.Open();
            using var reader = XmlReader.Create(
                entryStream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var count = 0;
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "row")
                    continue;

                count++;
                if (count > rowCap || (int.TryParse(reader.GetAttribute("r"), out var index) && index > rowCap))
                    throw new WorkbookUserException(
                        $"A worksheet in this file has more than {rowCap} rows, which is more than this library needs.");
            }
        }
    }

    private static Dictionary<string, string> ReadMetadata(IXLWorksheet sheet)
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

    /// <summary>The header names in a row. A duplicated header that import consumes is an error, not a silent overwrite.</summary>
    private static Dictionary<string, int> ReadHeaderRow(
        IXLWorksheet sheet, int row, List<string> errors, string sheetName, IEnumerable<string> consumed)
    {
        var consumedSet = new HashSet<string>(consumed, StringComparer.OrdinalIgnoreCase);
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var lastColumn = sheet.Row(row).LastCellUsed()?.Address.ColumnNumber ?? 0;
        for (var column = 1; column <= lastColumn; column++)
        {
            var name = sheet.Cell(row, column).GetString().Trim();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            if (!map.TryAdd(name, column) && consumedSet.Contains(name))
                errors.Add($"{sheetName} has more than one '{name}' column (row {row}).");
        }

        return map;
    }

    private static void ValidateMetadata(Dictionary<string, string> meta, ScanInventory inventory, List<string> errors, List<string> warnings)
    {
        if (!meta.TryGetValue("formatVersion", out var formatVersion) ||
            !int.TryParse(formatVersion, out var parsedVersion) ||
            parsedVersion is not (2 or 3))
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
        IReadOnlyList<string> warnings,
        int formatVersion,
        MappingParseResult? mapping,
        ImportTally tally)
    {
        meta.TryGetValue("sourceExportId", out var sourceExportId);
        meta.TryGetValue("scanIdentity", out var scanIdentity);
        meta.TryGetValue("installationIdentity", out var installationIdentity);
        meta.TryGetValue("generatedAtUtc", out var generatedAtUtcRaw);
        DateTimeOffset.TryParse(generatedAtUtcRaw, out var generatedAtUtc);

        var ready = tally.ReadyRows;
        var unchanged = rows.Count - ready;
        var blocked = tally.BlockedRows.Count;
        var counts = new WorkbookImportCounts(ready, unchanged, tally.Stale, blocked, warnings.Count);

        var summary = rows.Count == 0 && errors.Count > 0 && blocked == 0
            ? $"Workbook import blocked. {errors.Count} validation issue(s) were found."
            : blocked > 0 || errors.Count > 0
                ? $"Workbook imported with problems: {ready} move(s) ready for review, {unchanged} unchanged or blank, {tally.Stale} skipped as stale, {blocked} blocked by errors, {warnings.Count} warning(s)."
                : $"Workbook imported: {ready} move(s) ready for review, {unchanged} unchanged or blank, {tally.Stale} skipped as stale, {warnings.Count} warning(s).";

        // The saved list is replaced only by a CLEAN mapping table (no mapping-sheet error); a blocked
        // import, a version 2 file and a table with errors all leave it alone.
        IReadOnlyList<CustomCode>? customCodes = mapping is { HasErrors: false } ? mapping.Custom : null;
        IReadOnlyList<WorkbookCodeUsage> usage = mapping is null
            ? []
            : tally.CodeCounts
                .Where(pair => mapping.FolderFor(pair.Key) is not null)
                .OrderBy(pair => pair.Key)
                .Select(pair => new WorkbookCodeUsage(pair.Key, mapping.FolderFor(pair.Key)!, pair.Value))
                .ToList();

        return new WorkbookImportResult(
            workbookPath,
            sourceExportId ?? string.Empty,
            generatedAtUtc,
            scanIdentity ?? string.Empty,
            installationIdentity ?? string.Empty,
            rows,
            errors,
            warnings,
            summary,
            formatVersion,
            customCodes,
            usage,
            counts);
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
            error = "Use a category name or code from the workbook mapping sheet.";
            return false;
        }

        resolvedType = namedCategory.Name;
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// The version 2 destination rules, unchanged (no tokens, no length cap, the old category names), so a
    /// workbook from before this format reads exactly as it always did.
    /// </summary>
    private static bool TryResolveLegacyDestination(string raw, out string? resolvedDestination, out string error)
    {
        resolvedDestination = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = string.Empty;
            return true;
        }

        var normalized = DestinationResolver.NormalizePath(raw);
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

        // Category codes are always positive, so a negative number can never be an attempted code shorthand.
        // Version 2 accepted signs and padding on the code ("+16"), so it parses with NumberStyles.Integer.
        if (int.TryParse(parts[0], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var code) && code > 0)
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
}
