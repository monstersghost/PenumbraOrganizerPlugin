namespace PenumbraOrganizer.Plugin.Organizer.Operations;

/// <summary>
/// Design doc section 5's "checkpoint after each step or cascade batch" requirement, isolated from
/// the mutation loop so it's independently testable. Tracks its own steps-since-last-checkpoint
/// and time-since-last-checkpoint so it can be called once per step in a burst (as
/// PathMutationOperation.Advance does) without rewriting the journal file on every call - only when
/// CheckpointPolicy.IsDue actually says so, or when force is requested (stage transitions,
/// cancellation-intent persistence).
/// </summary>
public sealed class OperationCheckpointer
{
    private readonly IElapsedTimeSource _clock;
    private readonly string _bundleDirectory;
    private int _lastCheckpointedProcessedStepCount;
    private long _lastCheckpointTimestamp;

    public OperationCheckpointer(IElapsedTimeSource clock, string bundleDirectory)
    {
        _clock = clock;
        _bundleDirectory = bundleDirectory;
        _lastCheckpointTimestamp = clock.GetTimestamp();
    }

    public void CheckpointIfDue(OperationJournal journal) => CheckpointIfDue(journal, force: false);

    public void CheckpointIfDue(OperationJournal journal, bool force)
    {
        var delta = journal.ProcessedStepCount - _lastCheckpointedProcessedStepCount;
        var elapsed = _clock.GetElapsedTime(_lastCheckpointTimestamp);
        if (!force && !CheckpointPolicy.IsDue(delta, elapsed))
            return;

        OperationJournalCodec.Save(OperationBundlePaths.JournalPath(_bundleDirectory), journal);
        _lastCheckpointedProcessedStepCount = journal.ProcessedStepCount;
        _lastCheckpointTimestamp = _clock.GetTimestamp();
    }
}
