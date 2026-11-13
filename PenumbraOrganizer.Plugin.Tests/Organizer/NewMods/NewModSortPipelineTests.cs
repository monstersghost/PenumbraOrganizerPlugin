using PenumbraOrganizer.Plugin.LibraryWork;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Collections;
using PenumbraOrganizer.Plugin.Organizer.NewMods;
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.NewMods;

public class NewModSortPipelineTests
{
    /// <summary>
    /// Stands in for the plugin. A scan or Apply stays running until the test finishes it. With
    /// PostApplyRescan on, finishing an Apply starts a rescan the way B0's CompletionWatcher does.
    /// </summary>
    private sealed class FakeHost : INewModSortHost
    {
        public NewModAction Setting { get; set; } = NewModAction.Ask;
        public bool ShowPopup { get; set; } = true;
        public bool HasStagedProposals { get; set; }
        public bool HasSessionOverrides { get; set; }
        public string? ExternalRejection { get; set; }
        public string? ApplyRejection { get; set; }
        public bool PostApplyRescan { get; set; }
        public Func<SortScope, ScopedSortResult>? SortResult { get; set; }

        public int ScanStarts { get; private set; }
        public int ApplyStarts { get; private set; }
        public int ReviewOpens { get; private set; }
        public int Discards { get; private set; }
        public List<SortScope> Sorts { get; } = [];
        public List<string> Chats { get; } = [];
        public bool ScanRunning => ScanState.IsRunning;
        public bool ApplyRunning { get; private set; }

        public int ScanGeneration { get; private set; }
        public LibraryWorkStateSnapshot ScanState { get; private set; } = LibraryWorkStateSnapshot.Idle;
        public long OperationCompletionGeneration => OperationState.CompletionGeneration;
        public OperationStateSnapshot OperationState { get; private set; } = OperationStateSnapshot.Idle;

        public string? AdmissionRejection() =>
            ExternalRejection
            ?? (ApplyRunning ? "An operation is running." : null)
            ?? (ScanRunning ? "Scan is already running." : null);

        public string? StartScan()
        {
            if (AdmissionRejection() is { } reason)
                return reason;
            ScanStarts++;
            ScanState = LibraryWorkStateSnapshot.Idle with { Phase = LibraryWorkPhase.Materializing, JobDisplayName = "Scan" };
            return null;
        }

        /// <summary>Settles the running scan. Completed publishes it, which clears staged work as a real scan does.</summary>
        public void FinishScan(LibraryWorkOutcome outcome = LibraryWorkOutcome.Completed, string? error = null)
        {
            Assert.True(ScanState.IsRunning, "no scan is running");
            if (outcome == LibraryWorkOutcome.Completed)
            {
                ScanGeneration++;
                HasStagedProposals = false;
                HasSessionOverrides = false;
            }

            ScanState = LibraryWorkStateSnapshot.Idle with { LastOutcome = outcome, LastError = error };
        }

        public ScopedSortResult SortScoped(SortScope scope)
        {
            Sorts.Add(scope);
            var result = SortResult?.Invoke(scope)
                ?? new ScopedSortResult(scope.Identifiers?.Count ?? 7, scope.Identifiers?.Count ?? 7);
            result = result with { ScopeText = $"Will sort {result.InScope} of 50 mods: loose in root." };
            HasStagedProposals = result.Changed > 0;
            var ids = scope.Identifiers?.OrderBy(i => i, StringComparer.Ordinal).ToArray()
                ?? Enumerable.Range(1, 7).Select(i => $"loose{i}").ToArray();
            Staged = ids.Take(result.Changed).Select(i => new StagedMove(i, i, "Sorted/" + i)).ToList();
            return result;
        }

        public List<StagedMove> Staged { get; private set; } = [];

        public IReadOnlyList<StagedMove> StagedMoves() => Staged;

        public void DiscardStaged()
        {
            Discards++;
            Staged = [];
            HasStagedProposals = false;
        }

        /// <summary>A rescan published from elsewhere (the Scan tab, or the post-Apply watcher).</summary>
        public void ExternalScanPublished()
        {
            ScanGeneration++;
            HasStagedProposals = false;
            Staged = [];
        }

        /// <summary>An Apply started and finished from the Review tab, outside the pipeline.</summary>
        public void ExternalApplyCompleted()
        {
            OperationState = OperationStateSnapshot.Idle with
            {
                Stage = OperationStage.Completed,
                Kind = OperationType.Apply,
                CompletionGeneration = OperationState.CompletionGeneration + 1,
            };
            HasStagedProposals = false;
        }

        public string? StartApply()
        {
            if (ApplyRejection is { } reason)
                return reason;
            if (AdmissionRejection() is { } admission)
                return admission;
            ApplyStarts++;
            ApplyRunning = true;
            return null;
        }

        public void FinishApply(OperationStage stage = OperationStage.Completed, int moved = -1, string? error = null)
        {
            Assert.True(ApplyRunning, "no Apply is running");
            ApplyRunning = false;
            OperationState = OperationStateSnapshot.Idle with
            {
                Stage = stage,
                Kind = OperationType.Apply,
                CompletionGeneration = OperationState.CompletionGeneration + 1,
                SuccessfulTargets = moved < 0 ? 0 : moved,
                TotalTargets = moved < 0 ? 0 : moved,
                LastError = error,
            };
            if (stage == OperationStage.Completed)
                HasStagedProposals = false;
            if (PostApplyRescan)
                Assert.Null(StartScan());
        }

        public void OpenReview() => ReviewOpens++;

        public void Chat(string message) => Chats.Add(message);
    }

    private static readonly DateTimeOffset T0 = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

