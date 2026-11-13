namespace PenumbraOrganizer.Plugin.Organizer.Collections;

/// <summary>What happens when new mods are installed.</summary>
public enum NewModAction
{
    Ask,
    SortAutomatically,
    DoNothing,
}

/// <summary>Where a mod enabled in several collections goes when sorting by collection.</summary>
public enum CollectionSharedRule
{
    SharedFolder,
    LeaveInPlace,
}

/// <summary>Where a mod enabled in no collection goes when sorting by collection.</summary>
public enum CollectionUnusedRule
{
    UnusedFolder,
    SortNormally,
}
