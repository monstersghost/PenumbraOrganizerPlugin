using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

/// <summary> No-op sink for tests that don't care about diagnostics events. </summary>
public sealed class NoOpDiagnosticsSink : IDiagnosticsSink
{
    public void RecordSlowCall(Guid? operationId, string identifier, TimeSpan duration) { }
    public void RecordSlowLiveSnapshot(Guid? operationId, TimeSpan duration) { }
    public void RecordSlowRefresh(Guid? operationId, TimeSpan duration) { }
}

/// <summary> Recording sink for tests that must assert a diagnostic event was actually emitted. </summary>
public sealed class RecordingDiagnosticsSink : IDiagnosticsSink
{
    private readonly List<(Guid? OperationId, string Identifier, TimeSpan Duration)> _slowCalls = [];
    private readonly List<(Guid? OperationId, TimeSpan Duration)> _slowLiveSnapshots = [];
    private readonly List<(Guid? OperationId, TimeSpan Duration)> _slowRefreshes = [];

    public IReadOnlyList<(Guid? OperationId, string Identifier, TimeSpan Duration)> SlowCalls => _slowCalls;
    public IReadOnlyList<(Guid? OperationId, TimeSpan Duration)> SlowLiveSnapshots => _slowLiveSnapshots;
    public IReadOnlyList<(Guid? OperationId, TimeSpan Duration)> SlowRefreshes => _slowRefreshes;

    public void RecordSlowCall(Guid? operationId, string identifier, TimeSpan duration) =>
        _slowCalls.Add((operationId, identifier, duration));

    public void RecordSlowLiveSnapshot(Guid? operationId, TimeSpan duration) =>
        _slowLiveSnapshots.Add((operationId, duration));

    public void RecordSlowRefresh(Guid? operationId, TimeSpan duration) =>
        _slowRefreshes.Add((operationId, duration));
}