    private static (NewModSortPipeline Pipeline, FakeHost Host) Make(NewModAction setting = NewModAction.Ask)
    {
        var host = new FakeHost { Setting = setting };
        return (new NewModSortPipeline(host), host);
    }

    private static string[] Ids(int count, string prefix = "mod") =>
        Enumerable.Range(1, count).Select(i => $"{prefix}{i}").ToArray();

    /// <summary>Drives one already-admitted automatic run through scan, sort and Apply to Done.</summary>
    private static void RunToApplied(NewModSortPipeline pipeline, FakeHost host, ref DateTimeOffset now)
    {
        pipeline.Update(now = now.AddSeconds(1));
        Assert.Equal(NewModPipelineStage.Scanning, pipeline.Stage);
        host.FinishScan();
        pipeline.Update(now = now.AddSeconds(1));
        Assert.Equal(NewModPipelineStage.Applying, pipeline.Stage);
        host.FinishApply();
        pipeline.Update(now = now.AddSeconds(1));
        Assert.Equal(NewModPipelineStage.Idle, pipeline.Stage);
        Assert.Equal(NewModRunOutcome.Done, pipeline.LastResult!.Outcome);
    }

    /// <summary>Drives one already-admitted interactive run through scan and sort to the confirm window.</summary>
    private static void RunToConfirming(NewModSortPipeline pipeline, FakeHost host, ref DateTimeOffset now)
    {
        pipeline.Update(now = now.AddSeconds(1));
        Assert.Equal(NewModPipelineStage.Scanning, pipeline.Stage);
        host.FinishScan();
        pipeline.Update(now = now.AddSeconds(1));
        Assert.Equal(NewModPipelineStage.Confirming, pipeline.Stage);
        Assert.NotNull(pipeline.Preview);
        Assert.Equal(0, host.ApplyStarts);
    }

    /// <summary>Drives one already-admitted interactive run through the confirm window's Apply to Done.</summary>
    private static void RunToConfirmedApplied(NewModSortPipeline pipeline, FakeHost host, ref DateTimeOffset now)
    {
        RunToConfirming(pipeline, host, ref now);
        pipeline.Confirm(NewModConfirmChoice.Apply);
        pipeline.Update(now = now.AddSeconds(1));
        Assert.Equal(NewModPipelineStage.Applying, pipeline.Stage);
        host.FinishApply();
        pipeline.Update(now = now.AddSeconds(1));
        Assert.Equal(NewModPipelineStage.Idle, pipeline.Stage);
        Assert.Equal(NewModRunOutcome.Done, pipeline.LastResult!.Outcome);
    }

    // ---- Offer decisions ----

    [Fact]
    public void Ask_ShowsBanner_AndStartsNothing()
    {
        var (pipeline, host) = Make(NewModAction.Ask);
        pipeline.Offer(Ids(3), 3);
        pipeline.Update(T0);

        Assert.NotNull(pipeline.Banner);
        Assert.Equal(3, pipeline.Banner!.Count);
        Assert.Equal("3 new mods are loose in the root. Sort them?", pipeline.Banner.Message);
        Assert.Empty(pipeline.Banner.Warnings);
        Assert.Equal(NewModPipelineStage.Idle, pipeline.Stage);
        Assert.Equal(0, host.ScanStarts);
        Assert.Empty(host.Chats);
    }

    [Fact]
    public void PopupOff_NoBanner_ChatsOnce_AndKeepsThemPending()
    {
        var (pipeline, host) = Make(NewModAction.Ask);
        host.ShowPopup = false;
        pipeline.Offer(Ids(3), 3);
        pipeline.Update(T0);
        pipeline.Update(T0.AddSeconds(1));

        Assert.Null(pipeline.Banner);
        Assert.Equal(3, pipeline.PendingCount);
        Assert.Equal(0, host.ScanStarts);
        Assert.Equal(
            ["Penumbra Organizer: 3 new mods are loose in the root. /porganizer sortnew sorts them."],
            host.Chats);
    }

    [Fact]
    public void PopupTurnedOffWhileShowing_HidesIt_AndTurnedBackOn_ShowsItAgain()
    {
        var (pipeline, host) = Make(NewModAction.Ask);
        pipeline.Offer(Ids(2), 2);
        Assert.NotNull(pipeline.Banner);

        host.ShowPopup = false;
        Assert.Null(pipeline.Banner);
        host.ShowPopup = true;
        Assert.Equal(2, pipeline.Banner!.Count);
    }

    [Fact]
    public void DoNothingChosenWhileShowing_HidesIt()
    {
        var (pipeline, host) = Make(NewModAction.Ask);
        pipeline.Offer(Ids(2), 2);
        host.Setting = NewModAction.DoNothing;

        Assert.Null(pipeline.Banner);
        Assert.Equal(2, pipeline.PendingCount);
    }

    [Fact]
    public void Ask_TwoBatches_CoalesceIntoOneBanner()
    {
        var (pipeline, _) = Make(NewModAction.Ask);
        pipeline.Offer(Ids(2, "a"), 2);
        pipeline.Offer(Ids(3, "b"), 3);

        Assert.Equal(5, pipeline.Banner!.Count);
        Assert.Equal(5, pipeline.PendingCount);
    }

    [Fact]
    public void ZeroEligible_NoBanner()
    {
        var (pipeline, host) = Make(NewModAction.Ask);
        pipeline.Offer(Ids(2), 0);
        pipeline.Update(T0);

        Assert.Null(pipeline.Banner);
        Assert.Equal(0, pipeline.PendingCount);
        Assert.Empty(host.Chats);
    }

    [Fact]
    public void DoNothing_NoBanner_ButSortNewStillSeesThem()
    {
        var (pipeline, host) = Make(NewModAction.DoNothing);
        pipeline.Offer(Ids(2), 2);
        pipeline.Update(T0);

        Assert.Null(pipeline.Banner);
        Assert.Equal(0, host.ScanStarts);
        Assert.Equal(2, pipeline.PendingCount);
        Assert.Empty(host.Chats);
    }

