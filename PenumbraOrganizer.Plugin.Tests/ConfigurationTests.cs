namespace PenumbraOrganizer.Plugin.Tests;

public class ConfigurationTests
{
    [Fact]
    public void DefaultConfiguration_HasVersionOneAndEmptyProtectedSet()
    {
        var config = new Configuration();

        Assert.Equal(1, config.Version);
        Assert.Empty(config.ProtectedModIdentifiers);
    }

    [Fact]
    public void ProtectedModIdentifiers_IsMutable()
    {
        var config = new Configuration();

        config.ProtectedModIdentifiers.Add("hs-Nightingale-1.0");

        Assert.Contains("hs-Nightingale-1.0", config.ProtectedModIdentifiers);
    }
}
