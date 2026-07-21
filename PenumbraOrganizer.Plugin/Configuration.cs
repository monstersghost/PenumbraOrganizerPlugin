using Dalamud.Configuration;

namespace PenumbraOrganizer.Plugin;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public HashSet<string> ProtectedModIdentifiers { get; set; } = [];

    public HashSet<string> ProtectedFolderPaths { get; set; } = [];

    public Organizer.ApplyOperationSummary? LastApply { get; set; }

    public Organizer.RestoreOperationSummary? LastRestore { get; set; }

    public Organizer.FolderCleanupOperationSummary? LastFolderCleanup { get; set; }

    public Organizer.FolderCleanupRollbackOperationSummary? LastFolderCleanupRollback { get; set; }
}
