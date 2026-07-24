using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class RecoveryAssessmentBuilderTests
{
    private static OperationPlan SimplePlan() => OperationPlan.Create(
        OperationType.Apply, [new(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0)],
        [new("mod-a", "Gear/A", "Weapons/A", "mod-a")]);

    private static LiveModSnapshot Live(params LiveMod[] mods) => LiveModSnapshotBuilder.Build(mods);

    [Fact]
    public void Build_ClassificationsMatchADirectClassifyCall()
    {
        var plan = SimplePlan();
        var live = Live(new LiveMod("mod-a", "mod-a", "Weapons/A", false));

        var assessment = RecoveryAssessmentBuilder.Build(plan, live);
        var direct = RecoveryClassifier.Classify(plan, live);

        Assert.Equal(direct, assessment.Classifications);
    }

    [Fact]
    public void Build_Fingerprint_IsDeterministic()
    {
        var plan = SimplePlan();
        var live = Live(new LiveMod("mod-a", "mod-a", "Weapons/A", false));

        var first = RecoveryAssessmentBuilder.Build(plan, live).LiveStateFingerprint;
        var second = RecoveryAssessmentBuilder.Build(plan, live).LiveStateFingerprint;

        Assert.Equal(first, second);
    }

    [Fact]
    public void Build_Fingerprint_IsOrderIndependent()
    {
        var plan = SimplePlan();
        var liveAB = Live(new LiveMod("mod-a", "A", "Weapons/A", false), new LiveMod("mod-b", "B", "Weapons/B", false));
        var liveBA = Live(new LiveMod("mod-b", "B", "Weapons/B", false), new LiveMod("mod-a", "A", "Weapons/A", false));

        Assert.Equal(RecoveryAssessmentBuilder.Build(plan, liveAB).LiveStateFingerprint, RecoveryAssessmentBuilder.Build(plan, liveBA).LiveStateFingerprint);
    }

    [Fact]
    public void Build_Fingerprint_DiffersWhenDuplicateIdentifiersDiffer()
    {
        var plan = SimplePlan();
        var mod = new LiveMod("mod-a", "mod-a", "Weapons/A", false);
        var withoutDuplicates = new LiveModSnapshot(new Dictionary<string, LiveMod> { ["mod-a"] = mod }, new HashSet<string>());
        var withDuplicates = new LiveModSnapshot(new Dictionary<string, LiveMod> { ["mod-a"] = mod }, new HashSet<string> { "some-other-id" });

        var first = RecoveryAssessmentBuilder.Build(plan, withoutDuplicates).LiveStateFingerprint;
        var second = RecoveryAssessmentBuilder.Build(plan, withDuplicates).LiveStateFingerprint;

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Build_Fingerprint_DiffersWhenModNameDiffers()
    {
        var plan = SimplePlan();
        var liveNamedX = Live(new LiveMod("mod-a", "Name X", "Weapons/A", false));
        var liveNamedY = Live(new LiveMod("mod-a", "Name Y", "Weapons/A", false));

        var first = RecoveryAssessmentBuilder.Build(plan, liveNamedX).LiveStateFingerprint;
        var second = RecoveryAssessmentBuilder.Build(plan, liveNamedY).LiveStateFingerprint;

        Assert.NotEqual(first, second);
    }
}