    [Fact]
    public void Automatic_Scans_ThenSortsRootOnlyWithTheBatch_ThenApplies()
    {
        var (pipeline, host) = Make(NewModAction.SortAutomatically);
        pipeline.Offer(Ids(3), 3);
        Assert.Null(pipeline.Banner);

        var now = T0;
        RunToApplied(pipeline, host, ref now);

        Assert.Equal(1, host.ScanStarts);
        var scope = Assert.Single(host.Sorts);
        Assert.True(scope.RootOnly);
        Assert.Equal(Ids(3).ToHashSet(), scope.Identifiers!.ToHashSet());
        Assert.Equal(1, host.ApplyStarts);
        Assert.Equal(
            ["Penumbra Organizer: moved 3 new mods. Undo from the History tab. In Penumbra, Settings > " +
             "Rediscover Mods removes any empty folders left behind."],
            host.Chats);
    }

    [Fact]
    public void Automatic_AtTheCap_RunsAutomatically()
    {
        var (pipeline, host) = Make(NewModAction.SortAutomatically);
        pipeline.Offer(Ids(20), 20);
        pipeline.Update(T0);

        Assert.Null(pipeline.Banner);
        Assert.Equal(1, host.ScanStarts);
    }

    [Fact]
    public void Automatic_With21Eligible_FallsBackToBanner()
    {
        var (pipeline, host) = Make(NewModAction.SortAutomatically);
        pipeline.Offer(Ids(21), 21);
        pipeline.Update(T0);

        Assert.Equal(21, pipeline.Banner!.Count);
        Assert.Equal(0, host.ScanStarts);
    }

    [Fact]
    public void Automatic_WithStagedProposals_FallsBackToBannerWithWarning()
    {
        var (pipeline, host) = Make(NewModAction.SortAutomatically);
        host.HasStagedProposals = true;
        pipeline.Offer(Ids(2), 2);
        pipeline.Update(T0);

        Assert.Equal(0, host.ScanStarts);
        Assert.Equal(
            ["Sorting rescans your library first: your unapplied sort will be discarded."],
            pipeline.Banner!.Warnings);
    }

    [Fact]
    public void Automatic_WithSessionOverrides_FallsBackToBannerWithWarning()
    {
        var (pipeline, host) = Make(NewModAction.SortAutomatically);
        host.HasSessionOverrides = true;
        pipeline.Offer(Ids(2), 2);
        pipeline.Update(T0);

        Assert.Equal(0, host.ScanStarts);
        Assert.Equal(
            ["This session's unprotect choices for Heliosphere/Proteus mods will be reset."],
            pipeline.Banner!.Warnings);
    }

    // ---- Admission ----

    [Fact]
    public void Gated_WaitsThenProceeds()
    {
        var (pipeline, host) = Make(NewModAction.SortAutomatically);
        host.ExternalRejection = "An operation is running.";
        pipeline.Offer(Ids(2), 2);

        pipeline.Update(T0);
        pipeline.Update(T0.AddSeconds(30));
        Assert.Equal(NewModPipelineStage.Admitting, pipeline.Stage);
        Assert.Equal(0, host.ScanStarts);

        host.ExternalRejection = null;
        pipeline.Update(T0.AddSeconds(31));
        Assert.Equal(NewModPipelineStage.Scanning, pipeline.Stage);
        Assert.Equal(1, host.ScanStarts);
        Assert.Empty(host.Chats);
    }

    [Fact]
    public void GateNeverClears_TimesOutAfterTwoMinutes_AndKeepsThemPending()
    {
        var (pipeline, host) = Make(NewModAction.SortAutomatically);
        host.ExternalRejection = "Folder Cleanup is running.";
        pipeline.Offer(Ids(2), 2);

        pipeline.Update(T0);
        pipeline.Update(T0.AddSeconds(119));
        Assert.Equal(NewModPipelineStage.Admitting, pipeline.Stage);
        Assert.Empty(host.Chats);

        pipeline.Update(T0.AddMinutes(2));
        Assert.Equal(NewModPipelineStage.Idle, pipeline.Stage);
        Assert.Equal(NewModRunOutcome.Failed, pipeline.LastResult!.Outcome);
        Assert.Equal(
            ["Penumbra Organizer: gave up waiting to sort the new mods after 2 minutes: Folder Cleanup is running. " +
             "They stay pending for /porganizer sortnew."],
            host.Chats);
        Assert.Equal(0, host.ScanStarts);
        Assert.Equal(2, pipeline.PendingCount);
    }

    // ---- Scan ----

    [Fact]
    public void StaleModList_ReAdmitsAndScansAgain()
    {
        var (pipeline, host) = Make(NewModAction.SortAutomatically);
        pipeline.Offer(Ids(2), 2);
        pipeline.Update(T0);
        host.FinishScan(LibraryWorkOutcome.StaleModList);

        pipeline.Update(T0.AddSeconds(1));
        Assert.Empty(host.Sorts);
        pipeline.Update(T0.AddSeconds(2));
        Assert.Equal(NewModPipelineStage.Scanning, pipeline.Stage);
        Assert.Equal(2, host.ScanStarts);

        host.FinishScan();
        pipeline.Update(T0.AddSeconds(3));
        Assert.Single(host.Sorts);
        Assert.Equal(NewModPipelineStage.Applying, pipeline.Stage);
    }

