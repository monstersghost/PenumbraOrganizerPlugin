namespace PenumbraOrganizer.Plugin.Organizer.BodyTypes;

/// <summary>
/// The user's body ranking, first is highest. An immutable snapshot with CONTENT equality.
/// </summary>
/// <remarks>
/// It rides inside SortSelection, which SortPanel compares with <c>!=</c> to decide whether the last
/// sort is stale. A plain List would compare by reference: mutating it in place would never trip
/// the hint, and allocating a fresh one every frame would trip it permanently. Content equality
/// makes a reorder differ and an unchanged list not, and Reset equal the default again.
/// </remarks>
public sealed class BodyPriority : IEquatable<BodyPriority>
{
    private readonly string[] _labels;
    private readonly System.Collections.ObjectModel.ReadOnlyCollection<string> _view;
    private readonly Dictionary<string, int> _rank;

    private BodyPriority(string[] labels)
    {
        _labels = labels;
        // A wrapper, never the array itself: handing out the array would let a caller cast back and
        // mutate it, changing equality and hashing while _rank stayed stale.
        _view = Array.AsReadOnly(labels);
        _rank = new Dictionary<string, int>(labels.Length, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < labels.Length; i++)
            _rank[labels[i]] = i;
    }

    public IReadOnlyList<string> Labels => _view;

    /// <summary>
    /// <see cref="Normalize"/>, and whether the repaired list must be written back. An empty saved
    /// list means "the default" and is never rewritten, so it keeps following the shipped order.
    /// A non-empty list that normalization changed IS written back: left as it was, a removed
    /// body would keep its old rank in the file and regain it if it ever returned.
    /// </summary>
    public static BodyPriority Repair(
        IReadOnlyList<string>? saved, IEnumerable<string> allLabels, out bool needsSave)
    {
        var result = Normalize(saved, allLabels);
        needsSave = saved is { Count: > 0 } && !saved.SequenceEqual(result._labels, StringComparer.Ordinal);
        return result;
    }

    public static BodyPriority Default(IEnumerable<string> allLabels) => Normalize(null, allLabels);

    /// <summary>
    /// Repairs a saved list against the current body list: canonical spelling from the body list,
    /// first occurrence wins, unknown labels dropped, missing bodies appended in list order.
    /// </summary>
    public static BodyPriority Normalize(IEnumerable<string>? saved, IEnumerable<string> allLabels)
    {
        var canonical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var label in allLabels)
        {
            if (!string.IsNullOrWhiteSpace(label) && canonical.TryAdd(label, label))
                order.Add(label);
        }

        var result = new List<string>(order.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (saved is not null)
        {
            foreach (var entry in saved)
            {
                if (string.IsNullOrWhiteSpace(entry) || !canonical.TryGetValue(entry, out var spelled))
                    continue;
                if (seen.Add(spelled))
                    result.Add(spelled);
            }
        }

        foreach (var label in order)
        {
            if (seen.Add(label))
                result.Add(label);
        }

        return new BodyPriority([.. result]);
    }

    /// <summary>Position in the list, or <see cref="int.MaxValue"/> for a label not in it.</summary>
    public int Rank(string label) => _rank.TryGetValue(label, out var rank) ? rank : int.MaxValue;

    public BodyPriority MoveUp(int index) => Swap(index, index - 1);

    public BodyPriority MoveDown(int index) => Swap(index, index + 1);

    private BodyPriority Swap(int from, int to)
    {
        if (from < 0 || from >= _labels.Length || to < 0 || to >= _labels.Length)
            return this;

        var copy = (string[])_labels.Clone(); // a private array, so the returned snapshot owns it
        (copy[from], copy[to]) = (copy[to], copy[from]);
        return new BodyPriority(copy);
    }

    public bool Equals(BodyPriority? other) =>
        other is not null && _labels.AsSpan().SequenceEqual(other._labels);

    public override bool Equals(object? obj) => Equals(obj as BodyPriority);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var label in _labels)
            hash.Add(label, StringComparer.Ordinal);
        return hash.ToHashCode();
    }

    public static bool operator ==(BodyPriority? left, BodyPriority? right) =>
        left is null ? right is null : left.Equals(right);

    public static bool operator !=(BodyPriority? left, BodyPriority? right) => !(left == right);
}
