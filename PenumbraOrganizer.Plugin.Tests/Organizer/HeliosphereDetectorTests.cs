using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class HeliosphereDetectorTests
{
    [Fact]
    public void DirectoryPrefix_IsDetected()
    {
        var tempDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));

        Assert.True(HeliosphereDetector.IsHeliosphereManaged("hs-Nightingale-1.0", tempDir));
    }

    [Fact]
    public void MetaFile_IsDetected()
    {
        var tempDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        tempDir.Create();
        File.WriteAllText(Path.Combine(tempDir.FullName, "heliosphere.json"), "{}");

        try
        {
            Assert.True(HeliosphereDetector.IsHeliosphereManaged("SomeOtherMod", tempDir));
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void NeitherSignal_ReturnsFalse()
    {
        var tempDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));

        Assert.False(HeliosphereDetector.IsHeliosphereManaged("RegularMod", tempDir));
    }
}