    [Fact]
    public void ScanFailed_Fails_WithTheScanError()
    {
        var (pipeline, host) = Make(NewModAction.SortAutomatically);
        pipeline.Offer(Ids(2), 2);
        pipeline.Update(T0);
        host.FinishScan(LibraryWorkOutcome.Failed, "Penumbra is not available.");
        pipeline.Update(T0.AddSeconds(1));

        Assert.Equal(NewModPipelineStage.Idle, pipeline.Stage);
        Assert.Equal(NewModRunOutcome.Failed, pipeline.LastResult!.Outcome);
        Assert.Empty(host.Sorts);
        Assert.Equal(0, host.ApplyStarts);
        Assert.Equal(
            ["Penumbra Organizer: could not sort the new mods. Penumbra is not available. " +
             "They stay pending for /porganizer sortnew."],
            host.Chats);
        Assert.Equal(2, pipeline.PendingCount);
    }

    [Fact]
    public void ScanStillRunning_Waits()
    {
        var (pipeline, host) = Make(NewModAction.SortAutomatically);
        pipeline.Offer(Ids(2), 2);
        pipeline.Update(T0);
        pipeline.Update(T0.AddMinutes(5));

        Assert.Equal(NewModPipelineStage.Scanning, pipeline.Stage);
        Assert.Empty(host.Sorts);
    }

    // ---- Sort ----

    [Fact]
    public void ZeroChanged_IsDone_WithNoApply()
    {
        var (pipeline, host) = Make(NewModAction.SortAutomatically);
        host.SortResult = _ => new ScopedSortResult(2, 0);
        pipeline.Offer(Ids(2), 2);
        pipeline.Update(T0);
        host.FinishScan();
        pipeline.Update(T0.AddSeconds(1));

        Assert.Equal(NewModPipelineStage.Idle, pipeline.Stage);
        Assert.Equal(NewModRunOutcome.Done, pipeline.LastResult!.Outcome);
        Assert.Equal(0, host.ApplyStarts);
        Assert.Equal(
            ["Penumbra Organizer: nothing to move. The new mods are already where a sort would put them."],
            host.Chats);
    }

    [Fact]
    public void MissingIds_AreCounted()
    {
        var (pipeline, host) = Make(NewModAction.SortAutomatically);
        host.SortResult = _ => new ScopedSortResult(InScope: 2, Changed: 2);
        pipeline.Offer(Ids(3), 3);
        pipeline.Update(T0);
        host.FinishScan();
        pipeline.Update(T0.AddSeconds(1));
        host.FinishApply(moved: 2);
        pipeline.Update(T0.AddSeconds(2));

        var line = Assert.Single(host.Chats);
        Assert.StartsWith("Penumbra Organizer: moved 2 new mods.", line);
        Assert.Contains("1 new mod was not sorted: it was removed, moved into a folder, or protected.", line);
    }

    // ---- Banner choices ----

    [Fact]
    public void SortNow_StagesThenWaitsForConfirmation_WithThePreview()
    {
        var (pipeline, host) = Make(NewModAction.Ask);
        pipeline.Offer(Ids(2), 2);
        pipeline.UserChose(NewModChoice.SortNow);

        pipeline.Update(T0);
        host.FinishScan();
        pipeline.Update(T0.AddSeconds(1));
        pipeline.Update(T0.AddSeconds(2));
        pipeline.Update(T0.AddMinutes(10)); // no timeout while the user decides

        Assert.Equal(NewModPipelineStage.Confirming, pipeline.Stage);
        Assert.Equal(0, host.ApplyStarts);
        Assert.Null(pipeline.Banner);
        var preview = pipeline.Preview!;
        Assert.Equal("Move 2 new mods?", preview.Title);
        Assert.Equal("Will sort 2 of 50 mods: loose in root.", preview.ScopeText);
        Assert.Equal(
            [new NewModPreviewRow("mod1", "(root)", "Sorted"), new NewModPreviewRow("mod2", "(root)", "Sorted")],
            preview.Rows);
        Assert.Empty(host.Chats);
    }

    [Fact]
    public void Confirm_Apply_StartsTheApplyOnTheNextUpdate_NotInTheCall()
    {
        var (pipeline, host) = Make(NewModAction.Ask);
        pipeline.Offer(Ids(2), 2);
        pipeline.UserChose(NewModChoice.SortNow);
        var now = T0;
        RunToConfirming(pipeline, host, ref now);

        pipeline.Confirm(NewModConfirmChoice.Apply);
        Assert.Equal(0, host.ApplyStarts);
        Assert.Null(pipeline.Preview);

        pipeline.Update(now.AddSeconds(1));
        Assert.Equal(1, host.ApplyStarts);
        Assert.Equal(NewModPipelineStage.Applying, pipeline.Stage);
    }

    [Fact]
    public void Confirm_Cancel_DiscardsTheStagedSort_AndReturnsThemToPending()
    {
        var (pipeline, host) = Make(NewModAction.Ask);
        pipeline.Offer(Ids(2), 2);
        pipeline.UserChose(NewModChoice.SortNow);
        var now = T0;
        RunToConfirming(pipeline, host, ref now);

        pipeline.Confirm(NewModConfirmChoice.Cancel);
        pipeline.Update(now.AddSeconds(1));

        Assert.Equal(NewModPipelineStage.Idle, pipeline.Stage);
        Assert.Equal(NewModRunOutcome.Cancelled, pipeline.LastResult!.Outcome);
        Assert.Equal(1, host.Discards);
        Assert.False(host.HasStagedProposals);
        Assert.Equal(0, host.ApplyStarts);
        Assert.Equal(2, pipeline.PendingCount);
        Assert.Null(pipeline.Banner); // answered: no popup until the next batch
        Assert.Empty(host.Chats);
    }

