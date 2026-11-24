using PenumbraOrganizer.Plugin.LibraryWork;
using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Collections;
using PenumbraOrganizer.Plugin.Organizer.NewMods;
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.NewMods;

/// <summary>
/// New Heliosphere/Proteus mods that the Protect tab's mode keeps in place: the user is told, instead of
/// the batch disappearing into a log line. Everything follows the Protect tab dropdowns.
/// </summary>
public class NewModToolNoticeTests
{
    private static NewModCandidate C(string id, string path, string? author = null, bool hs = false) =>
        new(id, id, author ?? "someone", path, hs);

    private static readonly ToolPolicy HsNormal = new(ToolMode.Normal, ToolMode.Protected, false);
    private static readonly ToolPolicy HsInside = new(ToolMode.SortInside, ToolMode.Protected, false);
    private static readonly ToolPolicy ProteusNormal = new(ToolMode.Protected, ToolMode.Normal, false);

    // ---- Which new mods a tool mode holds back ----

    [Fact]
    public void ToolHeld_CountsRootHeliosphereAndProteusMods_InProtectedMode()
    {
        var held = NewModEligibility.ToolHeld(
            [C("hs1", "hs1", hs: true), C("hs2", "hs2", hs: true), C("p1", "p1", author: "Proteus"), C("plain", "plain")],
            ToolPolicy.Default);

        Assert.Equal(new ToolHeldCounts(2, 1), held);
        Assert.Equal(3, held.Total);
    }

    [Fact]
    public void ToolHeld_FollowsTheModes_NothingHeldWhenNotProtected()
    {
        var mods = new[] { C("hs1", "hs1", hs: true), C("p1", "p1", author: "Proteus") };

        Assert.Equal(new ToolHeldCounts(0, 1), NewModEligibility.ToolHeld(mods, HsNormal));
        Assert.Equal(new ToolHeldCounts(0, 1), NewModEligibility.ToolHeld(mods, HsInside));
        Assert.Equal(new ToolHeldCounts(1, 0), NewModEligibility.ToolHeld(mods, ProteusNormal));
    }

    [Fact]
    public void ToolHeld_IgnoresModsInAFolder_AndTheProteusManagedMod()
    {
        var held = NewModEligibility.ToolHeld(
            [C("hs1", "Heliosphere/hs1", hs: true), C("Proteus", "Proteus", author: "Proteus")],
            ToolPolicy.Default);

        Assert.Equal(new ToolHeldCounts(0, 0), held);
    }

    [Fact]
    public void ToolHeld_And_Filter_Agree_HeldModsAreNeverEligible()
    {
        var mods = new[] { C("hs1", "hs1", hs: true), C("p1", "p1", author: "Proteus"), C("plain", "plain") };
        var eligible = NewModEligibility.Filter(mods, new HashSet<string>(), new Dictionary<string, bool>(), ToolPolicy.Default);

        Assert.Equal(["plain"], eligible.Select(r => r.Identifier));
        Assert.Equal(2, NewModEligibility.ToolHeld(mods, ToolPolicy.Default).Total);
    }

    // ---- Texts ----

    [Fact]
    public void Notice_OneHeliosphereMod()
    {
        Assert.Equal(
            "1 new Heliosphere mod added. Heliosphere mods are Protected, so it stays where it is. "
            + "You can change this on the Protect tab.",
            NewModPromptText.ToolNotice(new ToolHeldCounts(1, 0)).Message);
    }

    [Fact]
    public void Notice_SeveralProteusMods()
    {
        Assert.Equal(
            "3 new Proteus mods added. Proteus mods are Protected, so they stay where they are. "
            + "You can change this on the Protect tab.",
            NewModPromptText.ToolNotice(new ToolHeldCounts(0, 3)).Message);
    }

    [Fact]
    public void Notice_BothTools()
    {
        var notice = NewModPromptText.ToolNotice(new ToolHeldCounts(2, 1));
        Assert.Equal(
            "2 new Heliosphere mods added. Heliosphere mods are Protected, so they stay where they are. "
            + "1 new Proteus mod added. Proteus mods are Protected, so it stays where it is. "
            + "You can change this on the Protect tab.",
            notice.Message);
        Assert.Equal(new ToolHeldCounts(2, 1), notice.Counts);
    }

