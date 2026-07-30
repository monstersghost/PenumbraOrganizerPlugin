namespace PenumbraOrganizer.Plugin.Tests.Organizer.Templates;

using PenumbraOrganizer.Plugin.Organizer.Templates;

public sealed class TemplateStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("template-store-tests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string Json(string name, string strategy = "ModType", string entryName = "some mod") =>
        $$"""
        {"formatVersion":1,"name":"{{name}}","fallbackStrategy":"{{strategy}}",
         "folders":["Gear"],"entries":[{"n":"{{entryName}}","f":"Gear"}]}
        """;

    [Fact]
    public void List_EmptyOrMissingDirectory_ReturnsNothingWithoutThrowing()
    {
        var store = new TemplateStore(Path.Combine(_dir, "does-not-exist"));

        var listing = store.List();

        Assert.Empty(listing.Templates);
        Assert.Empty(listing.UnreadableFiles);
    }

    [Fact]
    public void Save_WritesFileNamedFromDisplayName()
    {
        var store = new TemplateStore(_dir);

        var fileName = store.Save(Json("Detailed type sort"), "Detailed type sort");

        Assert.Equal("detailed-type-sort.json", fileName);
        Assert.True(File.Exists(Path.Combine(_dir, fileName)));
    }

    [Fact]
    public void SaveThenList_RoundTripsTheTemplate()
    {
        var store = new TemplateStore(_dir);
        store.Save(Json("Detailed type sort"), "Detailed type sort");

        var listing = store.List();

        var stored = Assert.Single(listing.Templates);
        Assert.Equal("Detailed type sort", stored.Template.Name);
        Assert.Equal("detailed-type-sort.json", stored.FileName);
    }

    // Two people may share a template name; importing one must never clobber the other.
    [Fact]
    public void Save_ExistingSlug_GetsSuffixInsteadOfOverwriting()
    {
        var store = new TemplateStore(_dir);
        store.Save(Json("Layout", entryName: "first mod"), "Layout");

        var second = store.Save(Json("Layout", entryName: "second mod"), "Layout");

        Assert.Equal("layout-2.json", second);
        Assert.Equal(2, store.List().Templates.Count);
    }

    // A hostile display name must not write outside the templates directory.
    [Fact]
    public void Save_HostileDisplayName_StaysInsideTheDirectory()
    {
        var store = new TemplateStore(_dir);

        var fileName = store.Save(Json("escape"), "../../escaped");

        Assert.Equal(fileName, Path.GetFileName(fileName));
        var written = Path.GetFullPath(Path.Combine(_dir, fileName));
        Assert.StartsWith(Path.GetFullPath(_dir), written, StringComparison.OrdinalIgnoreCase);
    }

    // One bad file must not make the whole list unavailable.
    [Fact]
    public void List_InvalidFile_IsReportedWithoutHidingTheGoodOnes()
    {
        var store = new TemplateStore(_dir);
        store.Save(Json("Good one"), "Good one");
        File.WriteAllText(Path.Combine(_dir, "broken.json"), "{ not json");

        var listing = store.List();

        Assert.Single(listing.Templates);
        Assert.Equal("Good one", listing.Templates[0].Template.Name);
        Assert.Equal(["broken.json"], listing.UnreadableFiles);
    }

    [Fact]
    public void List_NonJsonFiles_AreIgnoredEntirely()
    {
        var store = new TemplateStore(_dir);
        store.Save(Json("Good one"), "Good one");
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "hello");

        var listing = store.List();

        Assert.Single(listing.Templates);
        Assert.Empty(listing.UnreadableFiles);
    }

    [Fact]
    public void List_IsOrderedByFileNameSoTheUiIsStable()
    {
        var store = new TemplateStore(_dir);
        store.Save(Json("Zeta"), "Zeta");
        store.Save(Json("Alpha"), "Alpha");

        var listing = store.List();

        Assert.Equal(["alpha.json", "zeta.json"], listing.Templates.Select(t => t.FileName));
    }

    // Warnings are part of what the preview shows, so they must survive being stored and re-read.
    [Fact]
    public void List_CarriesDecodeWarningsThrough()
    {
        var store = new TemplateStore(_dir);
        File.WriteAllText(
            Path.Combine(_dir, "warned.json"),
            """{"formatVersion":1,"name":"Warned","fallbackStrategy":"ModType","folders":["Gear//Bad"]}""");

        var stored = Assert.Single(store.List().Templates);

        Assert.Contains(stored.Warnings, w => w.Code == TemplateWarningCode.InvalidFolderPath);
    }

    [Fact]
    public void Save_FailedWriteLeavesNoPartialFile()
    {
        var store = new TemplateStore(_dir);

        Assert.Throws<ArgumentException>(() => store.Save("{ not a valid template }", "Bad"));
        Assert.Empty(Directory.GetFiles(_dir, "*.json"));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }
}
