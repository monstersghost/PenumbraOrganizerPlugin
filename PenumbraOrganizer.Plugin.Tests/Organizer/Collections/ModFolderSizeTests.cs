using PenumbraOrganizer.Plugin.Organizer.Collections;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Collections;

public class ModFolderSizeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pto-size-" + Guid.NewGuid().ToString("N"));

    public ModFolderSizeTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public void Sums_Files_Recursively()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllBytes(Path.Combine(_root, "a.bin"), new byte[100]);
        File.WriteAllBytes(Path.Combine(_root, "sub", "b.bin"), new byte[50]);
        Assert.Equal(150L, ModFolderSize.Measure(_root));
    }

    [Fact]
    public void Missing_Directory_Is_Null() =>
        Assert.Null(ModFolderSize.Measure(Path.Combine(_root, "nope")));

    [Fact]
    public void Cancelled_Token_Throws()
    {
        File.WriteAllBytes(Path.Combine(_root, "a.bin"), new byte[10]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => ModFolderSize.Measure(_root, cts.Token));
    }
}
