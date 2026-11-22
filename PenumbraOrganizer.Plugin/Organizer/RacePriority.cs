using PenumbraOrganizer.Plugin.Organizer.Classification;

namespace PenumbraOrganizer.Plugin.Organizer;

/// <summary>
/// The user's ranked races, first is highest, used only to choose a folder for a mod made for several
/// races. May be partial: a ranked race wins in order, an unranked race never wins. Immutable, with
/// sequence equality so the Sort tab's "selection changed since the last sort" check (record equality)
/// compares it by value, as <c>BodyPriority</c> does.
/// </summary>
public sealed class RacePriority : IEquatable<RacePriority>
{
    public static readonly RacePriority Empty = new([]);

    private readonly string[] _ranked;

    private RacePriority(string[] ranked) => _ranked = ranked;

    public IReadOnlyList<string> Ranked => _ranked;

    /// <summary>Keeps known race names, drops unknown and duplicate ones, keeps the order.</summary>
    public static RacePriority Normalize(IEnumerable<string>? saved)
    {
        if (saved is null)
            return Empty;

        var ranked = new List<string>();
        foreach (var name in saved)
        {
            if (RaceNames.IsKnown(name) && !ranked.Contains(name, StringComparer.Ordinal))
                ranked.Add(name);
        }

        return new RacePriority([.. ranked]);
    }

    public RacePriority Add(string race) =>
        RaceNames.IsKnown(race) && RankOf(race) < 0 ? new([.. _ranked, race]) : this;

    public RacePriority Remove(string race) =>
        RankOf(race) < 0 ? this : new([.. _ranked.Where(r => !string.Equals(r, race, StringComparison.Ordinal))]);

    public RacePriority MoveUp(int index) => Swap(index - 1, index);

    public RacePriority MoveDown(int index) => Swap(index, index + 1);

    private RacePriority Swap(int first, int second)
    {
        if (first < 0 || second >= _ranked.Length || first >= second)
            return this;

        var copy = (string[])_ranked.Clone();
        (copy[first], copy[second]) = (copy[second], copy[first]);
        return new RacePriority(copy);
    }

    public int RankOf(string race) => Array.FindIndex(_ranked, r => string.Equals(r, race, StringComparison.Ordinal));

    public bool Equals(RacePriority? other) => other is not null && _ranked.AsSpan().SequenceEqual(other._ranked);

    public override bool Equals(object? obj) => Equals(obj as RacePriority);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var race in _ranked)
            hash.Add(race, StringComparer.Ordinal);
        return hash.ToHashCode();
    }

    public static bool operator ==(RacePriority? left, RacePriority? right) =>
        left is null ? right is null : left.Equals(right);

    public static bool operator !=(RacePriority? left, RacePriority? right) => !(left == right);
}
