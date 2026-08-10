using PenumbraOrganizer.Plugin.Windows;

namespace PenumbraOrganizer.Plugin.Tests.Windows;

public class FirstRunStepsTests
{
    private static FirstRunSteps Available() => new(penumbraAvailable: true);

    private static FirstRunSteps RunToEnd(FirstRunSteps steps)
    {
        // One Next per step: the last one finishes rather than advancing.
        for (var i = 0; i < steps.Count; i++)
            steps.Next();
        return steps;
    }

    [Fact]
    public void NextPastTheLastStep_ClosesAndMarksSeen()
    {
        var steps = RunToEnd(Available());

        Assert.True(steps.IsFinished);
        Assert.True(steps.ShouldMarkSeen);
    }

    [Fact]
    public void NextAdvancesOneStepAtATime_AndDoesNotFinishEarly()
    {
        var steps = Available();

        steps.Next();

        Assert.Equal(1, steps.Index);
        Assert.False(steps.IsFinished);
    }

    [Fact]
    public void BackFromTheFirstStep_IsANoOp()
    {
        var steps = Available();

        steps.Back();

        Assert.Equal(0, steps.Index);
        Assert.False(steps.IsFinished);
        Assert.False(steps.CanGoBack);
    }

    [Fact]
    public void BackReturnsToThePreviousStep()
    {
        var steps = Available();
        steps.Next();
        steps.Next();

        steps.Back();

        Assert.Equal(1, steps.Index);
    }

    [Fact]
    public void SkipAtAnyStep_MarksSeen()
    {
        var steps = Available();
        steps.Next();

        steps.Skip();

        Assert.True(steps.IsFinished);
        Assert.True(steps.ShouldMarkSeen);
    }

    [Fact]
    public void ClosingTheWindow_MarksSeen()
    {
        // The most likely exit. A Dalamud Window renders an X by default; writing the flag only on
        // Next-from-last and Skip hands step 1 to that user every session, forever.
        var steps = Available();

        steps.Closed();

        Assert.True(steps.IsFinished);
        Assert.True(steps.ShouldMarkSeen);
    }

    [Fact]
    public void ClosingAfterSkipping_DoesNotClearTheFlag()
    {
        // Dismissing the window IS a close, so Closed() can follow Skip() in the same frame. If
        // Finish assigned rather than or-ed, the Penumbra-unavailable rule would clear a true here.
        var steps = new FirstRunSteps(penumbraAvailable: false);
        steps.Skip();

        steps.Closed();

        Assert.True(steps.ShouldMarkSeen);
    }

    [Fact]
    public void ProgressIsNotResumed_ReopeningStartsAtStepOne()
    {
        // FirstRunWindow builds a new FirstRunSteps each time it opens, so there is no partial
        // progress to restore - asserted here so a later "helpfully" persisted Index is caught.
        var first = Available();
        first.Next();
        first.Next();

        var reopened = Available();

        Assert.Equal(0, reopened.Index);
        Assert.False(reopened.IsFinished);
    }

    [Fact]
    public void WithPenumbraUnavailable_ShowsOneExplanatoryStep_AndDoesNotMarkSeen()
    {
        // Every step from "press Refresh mod list" onward describes results the user will not see,
        // and a first-run user is the likeliest to have Penumbra disabled. This is the one case
        // where no decision was made, so it appears again next time.
        var steps = new FirstRunSteps(penumbraAvailable: false);

        Assert.False(steps.IsWalkthrough);
        Assert.Equal(1, steps.Count);
        Assert.Equal(HelpTopics.FirstRunPenumbraUnavailable, steps.Current);

        steps.Next();

        Assert.True(steps.IsFinished);
        Assert.False(steps.ShouldMarkSeen);
    }

    [Fact]
    public void WithPenumbraUnavailable_BackIsANoOp()
    {
        var steps = new FirstRunSteps(penumbraAvailable: false);

        steps.Back();

        Assert.False(steps.CanGoBack);
        Assert.Equal(HelpTopics.FirstRunPenumbraUnavailable, steps.Current);
    }

    [Fact]
    public void EveryStepIdResolves_AndHasStepContent()
    {
        Assert.All(FirstRunSteps.DefaultSteps, topic =>
        {
            Assert.True(Help.TryGet(topic, out _), $"{topic.Id} is not in the resource");
            Assert.False(string.IsNullOrWhiteSpace(Help.Step(topic)), $"{topic.Id} has no step text");
        });

        Assert.False(string.IsNullOrWhiteSpace(Help.Step(HelpTopics.FirstRunPenumbraUnavailable)));
    }

    [Fact]
    public void EveryTopicWithAStep_IsInTheStepList()
    {
        // The third leg of the both-directions rule, alongside the short and body checks. Step
        // order lives in code, so a step added to the resource and never listed renders nowhere.
        var listed = FirstRunSteps.DefaultSteps
            .Append(HelpTopics.FirstRunPenumbraUnavailable)
            .Select(t => t.Id)
            .ToHashSet(StringComparer.Ordinal);

        var withStep = HelpTopics.All.Where(t => Help.Step(t) is not null).Select(t => t.Id);

        Assert.Empty(withStep.Where(id => !listed.Contains(id)));
    }

    [Fact]
    public void EveryStepTopicCarriesOnlyAStep_NotATooltipOrASection()
    {
        // A short here would make EveryTopicWithAShort_IsDeclaredAsUsedByAControl demand a widget
        // that does not exist; a body would make EveryTopicWithABody_AppearsInSomeSection demand a
        // Help tab section. Keeping these single-purpose keeps all three rules satisfiable.
        Assert.All(FirstRunSteps.DefaultSteps.Append(HelpTopics.FirstRunPenumbraUnavailable), t =>
        {
            Assert.Null(Help.Short(t));
            Assert.Null(Help.Body(t));
        });
    }

    [Fact]
    public void TheWalkthroughIsShortEnoughToFinish()
    {
        // A guided first run nobody reaches the end of is worse than none. Five to eight steps was
        // the design intent; this catches it growing into a manual by accretion.
        Assert.InRange(FirstRunSteps.DefaultSteps.Count, 5, 8);
    }
}