    [Fact]
    public void BannerToolLine_Singular_Plural_AndBoth()
    {
        Assert.Null(NewModPromptText.BannerToolLine(new ToolHeldCounts(0, 0)));
        Assert.Equal(
            "Also added: 1 Heliosphere mod. It is Protected, so it stays where it is. You can change this on the Protect tab.",
            NewModPromptText.BannerToolLine(new ToolHeldCounts(1, 0)));
        Assert.Equal(
            "Also added: 4 Proteus mods. They are Protected, so they stay where they are. You can change this on the Protect tab.",
            NewModPromptText.BannerToolLine(new ToolHeldCounts(0, 4)));
        Assert.Equal(
            "Also added: 2 Heliosphere mods and 1 Proteus mod. They are Protected, so they stay where they are. "
            + "You can change this on the Protect tab.",
            NewModPromptText.BannerToolLine(new ToolHeldCounts(2, 1)));
    }

    [Fact]
    public void Texts_AreAscii()
    {
        var texts = new[]
        {
            NewModPromptText.ToolNotice(new ToolHeldCounts(2, 3)).Message,
            NewModPromptText.BannerToolLine(new ToolHeldCounts(2, 3))!,
            NewModPromptText.OpenProtectTabLabel, NewModPromptText.ToolNoticeOkLabel, NewModPromptText.ToolNoticeTitle,
        };
        Assert.All(texts, t => Assert.All(t, ch => Assert.True(ch < 128)));
    }

    // ---- Pipeline ----

    private sealed class Host : INewModSortHost
    {
        public NewModAction Setting { get; set; } = NewModAction.Ask;
        public bool ShowPopup { get; set; } = true;
        public bool HasStagedProposals { get; set; }
        public bool HasSessionOverrides { get; set; }
        public int ScanStarts { get; private set; }
        public List<string> Chats { get; } = [];
        public string? AdmissionRejection() => ScanState.IsRunning ? "Scan is already running." : null;

        public string? StartScan()
        {
            ScanStarts++;
            ScanState = LibraryWorkStateSnapshot.Idle with { Phase = LibraryWorkPhase.Materializing, JobDisplayName = "Scan" };
            return null;
        }

        public int ScanGeneration { get; private set; }
        public LibraryWorkStateSnapshot ScanState { get; private set; } = LibraryWorkStateSnapshot.Idle;

        public void FinishScan()
        {
            ScanGeneration++;
            ScanState = LibraryWorkStateSnapshot.Idle with { LastOutcome = LibraryWorkOutcome.Completed };
        }

        public ScopedSortResult SortScoped(SortScope scope) => new(scope.Identifiers?.Count ?? 0, 0);
        public IReadOnlyList<StagedMove> StagedMoves() => [];
        public void DiscardStaged() { }
        public string? StartApply() => null;
        public long OperationCompletionGeneration => 0;
        public OperationStateSnapshot OperationState => OperationStateSnapshot.Idle;
        public void OpenReview() { }
        public void Chat(string message) => Chats.Add(message);
    }

    private static readonly DateTimeOffset T0 = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    private static (NewModSortPipeline Pipeline, Host Host) Make(NewModAction setting = NewModAction.Ask)
    {
        var host = new Host { Setting = setting };
        return (new NewModSortPipeline(host), host);
    }

    [Fact]
    public void OnlyHeldMods_ShowNotice_NoBanner_NothingStarts()
    {
        var (pipeline, host) = Make();
        pipeline.Offer(["hs1"], 0, new ToolHeldCounts(1, 0));
        pipeline.Update(T0);

        Assert.Null(pipeline.Banner);
        Assert.Equal(0, pipeline.PendingCount);
        Assert.Equal(0, host.ScanStarts);
        Assert.Empty(host.Chats);
        var notice = Assert.IsType<NewModToolNotice>(pipeline.ToolNotice);
        Assert.Equal(new ToolHeldCounts(1, 0), notice.Counts);
    }

    [Fact]
    public void Notice_Dismissed_IsGone_AndANewBatchStartsAFreshCount()
    {
        var (pipeline, _) = Make();
        pipeline.Offer(["hs1", "hs2"], 0, new ToolHeldCounts(2, 0));
        pipeline.DismissToolNotice();
        Assert.Null(pipeline.ToolNotice);

        pipeline.Offer(["p1"], 0, new ToolHeldCounts(0, 1));
        Assert.Equal(new ToolHeldCounts(0, 1), pipeline.ToolNotice!.Counts);
    }

    [Fact]
    public void TwoHeldBatches_CoalesceIntoOneNotice()
    {
        var (pipeline, _) = Make();
        pipeline.Offer(["hs1"], 0, new ToolHeldCounts(1, 0));
        pipeline.Offer(["hs2", "p1"], 0, new ToolHeldCounts(1, 1));

        Assert.Equal(new ToolHeldCounts(2, 1), pipeline.ToolNotice!.Counts);
    }

