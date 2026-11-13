using System.Collections.Concurrent;

namespace PenumbraOrganizer.Plugin.Organizer.NewMods;

/// <summary>
/// Collects Penumbra mod events (which arrive on undocumented threads) into a debounced batch of
/// genuinely new mod identifiers. Producers only enqueue; the framework thread drains.
/// </summary>
/// <remarks>
/// A mod is new only when its identifier was unknown at the moment its event is drained, so a
/// rediscover of known mods yields nothing. Events are held until the known set has been seeded.
/// </remarks>
public sealed class NewModTracker
{
    public static readonly TimeSpan Debounce = TimeSpan.FromSeconds(5);

    private enum Kind { Added, Deleted, Moved, Reset }

    private readonly record struct Evt(Kind Kind, string A, string? B);

    private readonly ConcurrentQueue<Evt> _inbox = new();
    private readonly Func<DateTimeOffset> _clock;
    private readonly HashSet<string> _known = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    private long _lastEventTicks;
    private volatile bool _needsSeed = true;

    public NewModTracker(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>True until the known set has been seeded (and again after a reset event).</summary>
    public bool NeedsSeed => _needsSeed;

    public IReadOnlySet<string> KnownIdentifiers => _known;

    public bool HasPending
    {
        get
        {
            Drain();
            return _pending.Count > 0;
        }
    }

    public void EnqueueAdded(string directory) => Enqueue(new Evt(Kind.Added, directory, null));

    public void EnqueueDeleted(string directory) => Enqueue(new Evt(Kind.Deleted, directory, null));

    public void EnqueueMoved(string oldDirectory, string newDirectory) =>
        Enqueue(new Evt(Kind.Moved, oldDirectory, newDirectory));

    public void EnqueueDirectoryChanged() => Enqueue(new Evt(Kind.Reset, string.Empty, null));

    public void EnqueueDisposed() => Enqueue(new Evt(Kind.Reset, string.Empty, null));

    private void Enqueue(Evt evt)
    {
        Interlocked.Exchange(ref _lastEventTicks, _clock().UtcTicks);
        _inbox.Enqueue(evt);
    }

    /// <summary>Framework thread: replaces the known set from a full mod-list read.</summary>
    public void Seed(IEnumerable<string> identifiers)
    {
        _known.Clear();
        foreach (var id in identifiers)
            _known.Add(id);
        _needsSeed = false;
    }

    /// <summary>Framework thread: a published scan's identifiers are known.</summary>
    public void PublishScan(IEnumerable<string> identifiers)
    {
        foreach (var id in identifiers)
            _known.Add(id);
    }

    /// <summary>The pending batch once no event has arrived for the debounce window, else null.</summary>
    public IReadOnlyList<string>? TakeReady()
    {
        Drain();
        if (_pending.Count == 0)
            return null;

        var quietFor = _clock().UtcTicks - Interlocked.Read(ref _lastEventTicks);
        return quietFor >= Debounce.Ticks ? Flush() : null;
    }

    /// <summary>Every pending mod, skipping the debounce.</summary>
    public IReadOnlyList<string> TakeAll()
    {
        Drain();
        return Flush();
    }

    private List<string> Flush()
    {
        var batch = _pending.ToList();
        _pending.Clear();
        return batch;
    }

    private void Drain()
    {
        while (!_needsSeed && _inbox.TryDequeue(out var evt))
        {
            switch (evt.Kind)
            {
                case Kind.Added:
                    if (_known.Add(evt.A))
                        _pending.Add(evt.A);
                    break;
                case Kind.Deleted:
                    _pending.Remove(evt.A);
                    _known.Remove(evt.A);
                    break;
                case Kind.Moved:
                    _known.Remove(evt.A);
                    _known.Add(evt.B!);
                    if (_pending.Remove(evt.A))
                        _pending.Add(evt.B!);
                    break;
                case Kind.Reset:
                    _pending.Clear();
                    _known.Clear();
                    _needsSeed = true;
                    break;
            }
        }
    }
}
