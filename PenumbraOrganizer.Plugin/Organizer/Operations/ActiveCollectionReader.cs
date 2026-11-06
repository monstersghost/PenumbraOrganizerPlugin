using Dalamud.Plugin;
using Penumbra.Api.Enums;
using Penumbra.Api.IpcSubscribers;

namespace PenumbraOrganizer.Plugin.Organizer.Operations;

/// <summary>
/// Reads which mods are enabled in the collection that affects the local player. Null when
/// Penumbra is missing or answers with anything but a usable collection. Nothing here writes.
/// </summary>
public sealed class ActiveCollectionReader
{
    private readonly GetCollectionForObject _forObject;
    private readonly GetCollection _current;
    private readonly GetAllModSettings _allSettings;

    public ActiveCollectionReader(IDalamudPluginInterface pluginInterface)
    {
        _forObject = new GetCollectionForObject(pluginInterface);
        _current = new GetCollection(pluginInterface);
        _allSettings = new GetAllModSettings(pluginInterface);
    }

    public CollectionRead? ReadEnabledModDirectories()
    {
        try
        {
            var player = PlayerCollection();
            var found = player ?? CurrentCollection();
            if (found is not { } collection)
                return null;

            var (ec, settings) = _allSettings.Invoke(collection.Id);
            if (ec != PenumbraApiEc.Success || settings is null)
                return null;

            var enabled = settings.Where(kv => kv.Value.Item1)
                .Select(kv => kv.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            // The source travels with the answer so the UI can say WHICH collection was read.
            return new CollectionRead(collection.Name, IsPlayerCollection: player is not null, enabled);
        }
        catch (Exception)
        {
            // Called from the draw path: any failure (Penumbra missing, IPC error, a changed shape)
            // must read as "could not check", never as an exception in the render loop.
            return null;
        }
    }

    private (Guid Id, string Name)? PlayerCollection()
    {
        var (valid, _, collection) = _forObject.Invoke(0);
        return valid && collection.Id != Guid.Empty ? (collection.Id, collection.Name) : null;
    }

    private (Guid Id, string Name)? CurrentCollection()
    {
        var current = _current.Invoke(ApiCollectionType.Current);
        return current is { } c && c.Id != Guid.Empty ? (c.Id, c.Name) : null;
    }
}

/// <summary>Which collection was read, whether it was the player's or Penumbra's selected one, and its enabled mods.</summary>
public sealed record CollectionRead(string Name, bool IsPlayerCollection, IReadOnlySet<string> EnabledModDirectories);