    [Fact]
    public void PopupOff_SilencesTheNotice_NoChat()
    {
        var (pipeline, host) = Make();
        host.ShowPopup = false;
        pipeline.Offer(["hs1"], 0, new ToolHeldCounts(1, 0));
        pipeline.Update(T0);

        Assert.Null(pipeline.ToolNotice);
        Assert.Empty(host.Chats);

        // Turning the popup back on later does not resurrect a notice that was silenced.
        host.ShowPopup = true;
        Assert.Null(pipeline.ToolNotice);
    }

    [Fact]
    public void DoNothing_SilencesTheNotice()
    {
        var (pipeline, host) = Make(NewModAction.DoNothing);
        pipeline.Offer(["hs1"], 0, new ToolHeldCounts(1, 0));
        pipeline.Update(T0);

        Assert.Null(pipeline.ToolNotice);
        Assert.Empty(host.Chats);
    }

    [Fact]
    public void PopupTurnedOffWhileNoticeShowing_HidesIt()
    {
        var (pipeline, host) = Make();
        pipeline.Offer(["hs1"], 0, new ToolHeldCounts(1, 0));
        host.ShowPopup = false;
        Assert.Null(pipeline.ToolNotice);
    }

    [Fact]
    public void MixedBatch_OneBannerWithTheExtraLine_NoSeparateNotice()
    {
        var (pipeline, _) = Make();
        pipeline.Offer(["plain1", "plain2", "hs1"], 2, new ToolHeldCounts(1, 0));

        var banner = pipeline.Banner!;
        Assert.Equal(2, banner.Count);
        Assert.Equal("2 new mods are loose in the root. Sort them?", banner.Message);
        Assert.Equal(NewModPromptText.BannerToolLine(new ToolHeldCounts(1, 0)), banner.ToolLine);
        Assert.Null(pipeline.ToolNotice);
    }

    [Fact]
    public void MixedBatch_AnsweringTheBanner_ClearsTheHeldLine()
    {
        var (pipeline, _) = Make();
        pipeline.Offer(["plain1", "hs1"], 1, new ToolHeldCounts(1, 0));
        pipeline.UserChose(NewModChoice.NotNow);

        Assert.Null(pipeline.Banner);
        Assert.Null(pipeline.ToolNotice);
    }

    [Fact]
    public void BannerWithoutHeldMods_HasNoToolLine()
    {
        var (pipeline, _) = Make();
        pipeline.Offer(["plain1"], 1, new ToolHeldCounts(0, 0));
        Assert.Null(pipeline.Banner!.ToolLine);
        Assert.Null(pipeline.ToolNotice);
    }

    [Fact]
    public void Automatic_MixedBatch_RunsTheEligible_ThenShowsTheNotice()
    {
        var (pipeline, host) = Make(NewModAction.SortAutomatically);
        pipeline.Offer(["plain1", "hs1"], 1, new ToolHeldCounts(1, 0));
        Assert.Null(pipeline.Banner);
        Assert.Null(pipeline.ToolNotice); // hidden while the run is in progress

        pipeline.Update(T0);
        Assert.Equal(1, host.ScanStarts);
        host.FinishScan();
        pipeline.Update(T0.AddSeconds(1)); // nothing changed in the fake sort: the run finishes

        Assert.Equal(NewModPipelineStage.Idle, pipeline.Stage);
        Assert.Equal(new ToolHeldCounts(1, 0), pipeline.ToolNotice!.Counts);
    }

    [Fact]
    public void ThreeArgumentOffer_WithNothingHeld_BehavesLikeTheOldOffer()
    {
        var (pipeline, host) = Make();
        pipeline.Offer(["a", "b"], 0, new ToolHeldCounts(0, 0));
        pipeline.Update(T0);

        Assert.Null(pipeline.Banner);
        Assert.Null(pipeline.ToolNotice);
        Assert.Empty(host.Chats);
    }

    // ---- Confirm window tag ----

    [Fact]
    public void Preview_CarriesTheToolTag()
    {
        var preview = NewModPreview.Build(
            [new StagedMove("hs mod", "hs mod", "Gear/hs mod", "Heliosphere"), new StagedMove("plain", "plain", "Gear/plain")],
            null, newMods: true);

        Assert.Equal("Heliosphere", preview.Rows.Single(r => r.Name == "hs mod").Tool);
        Assert.Null(preview.Rows.Single(r => r.Name == "plain").Tool);
    }

    [Fact]
    public void ToolTag_NamesTheTool()
    {
        Assert.Equal("Heliosphere", NewModPromptText.ToolTag(heliosphereManaged: true, ProteusRole.None));
        Assert.Equal("Proteus", NewModPromptText.ToolTag(heliosphereManaged: false, ProteusRole.Made));
        Assert.Equal("Proteus", NewModPromptText.ToolTag(heliosphereManaged: true, ProteusRole.Managed));
        Assert.Null(NewModPromptText.ToolTag(heliosphereManaged: false, ProteusRole.None));
    }
}
