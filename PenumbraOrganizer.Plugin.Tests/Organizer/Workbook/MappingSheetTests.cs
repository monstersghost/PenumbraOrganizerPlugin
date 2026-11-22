using PenumbraOrganizer.Plugin.Organizer.Workbook;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Workbook;

public class MappingSheetTests
{
    private static MappingSheetRow Row(int row, string code, string folder, string name = "", bool formula = false) =>
        new(row, code, name, folder, formula);

    private static MappingParseResult Parse(params MappingSheetRow[] rows) => MappingSheet.Parse(rows);

    // The full, unedited built-in block as the export writes it.
    private static IEnumerable<MappingSheetRow> BuiltIns() =>
        WorkbookCatalog.BuiltIn.Select((c, i) => Row(6 + i, c.Code.ToString(), c.Folder, c.Name));

    [Fact]
    public void ACleanCustomRow_IsUsable()
    {
        var result = Parse(Row(22, "17", "Face/Sculpts"));
        Assert.False(result.HasErrors);
        Assert.Equal(new[] { new CustomCode(17, "Face/Sculpts") }, result.Custom);
        Assert.Equal("Face/Sculpts", result.FolderFor(17));
    }

    [Fact]
    public void AnUneditedBuiltInBlock_IsCleanAndWarnsNothing()
    {
        var result = MappingSheet.Parse(BuiltIns().ToList());
        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
        Assert.Empty(result.Custom);
    }

    [Theory]
    [InlineData("17")]
    [InlineData("0017")]
    [InlineData(" 17 ")]
    [InlineData("17.0")]
    public void ACodeTypedDifferentWays_IsTheSameCode(string text) =>
        Assert.Equal(17, Parse(Row(22, text, "Mine")).Custom.Single().Code);

    [Theory]
    [InlineData("17.5")]
    [InlineData("-17")]
    [InlineData("10000")]
    [InlineData("0")]
    [InlineData("abc")]
    [InlineData("TRUE")]
    public void AnInvalidCode_IsAnErrorOnItsCell(string text)
    {
        var result = Parse(Row(22, text, "Mine"));
        Assert.True(result.HasErrors);
        Assert.Empty(result.Custom);
        Assert.StartsWith("Category Mapping!A22:", result.Errors.Single());
    }

    [Fact]
    public void ABuiltInCodeBelowTheBuiltInBlock_IsAnError()
    {
        var result = Parse(Row(22, "5", "Mine"));
        Assert.Contains("code 5 is built in", result.Errors.Single());
        Assert.Empty(result.Custom);
    }

    [Fact]
    public void ADuplicatedCode_IsAnErrorOnEveryRowAndUnusable()
    {
        var result = Parse(Row(22, "17", "A"), Row(23, "17", "B"));
        Assert.Equal(
            new[] { "Category Mapping!A22: code 17 is used twice.", "Category Mapping!A23: code 17 is used twice." },
            result.Errors);
        Assert.Empty(result.Custom);
        Assert.Equal("code 17 is defined more than once", result.Unusable[17]);
    }

    [Fact]
    public void ABlankFolder_IsAnErrorOnTheFolderCellAndTheCodeIsUnusable()
    {
        var result = Parse(Row(22, "17", ""));
        Assert.Equal("Category Mapping!D22: the folder is empty for code 17.", result.Errors.Single());
        Assert.Contains(17, result.Unusable.Keys);
    }

    [Fact]
    public void ABadFolderPath_IsAnErrorOnTheFolderCell()
    {
        var result = Parse(Row(22, "17", "Mine/../x"));
        Assert.Equal(
            "Category Mapping!D22: Destination cannot contain empty segments, . or ..", result.Errors.Single());
    }

    [Fact]
    public void AFolderMayContainKnownTokens() =>
        Assert.Equal("Mine/{type}", Parse(Row(22, "17", "Mine/{type}")).Custom.Single().Folder);

    [Fact]
    public void AFolderWithAnUnknownToken_IsAnErrorAndNeverUsable()
    {
        var result = Parse(Row(22, "17", "Mine/{typo}"));
        Assert.True(result.HasErrors);
        Assert.Empty(result.Custom);
        Assert.Contains("unknown token {typo}", result.Errors.Single());
    }

