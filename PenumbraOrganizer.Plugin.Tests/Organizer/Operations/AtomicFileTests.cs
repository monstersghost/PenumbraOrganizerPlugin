using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class AtomicFileTests
{
    [Fact]
    public void CreateOrReplace_WritesFileWhenDestinationDoesNotExist()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "journal.json");

            AtomicFile.CreateOrReplace(path, "{\"a\":1}");

            Assert.True(File.Exists(path));
            Assert.Equal("{\"a\":1}", File.ReadAllText(path));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void CreateOrReplace_ReplacesExistingDestination()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "journal.json");
            File.WriteAllText(path, "old");

            AtomicFile.CreateOrReplace(path, "new");

            Assert.Equal("new", File.ReadAllText(path));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void CreateOrReplace_LeavesNoOrphanedTempFileOnSuccess()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "journal.json");

            AtomicFile.CreateOrReplace(path, "contents");

            var leftover = Directory.GetFiles(dir.FullName).Where(f => f != path);
            Assert.Empty(leftover);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void CreateOrReplace_RemovesPreExistingOrphanedTempFileBeforeWriting()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "journal.json");
            var tempPath = path + ".tmp";
            File.WriteAllText(tempPath, "stale from a crashed previous write");

            AtomicFile.CreateOrReplace(path, "contents");

            Assert.Equal("contents", File.ReadAllText(path));
            Assert.False(File.Exists(tempPath));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void TryReadValidated_ReturnsFalseWhenFileDoesNotExist()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "missing.json");

            var found = AtomicFile.TryReadValidated(path, out var contents);

            Assert.False(found);
            Assert.Null(contents);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void TryReadValidated_ReturnsTrueAndContentsWhenFileExists()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "journal.json");
            File.WriteAllText(path, "{\"a\":1}");

            var found = AtomicFile.TryReadValidated(path, out var contents);

            Assert.True(found);
            Assert.Equal("{\"a\":1}", contents);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void TryReadValidated_ReturnsFalseWhenFileIsEmpty()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "empty.json");
            File.WriteAllText(path, string.Empty);

            var found = AtomicFile.TryReadValidated(path, out var contents);

            Assert.False(found);
            Assert.Null(contents);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void CreateOrReplace_CreatesDestinationDirectoryIfMissing()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "nested", "journal.json");

            AtomicFile.CreateOrReplace(path, "contents");

            Assert.Equal("contents", File.ReadAllText(path));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
