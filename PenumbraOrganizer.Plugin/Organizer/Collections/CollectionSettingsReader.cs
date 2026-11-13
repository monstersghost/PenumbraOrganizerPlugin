using Dalamud.Plugin;
using Penumbra.Api.Enums;
using Penumbra.Api.IpcSubscribers;

namespace PenumbraOrganizer.Plugin.Organizer.Collections;

/// <summary>
/// Reads, for every Penumbra collection, which mods its own settings enable. Inheritance and
/// temporary settings are ignored on purpose. Read-only; null when Penumbra cannot list collections.
/// </summary>
public sealed class CollectionSettingsReader
{
    private readonly GetCollections _collections;
    private readonly GetAllModSettings _allSettings;

    public CollectionSettingsReader(IDalamudPluginInterface pluginInterface)
    {
        _collections = new GetCollections(pluginInterface);
        _allSettings = new GetAllModSettings(pluginInterface);
    }

    public CollectionUsageSet? Read()
    {
        try
        {
            var names = _collections.Invoke();
            if (names is null)
                return null;

            return CollectionUsageSet.Build(names, ReadOne);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private IReadOnlyDictionary<string, bool>? ReadOne(Guid id)
    {
        var (ec, settings) = _allSettings.Invoke(id, ignoreInheritance: true, ignoreTemporary: true);
        if (ec != PenumbraApiEc.Success || settings is null)
            return null;

        return settings.ToDictionary(pair => pair.Key, pair => pair.Value.Item1, StringComparer.OrdinalIgnoreCase);
    }
}
