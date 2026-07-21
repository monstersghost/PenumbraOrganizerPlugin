namespace PenumbraOrganizer.Plugin.Organizer;

public static class HeliosphereDetector
{
    private const string DirectoryPrefix = "hs-";
    private const string MetaFileName = "heliosphere.json";

    public static bool IsHeliosphereManaged(string directoryName, DirectoryInfo modPath)
    {
        if (!string.IsNullOrWhiteSpace(directoryName)
            && directoryName.StartsWith(DirectoryPrefix, StringComparison.OrdinalIgnoreCase))
            return true;

        return modPath.Exists && File.Exists(Path.Combine(modPath.FullName, MetaFileName));
    }
}
