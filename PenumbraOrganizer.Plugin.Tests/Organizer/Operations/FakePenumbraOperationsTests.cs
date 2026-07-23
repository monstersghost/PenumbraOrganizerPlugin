using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class FakePenumbraOperationsTests
{
    [Fact]
    public void GetLiveMods_ReturnsQueuedResultsInOrder()
    {
        var fake = new FakePenumbraOperations();
        var snapshot = LiveModSnapshotBuilder.Build([]);
        fake.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, snapshot));
        fake.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.ProviderUnavailable, null));

        var first = fake.GetLiveMods();
        var second = fake.GetLiveMods();

        Assert.Equal(LiveModReadStatus.Success, first.Status);
        Assert.Equal(LiveModReadStatus.ProviderUnavailable, second.Status);
    }

    [Fact]
    public void GetLiveMods_NoQueuedResult_Throws()
    {
        var fake = new FakePenumbraOperations();

        Assert.Throws<InvalidOperationException>(() => fake.GetLiveMods());
    }

    [Fact]
    public void SetModPath_ReturnsQueuedResultAndRecordsTheCall()
    {
        var fake = new FakePenumbraOperations();
        fake.EnqueueSetModPathResult(new SetModPathResult(SetModPathStatus.Success, "Success", null));

        var result = fake.SetModPath("mod-a", "Weapons/A");

        Assert.Equal(SetModPathStatus.Success, result.Status);
        var call = Assert.Single(fake.SetModPathCalls);
        Assert.Equal("mod-a", call.Identifier);
        Assert.Equal("Weapons/A", call.TargetPath);
    }

    [Fact]
    public void SetModPath_NoQueuedResponse_Throws()
    {
        var fake = new FakePenumbraOperations();

        Assert.Throws<InvalidOperationException>(() => fake.SetModPath("mod-a", "Weapons/A"));
    }

    [Fact]
    public void SetModPath_QueuedException_ThrowsThatExceptionAndStillRecordsTheCall()
    {
        var fake = new FakePenumbraOperations();
        fake.EnqueueSetModPathException(new InvalidOperationException("simulated adapter failure"));

        var thrown = Assert.Throws<InvalidOperationException>(() => fake.SetModPath("mod-a", "Weapons/A"));

        Assert.Equal("simulated adapter failure", thrown.Message);
        Assert.Single(fake.SetModPathCalls);
    }

    [Fact]
    public void SetModPath_OnCallSideEffect_RunsExactlyOnceWhenDequeued()
    {
        var fake = new FakePenumbraOperations();
        var sideEffectCount = 0;
        fake.EnqueueSetModPathResult(new SetModPathResult(SetModPathStatus.Success, "Success", null), onCall: () => sideEffectCount++);

        fake.SetModPath("mod-a", "Weapons/A");

        Assert.Equal(1, sideEffectCount);
    }

    [Fact]
    public void GetLiveMods_OnCallSideEffect_RunsExactlyOnceWhenDequeued()
    {
        var fake = new FakePenumbraOperations();
        var sideEffectCount = 0;
        fake.EnqueueLiveModRead(new LiveModReadResult(LiveModReadStatus.Success, LiveModSnapshotBuilder.Build([])), onCall: () => sideEffectCount++);

        fake.GetLiveMods();

        Assert.Equal(1, sideEffectCount);
    }

    [Fact]
    public void RequestPostMutationRefresh_ReturnsQueuedResult()
    {
        var fake = new FakePenumbraOperations();
        fake.EnqueueRefreshResult(new RefreshResult(RefreshStatus.Success));

        var result = fake.RequestPostMutationRefresh();

        Assert.Equal(RefreshStatus.Success, result.Status);
    }

    [Fact]
    public void RequestPostMutationRefresh_NoQueuedResult_Throws()
    {
        var fake = new FakePenumbraOperations();

        Assert.Throws<InvalidOperationException>(() => fake.RequestPostMutationRefresh());
    }

    [Fact]
    public void RequestPostMutationRefresh_OnCallSideEffect_RunsExactlyOnceWhenDequeued()
    {
        var fake = new FakePenumbraOperations();
        var sideEffectCount = 0;
        fake.EnqueueRefreshResult(new RefreshResult(RefreshStatus.Success), onCall: () => sideEffectCount++);

        fake.RequestPostMutationRefresh();

        Assert.Equal(1, sideEffectCount);
    }
}
