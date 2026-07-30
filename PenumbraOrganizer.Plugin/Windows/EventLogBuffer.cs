using System.Collections.Concurrent;

namespace PenumbraOrganizer.Plugin.Windows;

/// <summary>
/// Splits the live-event log into a lock-free write side callable from any thread (Penumbra raises
/// ModAdded/ModDeleted/ModMoved on threads it does not document) and a read side touched only by the
/// framework thread. Before this existed, a plain List was Insert(0, ...)-ed from Penumbra's
/// callbacks while Draw() enumerated it, which is an InvalidOperationException at best and a torn
/// backing array at worst. Drain() is called once per framework update; Lines is safe to enumerate
/// for the rest of that frame because nothing else ever touches it.
/// </summary>
public sealed class EventLogBuffer
{
    public const int MaxLines = 200;

    private readonly ConcurrentQueue<string> _incoming = new();
    private readonly List<string> _lines = [];

    /// <summary>Callable from any thread.</summary>
    public void Add(string line) => _incoming.Enqueue(line);

    /// <summary>Framework thread only.</summary>
    public IReadOnlyList<string> Lines => _lines;

    /// <summary>
    /// Framework thread only. Moves queued lines into <see cref="Lines"/>, most recently arrived
    /// first. Note this is queue ARRIVAL order, not a chronological ordering of when the events
    /// fired: callbacks on different threads have no meaningful universal order. Timestamps are
    /// captured at callback time in the line text itself.
    /// </summary>
    public void Drain()
    {
        if (_incoming.IsEmpty)
            return;

        var drained = new List<string>();
        while (_incoming.TryDequeue(out var line))
            drained.Add(line);

        if (drained.Count == 0)
            return;

        // One InsertRange of a reversed batch rather than repeated Insert(0, ...), which is O(n)
        // per line. Harmless at a 200-line cap either way; the batch form is simply clearer.
        drained.Reverse();
        _lines.InsertRange(0, drained);

        if (_lines.Count > MaxLines)
            _lines.RemoveRange(MaxLines, _lines.Count - MaxLines);
    }
}