    [Fact]
    public void Confirm_ReviewFirst_OpensReview_KeepsTheStagedSort()
    {
        var (pipeline, host) = Make(NewModAction.Ask);
        pipeline.Offer(Ids(2), 2);
        pipeline.UserChose(NewModChoice.SortNow);
        var now = T0;
        RunToConfirming(pipeline, host, ref now);

        pipeline.Confirm(NewModConfirmChoice.ReviewFirst);
        pipeline.Update(now.AddSeconds(1));

        Assert.Equal(1, host.ReviewOpens);
        Assert.Equal(0, host.Discards);
        Assert.True(host.HasStagedProposals);
        Assert.Equal(0, host.ApplyStarts);
        Assert.Equal(NewModRunOutcome.Done, pipeline.LastResult!.Outcome);
        Assert.Equal(NewModPipelineStage.Idle, pipeline.Stage);
    }

    [Fact]
    public void Confirm_OutsideConfirming_IsIgnored()
    {
        var (pipeline, host) = Make(NewModAction.Ask);
        pipeline.Confirm(NewModConfirmChoice.Apply);
        pipeline.Offer(Ids(2), 2);
        pipeline.UserChose(NewModChoice.SortNow);
        pipeline.Confirm(NewModConfirmChoice.Cancel); // still admitting
        pipeline.Update(T0);
        host.FinishScan();
        pipeline.Update(T0.AddSeconds(1));

        Assert.Equal(NewModPipelineStage.Confirming, pipeline.Stage);
        Assert.Equal(0, host.Discards);
    }

    [Fact]
    public void Confirming_RescanFromElsewhere_EndsTheRun_AndKeepsThemPending()
    {
        var (pipeline, host) = Make(NewModAction.Ask);
        pipeline.Offer(Ids(2), 2);
        pipeline.UserChose(NewModChoice.SortNow);
        var now = T0;
        RunToConfirming(pipeline, host, ref now);

        host.ExternalScanPublished();
        pipeline.Update(now.AddSeconds(1));

        Assert.Equal(NewModPipelineStage.Idle, pipeline.Stage);
        Assert.Null(pipeline.Preview);
        Assert.Equal(NewModRunOutcome.Failed, pipeline.LastResult!.Outcome);
        Assert.Equal(
            ["Penumbra Organizer: could not sort the new mods. The library changed before the sort was confirmed, " +
             "so nothing was applied. They stay pending for /porganizer sortnew."],
            host.Chats);
        Assert.Equal(2, pipeline.PendingCount);
        Assert.Equal(0, host.ApplyStarts);
    }

    [Fact]
    public void Confirming_ApplyFromTheReviewTab_EndsTheRun()
    {
        var (pipeline, host) = Make(NewModAction.Ask);
        pipeline.Offer(Ids(2), 2);
        pipeline.UserChose(NewModChoice.SortNow);
        var now = T0;
        RunToConfirming(pipeline, host, ref now);

        host.ExternalApplyCompleted();
        pipeline.Update(now.AddSeconds(1));

        Assert.Equal(NewModPipelineStage.Idle, pipeline.Stage);
        Assert.Equal(0, host.ApplyStarts);
        Assert.Contains("nothing was applied", Assert.Single(host.Chats));
    }

    [Fact]
    public void Confirming_BatchArriving_IsQueued_NotMerged()
    {
        var (pipeline, host) = Make(NewModAction.Ask);
        pipeline.Offer(["a1"], 1);
        pipeline.UserChose(NewModChoice.SortNow);
        var now = T0;
        RunToConfirming(pipeline, host, ref now);

        pipeline.Offer(["b1"], 1);
        Assert.Equal(1, pipeline.QueuedBatches);
        Assert.Equal(["a1"], pipeline.Preview!.Rows.Select(r => r.Name));
        Assert.Null(pipeline.Banner);
    }

    [Fact]
    public void ProgressText_ShownWhileAnInteractiveRunPrepares_NotForAutomatic()
    {
        var (pipeline, host) = Make(NewModAction.Ask);
        Assert.Null(pipeline.ProgressText);
        pipeline.Offer(Ids(2), 2);
        pipeline.UserChose(NewModChoice.SortNow);
        Assert.Equal("Scanning your library before sorting...", pipeline.ProgressText);
        pipeline.Update(T0);
        Assert.Equal("Scanning your library before sorting...", pipeline.ProgressText);
        host.FinishScan();
        pipeline.Update(T0.AddSeconds(1));
        Assert.Null(pipeline.ProgressText); // the confirm window takes over

        var (auto, _) = Make(NewModAction.SortAutomatically);
        auto.Offer(Ids(2), 2);
        auto.Update(T0);
        Assert.Null(auto.ProgressText);
    }

    [Fact]
    public void SortNow_ConfirmedApply_StartsApplyOnce_ThenDoneOnTheNextCompletion()
    {
        var (pipeline, host) = Make(NewModAction.Ask);
        pipeline.Offer(Ids(3), 3);
        pipeline.UserChose(NewModChoice.SortNow);
        Assert.Null(pipeline.Banner);

        pipeline.Update(T0);
        host.FinishScan();
        pipeline.Update(T0.AddSeconds(1));
        Assert.Equal(0, host.ApplyStarts);
        pipeline.Confirm(NewModConfirmChoice.Apply);
        pipeline.Update(T0.AddSeconds(1));
        Assert.Equal(1, host.ApplyStarts);

        // Frames while the Apply runs start nothing more.
        pipeline.Update(T0.AddSeconds(2));
        pipeline.Update(T0.AddSeconds(3));
        Assert.Equal(1, host.ApplyStarts);
        Assert.Equal(NewModPipelineStage.Applying, pipeline.Stage);
        Assert.Null(pipeline.LastResult);

        host.FinishApply(moved: 3);
        pipeline.Update(T0.AddSeconds(4));
        Assert.Equal(NewModRunOutcome.Done, pipeline.LastResult!.Outcome);
        Assert.Equal(1, host.ApplyStarts);
        Assert.Equal(1, host.ScanStarts);
        Assert.StartsWith("Penumbra Organizer: moved 3 new mods.", Assert.Single(host.Chats));
        Assert.Equal(0, pipeline.PendingCount);
    }

