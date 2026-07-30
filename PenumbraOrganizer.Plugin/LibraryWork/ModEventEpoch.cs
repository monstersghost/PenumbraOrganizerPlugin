namespace PenumbraOrganizer.Plugin.LibraryWork;

/// <summary>
/// Counts observed Penumbra mod-list changes. A background library run snapshots this before it
/// starts and compares it before publishing: any difference means the run was built against a mod
/// list that no longer exists, so the result is discarded rather than published. Interlocked rather
/// than locked because the write side is a Penumbra IPC callback on an undocumented thread and must
/// never block it.
/// </summary>
public sealed class ModEventEpoch
{
    private long _value;

    /// <summary>Callable from any thread.</summary>
    public void Increment() => Interlocked.Increment(ref _value);

    /// <summary>Callable from any thread.</summary>
    public long Current => Interlocked.Read(ref _value);
}
