namespace PenumbraOrganizer.Plugin.Organizer.Operations;

/// <summary> Design doc section 5: PathMutationOperation/VerificationSettlement/RefreshSettlement
/// depend on this abstraction, not on a file path directly. </summary>
public interface IDiagnosticsSink
{
    void RecordSlowCall(Guid? operationId, string identifier, TimeSpan duration);
    void RecordSlowLiveSnapshot(Guid? operationId, TimeSpan duration);
    void RecordSlowRefresh(Guid? operationId, TimeSpan duration);
}

/// <summary> Writes through DiagnosticsLog, which already swallows its own write failures (Plan
/// A2) - no additional exception handling needed here. </summary>
public sealed class FileDiagnosticsSink(string diagnosticsLogPath) : IDiagnosticsSink
{
    public void RecordSlowCall(Guid? operationId, string identifier, TimeSpan duration) =>
        DiagnosticsLog.Append(diagnosticsLogPath, new DiagnosticEvent(
            operationId, DiagnosticEventKind.SlowCall, DateTimeOffset.UtcNow,
            (long)duration.TotalMilliseconds, null, null, null, identifier));

    public void RecordSlowLiveSnapshot(Guid? operationId, TimeSpan duration) =>
        DiagnosticsLog.Append(diagnosticsLogPath, new DiagnosticEvent(
            operationId, DiagnosticEventKind.SlowLiveSnapshot, DateTimeOffset.UtcNow,
            (long)duration.TotalMilliseconds, null, null, null, null));

    public void RecordSlowRefresh(Guid? operationId, TimeSpan duration) =>
        DiagnosticsLog.Append(diagnosticsLogPath, new DiagnosticEvent(
            operationId, DiagnosticEventKind.SlowRefresh, DateTimeOffset.UtcNow,
            (long)duration.TotalMilliseconds, null, null, null, null));
}
