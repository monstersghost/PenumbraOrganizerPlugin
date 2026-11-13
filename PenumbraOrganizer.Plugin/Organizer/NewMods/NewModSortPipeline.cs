using PenumbraOrganizer.Plugin.LibraryWork;
using PenumbraOrganizer.Plugin.Organizer.Collections;
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Organizer.NewMods;

public enum NewModChoice { SortNow, ReviewFirst, NotNow }

/// <summary>The confirm window's answer.</summary>
public enum NewModConfirmChoice { Apply, ReviewFirst, Cancel }

public enum NewModPipelineStage { Idle, Admitting, Scanning, Confirming, Applying }

public enum NewModRunOutcome { Done, Failed, Cancelled }

public sealed record NewModRunResult(NewModRunOutcome Outcome, string Message);

/// <summary>
/// The new-mod path as a pure state machine: popup or automatic run, then admission, rescan, scoped
/// root-only sort, confirmation (interactive runs only) and Apply through the host. Driven once per
/// framework update; never touches Penumbra itself and never calls SetModPath (Apply goes only
/// through INewModSortHost.StartApply).
/// </summary>
/// <remarks>
/// "Pending" mods are offered mods not yet sorted: the popup and /porganizer sortnew cover them.
/// A batch offered while a run is in progress is queued and decided after the run, never merged
/// into the running scope. A failed or cancelled run returns its mods to pending.
/// The popup's Sort now and both chat commands stage the sort and wait in Confirming for the confirm
/// window; only the opt-in automatic mode applies without asking. UI calls (UserChose, Confirm) only
/// record the answer; the work happens in Update on the framework thread.
/// </remarks>
public sealed class NewModSortPipeline
{
    public const int AutomaticCap = 20;
    public static readonly TimeSpan AdmissionTimeout = TimeSpan.FromMinutes(2);

    // Apply: automatic, applies without asking. Confirm: stages, then waits for the confirm window.
    // Review: stages and opens the Review tab.
    private enum RunMode { Apply, Confirm, Review }

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
        public bool FromCommand { get; init; }
        public NewModPreview? Preview { get; set; }
        public NewModConfirmChoice? ConfirmChoice { get; set; }
        public int ConfirmScanGeneration { get; set; }
        public long ConfirmCompletionGeneration { get; set; }
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

    /// <summary>
    /// The popup to draw, or null. Hidden while a run is in progress, when the popup is turned off, and
    /// under Do nothing. Warnings reflect the host's state at the moment of reading.
    /// </summary>
    public NewModBanner? Banner =>
        _run is null && _bannerVisible && _pendingEligible > 0 && _host.ShowPopup
        && _host.Setting != NewModAction.DoNothing
            ? NewModPromptText.Banner(_pendingEligible, _host.HasStagedProposals, _host.HasSessionOverrides)
            : null;

    /// <summary>What the confirm window shows, or null when no staged sort is waiting for an answer.</summary>
    public NewModPreview? Preview =>
        _run is { Stage: NewModPipelineStage.Confirming, ConfirmChoice: null } run ? run.Preview : null;

    /// <summary>A status line while an interactive run (popup or command) scans before it can confirm.</summary>
    public string? ProgressText =>
        _run is { Mode: not RunMode.Apply, Stage: NewModPipelineStage.Admitting or NewModPipelineStage.Scanning }
            ? NewModPromptText.Preparing
            : null;

    /// <summary>Eligible new mods offered but not yet sorted (what the popup counts).</summary>
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

        StartRun(TakePending(), choice == NewModChoice.ReviewFirst ? RunMode.Review : RunMode.Confirm);
    }

    /// <summary>The confirm window's answer. Recorded only; Update acts on it. Ignored unless confirming.</summary>
    public void Confirm(NewModConfirmChoice choice)
    {
        if (Preview is null)
            return;

        _run!.ConfirmChoice = choice;
    }

    /// <summary>
    /// /porganizer sortnew and sortroot: skip the debounce and the cap, stage the sort, open the confirm
    /// window, and answer in chat.
    /// </summary>
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
            StartRun(TakePending(), RunMode.Confirm, fromCommand: true);
        }
        else
        {
            StartRun(new Batch([], 0), RunMode.Confirm, everyLooseMod: true, fromCommand: true);
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
            case NewModPipelineStage.Confirming:
                UpdateConfirming(_run);
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
        if (!_host.ShowPopup)
            _host.Chat(NewModPromptText.PopupOff(_pendingEligible));
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

        if (run.Mode == RunMode.Confirm)
        {
            run.Preview = NewModPreview.Build(_host.StagedMoves(), result.ScopeText, run.NewMods);
            run.ConfirmScanGeneration = _host.ScanGeneration;
            run.ConfirmCompletionGeneration = _host.OperationCompletionGeneration;
            run.Stage = NewModPipelineStage.Confirming;
            if (run.FromCommand)
                _host.Chat(NewModPromptText.ConfirmOpened(run.Preview.Count, run.NewMods));
            return;
        }

        StartApply(run);
    }

    private void UpdateConfirming(Run run)
    {
        switch (run.ConfirmChoice)
        {
            case NewModConfirmChoice.Apply:
                StartApply(run);
                return;
            case NewModConfirmChoice.ReviewFirst:
                _host.OpenReview();
                Finish(run, NewModRunOutcome.Done, NewModPromptText.ReviewOpened(run.Changed), chat: false);
                return;
            case NewModConfirmChoice.Cancel:
                _host.DiscardStaged();
                Finish(run, NewModRunOutcome.Cancelled, NewModPromptText.Cancelled(run.NewMods), chat: false);
                return;
        }

        // Still waiting. The staged sort is also on the Review tab, where a rescan or an Apply can
        // replace it; the window must not then Apply something other than what it listed.
        if (_host.ScanGeneration != run.ConfirmScanGeneration
            || _host.OperationCompletionGeneration != run.ConfirmCompletionGeneration
            || !_host.HasStagedProposals)
        {
            Finish(run, NewModRunOutcome.Failed, NewModPromptText.Failed(NewModPromptText.ConfirmStale, run.NewMods));
        }
    }

    private void StartApply(Run run)
    {
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

    private void StartRun(Batch batch, RunMode mode, bool everyLooseMod = false, bool fromCommand = false)
    {
        _run = new Run
        {
            Identifiers = everyLooseMod ? null : new HashSet<string>(batch.Identifiers, StringComparer.Ordinal),
            Eligible = batch.Eligible,
            Mode = mode,
            FromCommand = fromCommand,
        };
    }

    private void Finish(Run run, NewModRunOutcome outcome, string message, bool chat = true)
    {
        _run = null;
        LastResult = new NewModRunResult(outcome, message);
        if (outcome is NewModRunOutcome.Failed or NewModRunOutcome.Cancelled && run.Identifiers is { } ids)
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