    [Fact]
    public void SortNow_UsesEveryPendingMod()
    {
        var (pipeline, host) = Make(NewModAction.Ask);
        pipeline.Offer(["a"], 1);
        pipeline.Offer(["b", "c"], 2);
        pipeline.UserChose(NewModChoice.SortNow);
        pipeline.Update(T0);
        host.FinishScan();
        pipeline.Update(T0.AddSeconds(1));

        Assert.Equal(new HashSet<string> { "a", "b", "c" }, host.Sorts.Single().Identifiers!.ToHashSet());
    }

    [Fact]
    public void ApplyRejection_Fails_WithTheEnginesReason()
    {
        var (pipeline, host) = Make(NewModAction.Ask);
        host.ApplyRejection = "Cannot Apply while Validate() reports issues.";
        pipeline.Offer(Ids(2), 2);
        pipeline.UserChose(NewModChoice.SortNow);
        pipeline.Update(T0);
        host.FinishScan();
        pipeline.Update(T0.AddSeconds(1));
        pipeline.Confirm(NewModConfirmChoice.Apply);
        pipeline.Update(T0.AddSeconds(2));

        Assert.Equal(NewModPipelineStage.Idle, pipeline.Stage);
        Assert.Equal(NewModRunOutcome.Failed, pipeline.LastResult!.Outcome);
        Assert.Contains("Cannot Apply while Validate() reports issues.", pipeline.LastResult.Message);
        Assert.Equal(
            ["Penumbra Organizer: could not sort the new mods. Cannot Apply while Validate() reports issues. " +
             "They stay pending for /porganizer sortnew."],
            host.Chats);
        Assert.Equal(0, host.ApplyStarts);
    }

    [Fact]
    public void ApplyFinishedBadly_Fails_WithTheOperationError()
    {
        var (pipeline, host) = Make(NewModAction.SortAutomatically);
        pipeline.Offer(Ids(2), 2);
        pipeline.Update(T0);
        host.FinishScan();
        pipeline.Update(T0.AddSeconds(1));
        host.FinishApply(OperationStage.FailedBeforeMutation, error: "Penumbra refused the move.");
        pipeline.Update(T0.AddSeconds(2));

        Assert.Equal(NewModRunOutcome.Failed, pipeline.LastResult!.Outcome);
        Assert.Contains("Penumbra refused the move.", Assert.Single(host.Chats));
    }

    [Fact]
    public void ReviewFirst_OpensReview_WithNoApply()
    {
        var (pipeline, host) = Make(NewModAction.Ask);
        pipeline.Offer(Ids(2), 2);
        pipeline.UserChose(NewModChoice.ReviewFirst);
        pipeline.Update(T0);
        host.FinishScan();
        pipeline.Update(T0.AddSeconds(1));

        Assert.Equal(1, host.ReviewOpens);
        Assert.Equal(0, host.ApplyStarts);
        Assert.Equal(NewModPipelineStage.Idle, pipeline.Stage);
        Assert.Equal(NewModRunOutcome.Done, pipeline.LastResult!.Outcome);
        Assert.Single(host.Sorts);
        Assert.Equal(0, pipeline.PendingCount);
    }

    [Fact]
    public void NotNow_HidesBanner_AndKeepsThemPending()
    {
        var (pipeline, host) = Make(NewModAction.Ask);
        pipeline.Offer(Ids(2), 2);
        pipeline.UserChose(NewModChoice.NotNow);
        pipeline.Update(T0);

        Assert.Null(pipeline.Banner);
        Assert.Equal(2, pipeline.PendingCount);
        Assert.Equal(0, host.ScanStarts);
    }

    [Fact]
    public void BannerWarnings_FollowTheCurrentState()
    {
        var (pipeline, host) = Make(NewModAction.Ask);
        pipeline.Offer(Ids(2), 2);
        Assert.Empty(pipeline.Banner!.Warnings);

        host.HasStagedProposals = true;
        host.HasSessionOverrides = true;
        Assert.Equal(2, pipeline.Banner!.Warnings.Count);
    }

    // ---- Batches arriving mid-run ----

    [Fact]
    public void BatchMidRun_IsQueued_NotMerged_NotLost()
    {
        var (pipeline, host) = Make(NewModAction.SortAutomatically);
        pipeline.Offer(["a1", "a2"], 2);
        pipeline.Update(T0);
        Assert.Equal(NewModPipelineStage.Scanning, pipeline.Stage);

        pipeline.Offer(["b1"], 1);
        Assert.Equal(1, pipeline.QueuedBatches);
        Assert.Null(pipeline.Banner);

        host.FinishScan();
        pipeline.Update(T0.AddSeconds(1));
        Assert.Equal(new HashSet<string> { "a1", "a2" }, host.Sorts[0].Identifiers!.ToHashSet());

        host.FinishApply();
        pipeline.Update(T0.AddSeconds(2));
        Assert.Equal(NewModRunOutcome.Done, pipeline.LastResult!.Outcome);

        pipeline.Update(T0.AddSeconds(3));
        Assert.Equal(0, pipeline.QueuedBatches);
        Assert.Equal(NewModPipelineStage.Scanning, pipeline.Stage);
        host.FinishScan();
        pipeline.Update(T0.AddSeconds(4));
        Assert.Equal(new HashSet<string> { "b1" }, host.Sorts[1].Identifiers!.ToHashSet());
    }

