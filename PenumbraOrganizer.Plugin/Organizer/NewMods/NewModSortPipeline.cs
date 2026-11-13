using PenumbraOrganizer.Plugin.LibraryWork;
using PenumbraOrganizer.Plugin.Organizer.Collections;
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Organizer.NewMods;

public enum NewModChoice { SortNow, ReviewFirst, NotNow }

public enum NewModPipelineStage { Idle, Admitting, Scanning, Applying }

public enum NewModRunOutcome { Done, Failed }

public sealed record NewModRunResult(NewModRunOutcome Outcome, string Message);

/// <summary>
/// The new-mod path as a pure state machine: banner or automatic run, then admission, rescan, scoped
/// root-only sort, and Apply through the host. Driven once per framework update; never touches
/// Penumbra itself and never calls SetModPath (Apply goes only through INewModSortHost.StartApply).
/// </summary>
/// <remarks>
/// "Pending" mods are offered mods not yet sorted: the banner and /porganizer sortnew cover them.
/// A batch offered while a run is in progress is queued and decided after the run, never merged
/// into the running scope. A failed run returns its mods to pending.
/// </remarks>
public sealed class NewModSortPipeline
{
    public const int AutomaticCap = 20;
    public static readonly TimeSpan AdmissionTimeout = TimeSpan.FromMinutes(2);

    private enum RunMode { Apply, Review }

    private sealed class Run
    {
        public required IReadOnlySet<string>? Identifiers { get; init; } // null = every loose mod (sortroot)
        public required int Eligible { get; init; }
        public required RunMode Mode { get; init; }
        public NewModPipelineStage Stage { get; set; } = NewModPipelineStage.Admitting;
        public DateTimeOffset? AdmittingSince { get; set; }
        public int ScanGenerationAtStart { get; set; }
        public long CompletionGenerationAtStart { get; set; }
        public int Changed { get; set; }
        public int Missing { get; set; }
        public bool NewMods => Identifiers is not null;
    }

    private readonly record struct Batch(IReadOnlyCollection<string> Identifiers, int Eligible);

    private readonly INewModSortHost _host;
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    private readonly Queue<Batch> _queue = new();
    private int _pendingEligible;
    private bool _bannerVisible;
    private Run? _run;

    public NewModSortPipeline(INewModSortHost host)
    {
        _host = host;
    }

    public NewModPipelineStage Stage => _run?.Stage ?? NewModPipelineStage.Idle;

    /// <summary>The banner to draw, or null. Warnings reflect the host's state at the moment of reading.</summary>
    public NewModBanner? Banner =>
        _run is null && _bannerVisible && _pendingEligible > 0
            ? NewModPromptText.Banner(_pendingEligible, _host.HasStagedProposals, _host.HasSessionOverrides)
            : null;

    /// <summary>Eligible new mods offered but not yet sorted (what the banner counts).</summary>
    public int PendingCount => _pendingEligible;

    /// <summary>Batches offered during a run, waiting for it to finish.</summary>
    public int QueuedBatches => _queue.Count;

    /// <summary>How the last run ended, or null if none has.</summary>
    public NewModRunResult? LastResult { get; private set; }

    /// <summary>A debounced batch of new mods; eligibleCount is how many a root-only sort would move.</summary>
    public void Offer(IReadOnlyCollection<string> batch, int eligibleCount)
    {
        if (eligibleCount <= 0 || batch.Count == 0)
            return;

        if (_run is not null || _queue.Count > 0)
        {
            _queue.Enqueue(new Batch(batch.ToArray(), eligibleCount));
            return;
        }

        Decide(new Batch(batch, eligibleCount));
    }

    /// <summary>Adds mods to pending without a banner or a run (the undebounced mods before sortnew).</summary>
    public void AddPending(IReadOnlyCollection<string> batch, int eligibleCount)
    {
        if (eligibleCount <= 0 || batch.Count == 0)
            return;

        AddToPending(batch, eligibleCount);
    }

    public void UserChose(NewModChoice choice)
    {
        if (Banner is null)
            return;

        _bannerVisible = false;
        if (choice == NewModChoice.NotNow)
            return;

        StartRun(TakePending(), choice == NewModChoice.ReviewFirst ? RunMode.Review : RunMode.Apply);
    }

    /// <summary>/porganizer sortnew and sortroot: skip the debounce and the cap, Apply, answer in chat.</summary>
    public void RunCommand(CommandKind kind, bool force)
    {
        if (kind is not (CommandKind.SortNew or CommandKind.SortRoot))
            return;

        if (kind == CommandKind.SortNew && _pending.Count == 0)
        {
            _host.Chat(NewModPromptText.NothingPending);
            return;
        }

        if (_run is not null)
        {
            _host.Chat(NewModPromptText.AlreadyRunning);
            return;
        }

        if (!force && (_host.HasStagedProposals || _host.HasSessionOverrides))
        {
            _host.Chat(NewModPromptText.StagedWithoutForce(kind));
            return;
        }

        if (_host.AdmissionRejection() is { } reason)
        {
            _host.Chat(NewModPromptText.Busy(reason));
            return;
        }

        if (kind == CommandKind.SortNew)
        {
            _bannerVisible = false;
            StartRun(TakePending(), RunMode.Apply);
        }
        else
        {
            StartRun(new Batch([], 0), RunMode.Apply, everyLooseMod: true);
        }
    }

