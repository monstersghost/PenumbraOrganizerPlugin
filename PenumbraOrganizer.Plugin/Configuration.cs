using Dalamud.Configuration;

namespace PenumbraOrganizer.Plugin;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public HashSet<string> ProtectedModIdentifiers { get; set; } = [];
}