    [Fact]
    public void BatchMidRun_InAskMode_BecomesABannerAfterTheRun()
    {
        var (pipeline, host) = Make(NewModAction.Ask);
        pipeline.Offer(["a1"], 1);
        pipeline.UserChose(NewModChoice.SortNow);
        pipeline.Update(T0);
        pipeline.Offer(["b1", "b2"], 2);
        Assert.Null(pipeline.Banner);

        host.FinishScan();
        pipeline.Update(T0.AddSeconds(1));
        Assert.Equal(new HashSet<string> { "a1" }, host.Sorts.Single().Identifiers!.ToHashSet());
        pipeline.Confirm(NewModConfirmChoice.Apply);
        pipeline.Update(T0.AddSeconds(1));
        host.FinishApply();
        pipeline.Update(T0.AddSeconds(2));
        pipeline.Update(T0.AddSeconds(3));

        Assert.Equal(2, pipeline.Banner!.Count);
    }

    [Fact]
    public void TwoAutomaticBatchesInARow_WithThePopupOff_BothApply()
    {
        var (pipeline, host) = Make(NewModAction.SortAutomatically);
        host.ShowPopup = false;
        host.PostApplyRescan = true;
        var now = T0;

        pipeline.Offer(["a1", "a2"], 2);
        pipeline.Update(now = now.AddSeconds(1));
        host.FinishScan();
        pipeline.Update(now = now.AddSeconds(1));
        Assert.Equal(1, host.ApplyStarts);

        pipeline.Offer(["b1"], 1);
        host.FinishApply(); // starts the post-Apply rescan, as CompletionWatcher does
        pipeline.Update(now = now.AddSeconds(1));
        Assert.Equal(NewModRunOutcome.Done, pipeline.LastResult!.Outcome);

        // The second batch waits for the post-Apply rescan, then runs its own.
        pipeline.Update(now = now.AddSeconds(1));
        Assert.Equal(NewModPipelineStage.Admitting, pipeline.Stage);
        host.FinishScan();
        pipeline.Update(now = now.AddSeconds(1));
        Assert.Equal(NewModPipelineStage.Scanning, pipeline.Stage);
        host.FinishScan();
        pipeline.Update(now = now.AddSeconds(1));
        Assert.Equal(2, host.ApplyStarts);
        host.FinishApply();
        pipeline.Update(now = now.AddSeconds(1));

        Assert.Equal(NewModRunOutcome.Done, pipeline.LastResult!.Outcome);
        Assert.Equal(2, host.Chats.Count(c => c.StartsWith("Penumbra Organizer: moved ", StringComparison.Ordinal)));
        Assert.Equal(new HashSet<string> { "b1" }, host.Sorts[1].Identifiers!.ToHashSet());
    }

    // ---- Commands ----

    [Fact]
    public void SortNew_NothingPending_ChatsTheHint()
    {
        var (pipeline, host) = Make();
        pipeline.RunCommand(CommandKind.SortNew, force: false);
        pipeline.Update(T0);

        Assert.Equal(
            ["No new mods since the plugin loaded. /porganizer sortroot sorts every loose mod in the root."],
            host.Chats);
        Assert.Equal(0, host.ScanStarts);
    }

    [Fact]
    public void SortNew_StagedWithoutForce_ChatsTheWarning_AndStartsNothing()
    {
        var (pipeline, host) = Make();
        pipeline.Offer(Ids(2), 2);
        host.HasStagedProposals = true;
        pipeline.RunCommand(CommandKind.SortNew, force: false);
        pipeline.Update(T0);

        Assert.Equal(
            ["You have an unapplied sort (or unprotect choices) that a rescan would discard. Apply or clear it " +
             "on the Review tab, or run /porganizer sortnew force."],
            host.Chats);
        Assert.Equal(0, host.ScanStarts);
        Assert.Equal(2, pipeline.PendingCount);
    }

    [Fact]
    public void SortNew_OverridesWithoutForce_ChatsTheWarning()
    {
        var (pipeline, host) = Make();
        pipeline.Offer(Ids(2), 2);
        host.HasSessionOverrides = true;
        pipeline.RunCommand(CommandKind.SortNew, force: false);

        Assert.StartsWith("You have an unapplied sort", Assert.Single(host.Chats));
        Assert.Equal(0, host.ScanStarts);
    }

    [Fact]
    public void SortNew_StagedWithForce_Stages_Confirms_AndApplies()
    {
        var (pipeline, host) = Make();
        pipeline.Offer(Ids(2), 2);
        host.HasStagedProposals = true;
        pipeline.RunCommand(CommandKind.SortNew, force: true);
        Assert.Null(pipeline.Banner);

        var now = T0;
        RunToConfirmedApplied(pipeline, host, ref now);
        Assert.Equal(1, host.ApplyStarts);
        Assert.Equal(2, host.Chats.Count);
        Assert.Equal(
            "Penumbra Organizer: 2 new mods are ready to move. Check the list in the window that opened, then Apply.",
            host.Chats[0]);
        Assert.StartsWith("Penumbra Organizer: moved 2 new mods.", host.Chats[1]);
    }

    [Fact]
    public void SortNew_DoesNotApplyWithoutConfirmation()
    {
        var (pipeline, host) = Make();
        pipeline.Offer(Ids(2), 2);
        pipeline.RunCommand(CommandKind.SortNew, force: false);
        var now = T0;
        RunToConfirming(pipeline, host, ref now);
        pipeline.Update(now.AddMinutes(5));

        Assert.Equal(0, host.ApplyStarts);
        Assert.Equal("Move 2 new mods?", pipeline.Preview!.Title);
    }

