namespace PenumbraOrganizer.Plugin.Tests.Organizer.BodyTypes;

using PenumbraOrganizer.Plugin.Organizer.BodyTypes;

/// <summary>
/// The user list's load path. Every case here is a way a hand-written file goes wrong, and the
/// question in each is whether the user is TOLD rather than silently ignored.
/// </summary>
public class BodyTypeListStoreTests
{
    private static string TempConfigDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "PenumbraOrganizer.Plugin.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WriteUserList(string dir, string json) =>
        File.WriteAllText(Path.Combine(dir, BodyTypeListStore.UserFileName), json);

    [Fact]
    public void LoadForMatching_NoUserFile_IsBundledAndSilent()
    {
        var dir = TempConfigDir();

        var result = BodyTypeListStore.LoadForMatching(dir);

        Assert.NotEmpty(result.Document.Bodies);
        Assert.Empty(result.Warnings);
        Assert.False(File.Exists(BodyTypeListStore.UserListPath(dir)),
            "the store must never create the user file");
    }

    [Fact]
    public void LoadForMatching_UserEntryAdded_IsMerged()
    {
        var dir = TempConfigDir();
        WriteUserList(dir, """
            {"Version":1,"Bodies":[{"Label":"Zebra","Sex":"Female","Aliases":["zebra"]}]}
            """);

        var result = BodyTypeListStore.LoadForMatching(dir);

        Assert.Contains("Zebra", result.Document.Bodies.Select(b => b.Label));
        Assert.Contains("Bibo+", result.Document.Bodies.Select(b => b.Label));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void LoadForMatching_UserEntryWithTheSameLabel_ReplacesTheShippedOne()
    {
        // The supported way to correct a shipped body: same Label, so the user's aliases win
        // outright. Replacing rather than merging is what lets a wrong alias be REMOVED.
        var dir = TempConfigDir();
        WriteUserList(dir, """
            {"Version":1,"Bodies":[{"Label":"Bibo+","Sex":"Female","Aliases":["bibo+"]}]}
            """);

        var result = BodyTypeListStore.LoadForMatching(dir);

        var bibo = Assert.Single(result.Document.Bodies.Where(b => b.Label == "Bibo+"));
        Assert.Equal(["bibo+"], bibo.Aliases);
        Assert.DoesNotContain("bibo", bibo.Aliases);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void LoadForMatching_UnusableLabel_IsDroppedWithAWarningNamingIt()
    {
        // The bug this guards: a label the codec once accepted and the folder builder rejected
        // threw out of GetFolder partway through a sort. Now it never reaches a row, and the user
        // is told why rather than wondering where their entry went.
        var dir = TempConfigDir();
        WriteUserList(dir, """
            {"Version":1,"Bodies":[{"Label":"Yo*Body","Sex":"Female","Aliases":["yo"]}]}
            """);

        var result = BodyTypeListStore.LoadForMatching(dir);

        Assert.DoesNotContain("Yo*Body", result.Document.Bodies.Select(b => b.Label));
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("Yo*Body", warning);
    }

    [Fact]
    public void LoadForMatching_AliasCollidingWithADifferentShippedBody_WarnsThatOneWillNeverMatch()
    {
        // Silently losing this is how the documented extension mechanism fails: whichever label
        // sorts first wins, so either the user's body or the shipped one never matches.
        var dir = TempConfigDir();
        WriteUserList(dir, """
            {"Version":1,"Bodies":[{"Label":"Zebra","Sex":"Female","Aliases":["bibo"]}]}
            """);

        var result = BodyTypeListStore.LoadForMatching(dir);

        var warning = Assert.Single(result.Warnings);
        Assert.Contains("bibo", warning);
    }

    [Fact]
    public void LoadForMatching_BothProblemsAtOnce_ReportsBoth()
    {
        // Why Warnings is a list and not one string: reporting only the first would hide the
        // second, and a hand-written file can easily have both.
        var dir = TempConfigDir();
        WriteUserList(dir, """
            {"Version":1,"Bodies":[
              {"Label":"Yo*Body","Sex":"Female","Aliases":["yo"]},
              {"Label":"Zebra","Sex":"Female","Aliases":["bibo"]}]}
            """);

        var result = BodyTypeListStore.LoadForMatching(dir);

        Assert.Equal(2, result.Warnings.Count);
    }

    [Fact]
    public void LoadForMatching_MalformedJson_FallsBackAndWarns()
    {
        var dir = TempConfigDir();
        WriteUserList(dir, "{ not json");

        var result = BodyTypeListStore.LoadForMatching(dir);

        Assert.NotEmpty(result.Document.Bodies);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void LoadForMatching_OversizedUserFile_FallsBackAndLeavesTheFileAlone()
    {
        var dir = TempConfigDir();
        var entries = string.Join(",", Enumerable.Range(0, BodyTypeListStore.MaxUserBodyCount + 1)
            .Select(i => $"{{\"Label\":\"Body{i}\",\"Aliases\":[\"body{i}\"]}}"));
        WriteUserList(dir, $"{{\"Version\":1,\"Bodies\":[{entries}]}}");
        var before = File.ReadAllText(BodyTypeListStore.UserListPath(dir));

        var result = BodyTypeListStore.LoadForMatching(dir);

        Assert.Single(result.Warnings);
        Assert.DoesNotContain("Body0", result.Document.Bodies.Select(b => b.Label));
        Assert.Equal(before, File.ReadAllText(BodyTypeListStore.UserListPath(dir)));
    }
}
