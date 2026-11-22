using System.Runtime.CompilerServices;

namespace PenumbraOrganizer.Plugin.Organizer.Rules;

/// <summary>
/// One compiled <see cref="RuleSet"/> per configuration, reused until that config's rule version or the race
/// options change. The Review tab asks for the selection every frame, so parsing must never happen per call.
/// Keyed per configuration so independent configurations (and parallel tests) never evict each other.
/// </summary>
public static class RuleSetCache
{
    private sealed class Entry
    {
        public int Version = -1;
        public RaceSplitOptions? Race;
        public RuleSet Set = RuleSet.Empty;
    }

    private static readonly ConditionalWeakTable<Configuration, Entry> Entries = new();

    public static RuleSet For(Configuration config, RaceSplitOptions race)
    {
        var entry = Entries.GetOrCreateValue(config);
        lock (entry)
        {
            if (entry.Version == config.SortRulesVersion && race.Equals(entry.Race))
                return entry.Set;

            entry.Set = config.SortRules.Count == 0
                ? RuleSet.Empty
                : RuleSet.Compile(config.SortRules.Select(r => new RuleEntry(r.Text, r.Enabled)).ToList(), race);
            entry.Version = config.SortRulesVersion;
            entry.Race = race;
            return entry.Set;
        }
    }
}
