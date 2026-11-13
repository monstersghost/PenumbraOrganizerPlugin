namespace PenumbraOrganizer.Plugin.Organizer.Collections;

/// <summary>One collection and the mod directories enabled in its own settings.</summary>
public sealed record CollectionUsage(Guid Id, string Name, IReadOnlySet<string> EnabledDirectories);

/// <summary>Every readable collection, plus the names of the ones that could not be read.</summary>
public sealed record CollectionUsageSet(
    IReadOnlyList<CollectionUsage> Collections, IReadOnlyList<string> Unreadable)
{
    /// <summary>
    /// Builds the set from a per-collection read of directory -> enabled. A null result or a
    /// throw makes that collection unreadable; it never fails the whole set.
    /// </summary>
    public static CollectionUsageSet Build(
        IReadOnlyDictionary<Guid, string> names,
        Func<Guid, IReadOnlyDictionary<string, bool>?> readSettings)
    {
        var collections = new List<CollectionUsage>();
        var unreadable = new List<string>();

        foreach (var (id, name) in names.OrderBy(pair => pair.Key))
        {
            IReadOnlyDictionary<string, bool>? settings;
            try
            {
                settings = readSettings(id);
            }
            catch (Exception)
            {
                settings = null;
            }

            if (settings is null)
            {
                unreadable.Add(name);
                continue;
            }

            var enabled = settings.Where(pair => pair.Value)
                .Select(pair => pair.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            collections.Add(new CollectionUsage(id, name, enabled));
        }

        return new CollectionUsageSet(collections, unreadable);
    }

    /// <summary>Directories enabled in at least one readable collection.</summary>
    public HashSet<string> UsedDirectories()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var collection in Collections)
            used.UnionWith(collection.EnabledDirectories);
        return used;
    }
}
