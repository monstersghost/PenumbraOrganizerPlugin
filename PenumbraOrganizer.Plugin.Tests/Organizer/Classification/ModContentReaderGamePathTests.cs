using PenumbraOrganizer.Plugin.Organizer.Classification;
using Xunit;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Classification;

public sealed class ModContentReaderGamePathTests : IDisposable
{
    private readonly DirectoryInfo _dir =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pm-" + Guid.NewGuid().ToString("N")));

    public void Dispose() => _dir.Delete(true);

    [Fact]
    public void Game_paths_are_the_file_keys()
    {
        File.WriteAllText(Path.Combine(_dir.FullName, "default_mod.json"),
            """{ "Files": { "chara/human/c0101/skeleton/base/b0001/skl_c0101b0001.sklb": "a.sklb" } }""");

        var content = ModContentReader.Read(_dir);

        Assert.NotNull(content);
        Assert.Equal(["chara/human/c0101/skeleton/base/b0001/skl_c0101b0001.sklb"], content!.GamePaths);
    }
}
