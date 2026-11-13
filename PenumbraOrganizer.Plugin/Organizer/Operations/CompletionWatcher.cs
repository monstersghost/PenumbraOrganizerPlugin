namespace PenumbraOrganizer.Plugin.Organizer.Operations;

/// <summary>
/// Turns "an Apply or Restore completed" into exactly one library rescan, driven from the plugin's
/// framework update so it works with the main window closed. A rescan the engine refuses is retried
/// on later updates until it starts. No UI dependency.
/// </summary>
public sealed class CompletionWatcher
{
    private readonly Func<bool> _tryRequestScan;
    private long _lastSeen;
    private bool _scanPending;

    /// <param name="tryRequestScan">Returns true once the scan started (or must not be retried).</param>
    public CompletionWatcher(Func<bool> tryRequestScan)
    {
        _tryRequestScan = tryRequestScan;
    }

    /// <summary>Call every framework update with the controller's current completion generation and kind.</summary>
    public void Update(long completionGeneration, OperationType? kind)
    {
        if (completionGeneration > _lastSeen)
        {
            _lastSeen = completionGeneration;
            if (kind is OperationType.Apply or OperationType.Restore)
                _scanPending = true;
        }

        if (_scanPending && _tryRequestScan())
            _scanPending = false;
    }
}
