using System.Diagnostics;

namespace PenumbraOrganizer.Plugin.Organizer.Operations;

/// <summary>
/// Minimal elapsed-time seam so frame-budget and settlement timing can be driven deterministically
/// in tests. Mirrors System.Diagnostics.Stopwatch's static GetTimestamp/GetElapsedTime shape, so the
/// production implementation is a pass-through and no DI framework or external dependency is needed.
/// Timestamps are process-relative ticks and must never be persisted (design doc section 6).
/// </summary>
public interface IElapsedTimeSource
{
    long GetTimestamp();
    TimeSpan GetElapsedTime(long startTimestamp);
}

public sealed class StopwatchElapsedTimeSource : IElapsedTimeSource
{
    public long GetTimestamp() => Stopwatch.GetTimestamp();
    public TimeSpan GetElapsedTime(long startTimestamp) => Stopwatch.GetElapsedTime(startTimestamp);
}