    [Fact]
    public void SortNew_Cancel_ReturnsThemToPending_SoSortNewCanRunAgain()
    {
        var (pipeline, host) = Make();
        pipeline.Offer(Ids(2), 2);
        pipeline.RunCommand(CommandKind.SortNew, force: false);
        var now = T0;
        RunToConfirming(pipeline, host, ref now);
        pipeline.Confirm(NewModConfirmChoice.Cancel);
        pipeline.Update(now = now.AddSeconds(1));
        Assert.Equal(2, pipeline.PendingCount);
        Assert.Equal(1, host.Discards);

        pipeline.RunCommand(CommandKind.SortNew, force: false);
        RunToConfirming(pipeline, host, ref now);
        Assert.Equal(2, pipeline.Preview!.Count);
    }

    [Fact]
    public void SortNew_SkipsTheCap()
    {
        var (pipeline, host) = Make(NewModAction.SortAutomatically);
        pipeline.Offer(Ids(25), 25);
        Assert.NotNull(pipeline.Banner);
        pipeline.RunCommand(CommandKind.SortNew, force: false);
        pipeline.Update(T0);

        Assert.Equal(1, host.ScanStarts);
    }

    [Fact]
    public void SortNew_AddPendingThenRun_IncludesUndebouncedMods()
    {
        var (pipeline, host) = Make(NewModAction.SortAutomatically);
        pipeline.AddPending(["x"], 1);
        Assert.Null(pipeline.Banner);
        Assert.Equal(0, host.ScanStarts);

        pipeline.RunCommand(CommandKind.SortNew, force: false);
        pipeline.Update(T0);
        host.FinishScan();
        pipeline.Update(T0.AddSeconds(1));
        Assert.Equal(new HashSet<string> { "x" }, host.Sorts.Single().Identifiers!.ToHashSet());
    }

    [Fact]
    public void SortNew_Busy_ChatsTheEngineReason_AndKeepsThemPending()
    {
        var (pipeline, host) = Make();
        pipeline.Offer(Ids(2), 2);
        host.ExternalRejection = "An operation is running.";
        pipeline.RunCommand(CommandKind.SortNew, force: false);
        pipeline.Update(T0);

        Assert.Equal(["An operation is running. Try again when it finishes."], host.Chats);
        Assert.Equal(0, host.ScanStarts);
        Assert.Equal(2, pipeline.PendingCount);
    }

    [Fact]
    public void SortNew_WhilePipelineRuns_ChatsBusy()
    {
        var (pipeline, host) = Make(NewModAction.SortAutomatically);
        pipeline.Offer(Ids(2), 2);
        pipeline.Update(T0);
        pipeline.AddPending(["late"], 1);
        pipeline.RunCommand(CommandKind.SortNew, force: false);

        Assert.Equal(
            ["Penumbra Organizer is already sorting new mods. Try again when it finishes."],
            host.Chats);
        Assert.Equal(1, host.ScanStarts);
    }

    [Fact]
    public void SortRoot_SortsEveryLooseMod_EvenWithNothingPending()
    {
        var (pipeline, host) = Make();
        pipeline.RunCommand(CommandKind.SortRoot, force: false);
        pipeline.Update(T0);
        host.FinishScan();
        pipeline.Update(T0.AddSeconds(1));

        var scope = host.Sorts.Single();
        Assert.True(scope.RootOnly);
        Assert.Null(scope.Identifiers);
        Assert.Equal(NewModPipelineStage.Confirming, pipeline.Stage);
        Assert.Equal("Move 7 loose mods?", pipeline.Preview!.Title);
        pipeline.Confirm(NewModConfirmChoice.Apply);
        pipeline.Update(T0.AddSeconds(2));
        host.FinishApply(moved: 7);
        pipeline.Update(T0.AddSeconds(3));
        Assert.Equal(
            "Penumbra Organizer: 7 loose mods are ready to move. Check the list in the window that opened, then Apply.",
            host.Chats[0]);
        Assert.StartsWith("Penumbra Organizer: moved 7 loose mods.", host.Chats[1]);
        Assert.Equal(2, host.Chats.Count);
    }

    [Fact]
    public void SortRoot_Cancel_DiscardsTheStagedSort_AndLeavesPendingAlone()
    {
        var (pipeline, host) = Make();
        pipeline.RunCommand(CommandKind.SortRoot, force: false);
        var now = T0;
        RunToConfirming(pipeline, host, ref now);
        pipeline.Confirm(NewModConfirmChoice.Cancel);
        pipeline.Update(now.AddSeconds(1));

        Assert.Equal(NewModRunOutcome.Cancelled, pipeline.LastResult!.Outcome);
        Assert.Equal(1, host.Discards);
        Assert.Equal(0, pipeline.PendingCount);
    }

    [Fact]
    public void SortRoot_StagedWithoutForce_NamesSortRootForce()
    {
        var (pipeline, host) = Make();
        host.HasStagedProposals = true;
        pipeline.RunCommand(CommandKind.SortRoot, force: false);

        Assert.EndsWith("/porganizer sortroot force.", Assert.Single(host.Chats));
        Assert.Equal(0, host.ScanStarts);
    }

    [Fact]
    public void SortRoot_DoesNotConsumePendingNewMods()
    {
        var (pipeline, host) = Make();
        pipeline.Offer(Ids(2), 2);
        pipeline.RunCommand(CommandKind.SortRoot, force: false);
        var now = T0;
        RunToConfirmedApplied(pipeline, host, ref now);

        Assert.Equal(2, pipeline.PendingCount);
    }

    [Fact]
    public void OtherCommandKinds_DoNothing()
    {
        var (pipeline, host) = Make();
        pipeline.RunCommand(CommandKind.Help, false);
        pipeline.RunCommand(CommandKind.Toggle, false);
        pipeline.RunCommand(CommandKind.Unknown, false);
        pipeline.Update(T0);

        Assert.Empty(host.Chats);
        Assert.Equal(0, host.ScanStarts);
    }
}