    [Fact]
    public void AFormula_IsAnError()
    {
        var result = Parse(Row(22, "17", "Mine", formula: true));
        Assert.StartsWith("Category Mapping!A22: this row contains a formula", result.Errors.Single());
    }

    [Fact]
    public void AFormulaInANameOnlyRow_IsStillAnError()
    {
        var result = Parse(Row(22, "", "", name: "=A1", formula: true));
        Assert.StartsWith("Category Mapping!A22: this row contains a formula", result.Errors.Single());
    }

    [Fact]
    public void ErrorsNameTheColumnsThatWereFound()
    {
        var row = new MappingSheetRow(22, "17", "", "", false, CodeColumn: "B", FolderColumn: "E");
        Assert.StartsWith("Category Mapping!E22:", Parse(row).Errors.Single());
    }

    [Fact]
    public void OneBadRowDoesNotStopTheOthers()
    {
        var result = Parse(Row(22, "17", "Face/Sculpts"), Row(23, "18", ""));
        Assert.Equal("Face/Sculpts", result.FolderFor(17));
        Assert.Null(result.FolderFor(18));
        Assert.True(result.HasErrors);
    }

    [Fact]
    public void BlankRows_AreIgnored() =>
        Assert.Empty(Parse(Row(22, "", ""), Row(23, "  ", "  ")).Custom);

    [Fact]
    public void EditingABuiltInRow_WarnsWithBothValuesAndChangesNothing()
    {
        var result = Parse(Row(10, "5", "Bodies", name: "Body"));
        Assert.False(result.HasErrors);
        Assert.Empty(result.Custom);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("built-in code 5", warning);
        Assert.Contains("Body", warning);
        Assert.Contains("Bodies", warning);
    }

    [Fact]
    public void ChangingABuiltInRowsCodeTo17_WarnsAndDoesNotCreateACustomCode()
    {
        var result = Parse(Row(6, "17", "Gear", name: "Gear"));
        Assert.Empty(result.Custom);
        Assert.Contains("code '1' to '17'", Assert.Single(result.Warnings));
    }

    [Fact]
    public void AClearedBuiltInRow_Warns()
    {
        var result = Parse(Row(21, "", "", name: ""));
        Assert.Contains("built-in code 16 was edited", Assert.Single(result.Warnings));
    }

    [Fact]
    public void AnUneditedBuiltInRow_WarnsNothing() =>
        Assert.Empty(Parse(Row(6, "1", "Gear", name: "Gear")).Warnings);

    [Fact]
    public void MoreThanTwoHundredCustomRows_IsAnError_AndNoneAreUsable()
    {
        var rows = Enumerable.Range(17, 201).Select(code => Row(code + 6, code.ToString(), "F" + code)).ToArray();
        var result = Parse(rows);
        Assert.Contains(result.Errors, e => e.StartsWith("Category Mapping: more than 200 custom categories"));
        Assert.Empty(result.Custom);
        Assert.Equal(201, result.Unusable.Count);
    }
}

public class WorkbookSeedTests
{
    private static CustomCode C(int code, string folder) => new(code, folder);

    [Fact]
    public void NoChange_IsNull() =>
        Assert.Null(WorkbookSeed.Describe([C(17, "A")], [C(17, "A")]));

    [Fact]
    public void ReportsAddedChangedAndRemoved() =>
        Assert.Equal(
            "Saved 2 custom categories: 1 added, 1 changed, 1 removed.",
            WorkbookSeed.Describe([C(17, "A"), C(19, "Z")], [C(17, "B"), C(18, "N")]));

    [Fact]
    public void ClearingEverything_StillSaysSo() =>
        Assert.Equal(
            "Saved 0 custom categories: 0 added, 0 changed, 2 removed.",
            WorkbookSeed.Describe([C(17, "A"), C(18, "B")], []));

    [Fact]
    public void OneCategory_UsesTheSingularWord() =>
        Assert.Equal(
            "Saved 1 custom category: 1 added, 0 changed, 0 removed.",
            WorkbookSeed.Describe([], [C(17, "A")]));

    [Fact]
    public void ACorruptSavedListWithADuplicateCode_DoesNotThrow() =>
        Assert.Null(WorkbookSeed.Describe([C(17, "A"), C(17, "B")], [C(17, "A")]));
}