    /// <summary>Framework thread, once per update.</summary>
    public void Update(DateTimeOffset now)
    {
        if (_run is null)
        {
            while (_run is null && _queue.Count > 0)
                Decide(_queue.Dequeue());
            if (_run is null)
                return;
        }

        switch (_run.Stage)
        {
            case NewModPipelineStage.Admitting:
                UpdateAdmitting(_run, now);
                break;
            case NewModPipelineStage.Scanning:
                UpdateScanning(_run);
                break;
            case NewModPipelineStage.Applying:
                UpdateApplying(_run);
                break;
        }
    }

    private void Decide(Batch batch)
    {
        var automatic = _host.Setting == NewModAction.SortAutomatically
            && batch.Eligible <= AutomaticCap
            && !_host.HasStagedProposals
            && !_host.HasSessionOverrides;

        if (automatic)
        {
            StartRun(batch, RunMode.Apply);
            return;
        }

        AddToPending(batch.Identifiers, batch.Eligible);
        if (_host.Setting == NewModAction.DoNothing)
            return;

        _bannerVisible = true;
        if (!_host.IsWindowOpen)
            _host.Chat(NewModPromptText.WindowClosed(_pendingEligible));
    }

    private void UpdateAdmitting(Run run, DateTimeOffset now)
    {
        run.AdmittingSince ??= now;

        var reason = _host.AdmissionRejection();
        if (reason is null)
        {
            var generation = _host.ScanGeneration;
            reason = _host.StartScan();
            if (reason is null)
            {
                run.ScanGenerationAtStart = generation;
                run.Stage = NewModPipelineStage.Scanning;
                return;
            }
        }

        if (now - run.AdmittingSince.Value >= AdmissionTimeout)
            Finish(run, NewModRunOutcome.Failed, NewModPromptText.GaveUpWaiting(reason, run.NewMods));
    }

    private void UpdateScanning(Run run)
    {
        var scan = _host.ScanState;
        if (scan.IsRunning)
            return; // ours, or another scan that started after ours: sort only on a settled library

        if (_host.ScanGeneration == run.ScanGenerationAtStart)
        {
            switch (scan.LastOutcome)
            {
                case LibraryWorkOutcome.StaleModList:
                    run.Stage = NewModPipelineStage.Admitting;
                    run.AdmittingSince = null;
                    return;
                case LibraryWorkOutcome.Failed:
                    Finish(run, NewModRunOutcome.Failed,
                        NewModPromptText.Failed(scan.LastError ?? "The library scan failed.", run.NewMods));
                    return;
                case LibraryWorkOutcome.Cancelled:
                    Finish(run, NewModRunOutcome.Failed, NewModPromptText.Failed("The library scan was cancelled.", run.NewMods));
                    return;
            }
        }

        Sort(run);
    }

    private void Sort(Run run)
    {
        var result = _host.SortScoped(new SortScope(RootOnly: true, Identifiers: run.Identifiers));
        run.Changed = result.Changed;
        run.Missing = run.NewMods ? Math.Max(0, run.Eligible - result.InScope) : 0;

        if (result.Changed <= 0)
        {
            Finish(run, NewModRunOutcome.Done, NewModPromptText.NothingToMove(run.NewMods, run.Missing));
            return;
        }

        if (run.Mode == RunMode.Review)
        {
            _host.OpenReview();
            Finish(run, NewModRunOutcome.Done, NewModPromptText.ReviewOpened(result.Changed), chat: false);
            return;
        }

        run.CompletionGenerationAtStart = _host.OperationCompletionGeneration;
        if (_host.StartApply() is { } rejection)
        {
            Finish(run, NewModRunOutcome.Failed, NewModPromptText.Failed(rejection, run.NewMods));
            return;
        }

        run.Stage = NewModPipelineStage.Applying;
    }

    private void UpdateApplying(Run run)
    {
        if (_host.OperationCompletionGeneration <= run.CompletionGenerationAtStart)
            return;

        var state = _host.OperationState;
        if (state.Stage == OperationStage.Completed)
        {
            var moved = state.SuccessfulTargets > 0 ? state.SuccessfulTargets : run.Changed;
            Finish(run, NewModRunOutcome.Done, NewModPromptText.Moved(moved, run.NewMods, run.Missing));
            return;
        }

        var reason = state.LastError
            ?? (state.Stage == OperationStage.CompletedWithItemFailures
                ? "Some mods could not be moved. Details are on the Review tab."
                : $"The Apply ended as {state.Stage?.ToString() ?? "unknown"}.");
        Finish(run, NewModRunOutcome.Failed, NewModPromptText.Failed(reason, run.NewMods));
    }

    private void StartRun(Batch batch, RunMode mode, bool everyLooseMod = false)
    {
        _run = new Run
        {
            Identifiers = everyLooseMod ? null : new HashSet<string>(batch.Identifiers, StringComparer.Ordinal),
            Eligible = batch.Eligible,
            Mode = mode,
        };
    }

    private void Finish(Run run, NewModRunOutcome outcome, string message, bool chat = true)
    {
        _run = null;
        LastResult = new NewModRunResult(outcome, message);
        if (outcome == NewModRunOutcome.Failed && run.Identifiers is { } ids)
            AddToPending(ids, run.Eligible);
        if (chat)
            _host.Chat(message);
    }

    private void AddToPending(IEnumerable<string> identifiers, int eligible)
    {
        foreach (var id in identifiers)
            _pending.Add(id);
        _pendingEligible += eligible;
    }

    private Batch TakePending()
    {
        var batch = new Batch(_pending.ToArray(), _pendingEligible);
        _pending.Clear();
        _pendingEligible = 0;
        return batch;
    }
}
