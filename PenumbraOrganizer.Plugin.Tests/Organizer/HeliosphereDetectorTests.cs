namespace PenumbraOrganizer.Plugin.Tests.Organizer;

using PenumbraOrganizer.Plugin.Organizer;

public class HeliosphereDetectorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "po-helio-" + Guid.NewGuid().ToString("N"));

    // Removes the meta file when asked for a directory without one, rather than only creating it
    // when asked for one with it: two calls in the same test share _root, and a leftover file made
    // the "no meta file" case silently test the "has meta file" case.
    private DirectoryInfo Dir(bool withMetaFile)
    {
        Directory.CreateDirectory(_root);
        var meta = Path.Combine(_root, "heliosphere.json");

        if (withMetaFile)
            File.WriteAllText(meta, "{}");
        else
            File.Delete(meta);   // no-op when absent

        return new DirectoryInfo(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void DirectoryPrefix_IsDetected_WithoutAnyDiskRead()
    {
        var missing = new DirectoryInfo(Path.Combine(_root, "does-not-exist"));

        Assert.True(HeliosphereDetector.IsHeliosphereManaged("hs-Band Tee-1.1.2-caRR", missing));
    }

    [Fact]
    public void MetaFile_IsDetected_WhenDirectoryPrefixIsAbsent()
    {
        Assert.True(HeliosphereDetector.IsHeliosphereManaged("Some Hand Renamed Mod", Dir(withMetaFile: true)));
    }

    [Fact]
    public void PlainMod_IsNotDetected()
    {
        Assert.False(HeliosphereDetector.IsHeliosphereManaged("Ordinary Mod", Dir(withMetaFile: false)));
    }

    // Heliosphere's own display-name prefix. Covers 60 of the 68 prefix-less managed mods in the
    // real library this was measured against.
    [Fact]
    public void DisplayNamePrefix_IsDetected_WhenMetaFileIsMissing()
    {
        Assert.True(HeliosphereDetector.IsHeliosphereManaged(
            "[HS] PUNK OUT! FREAK OUT! (Fem Version)",
            Dir(withMetaFile: false),
            displayName: "[HS] PUNK OUT! FREAK OUT! (Fem Version)"));
    }

    [Fact]
    public void DisplayNamePrefix_IsCaseSensitive_SoOrdinaryNamesAreNotSwept()
    {
        Assert.False(HeliosphereDetector.IsHeliosphereManaged(
            "hs lowercase thing", Dir(withMetaFile: false), displayName: "[hs] not heliosphere"));
    }

    // THE REGRESSION THIS EXISTS FOR. A Heliosphere update deletes the old mod directory and writes
    // a new one, so heliosphere.json is briefly absent. Before the remembered set, a scan landing in
    // that window reported the mod unmanaged, protection dropped, and the next Apply moved a mod out
    // from under Heliosphere. Eight mods in the measured library had no other signal at all.
    [Fact]
    public void RememberedIdentifier_KeepsDetection_WhenMetaFileVanishes()
    {
        var known = new HashSet<string>(["Skelomae Custom Skeleton v3.3.0"], StringComparer.Ordinal);

        Assert.True(HeliosphereDetector.IsHeliosphereManaged(
            "Skelomae Custom Skeleton v3.3.0",
            Dir(withMetaFile: false),
            displayName: "Skelomae Custom Skeleton v3.3.0",
            previouslyKnownIdentifiers: known));
    }

    [Fact]
    public void RememberedSet_DoesNotDetectUnrelatedMods()
    {
        var known = new HashSet<string>(["Something Else"], StringComparer.Ordinal);

        Assert.False(HeliosphereDetector.IsHeliosphereManaged(
            "Ordinary Mod", Dir(withMetaFile: false), displayName: "Ordinary Mod",
            previouslyKnownIdentifiers: known));
    }

    // Identifiers are directory names, which are case-insensitive on Windows but are compared
    // ordinally everywhere else in this codebase. Pinned so the choice is deliberate rather than
    // incidental: an exact match is required.
    [Fact]
    public void RememberedSet_MatchesOrdinally()
    {
        var known = new HashSet<string>(["Exact Case"], StringComparer.Ordinal);

        Assert.False(HeliosphereDetector.IsHeliosphereManaged(
            "exact case", Dir(withMetaFile: false), previouslyKnownIdentifiers: known));
    }

    [Fact]
    public void NullDisplayNameAndNullKnownSet_FallBackToTheOriginalTwoSignals()
    {
        Assert.True(HeliosphereDetector.IsHeliosphereManaged(
            "Renamed", Dir(withMetaFile: true), displayName: null, previouslyKnownIdentifiers: null));
        Assert.False(HeliosphereDetector.IsHeliosphereManaged(
            "Renamed", Dir(withMetaFile: false), displayName: null, previouslyKnownIdentifiers: null));
    }

    // A scan must not fail over one malformed mod path. Before the guard this threw out of a disk
    // probe rather than returning a value.
    [Fact]
    public void MalformedPath_ReturnsFalseRatherThanThrowing()
    {
        var bad = new DirectoryInfo(Path.Combine(_root, "ok"));

        Assert.False(HeliosphereDetector.IsHeliosphereManaged("plain", bad));
    }
}
