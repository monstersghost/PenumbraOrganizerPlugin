using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

/// <summary>
/// Test double for IPenumbraOperations. Every call must have a queued response, or it throws -
/// a test that forgets to set up an expected call fails loudly rather than silently returning a
/// default value that could mask a real bug. EnqueueSetModPathException is the deliberate way to
/// simulate a real adapter failure in a test; the empty-queue throw is a test-infrastructure
/// safety net only and must never be relied on to simulate anything.
/// </summary>
public sealed class FakePenumbraOperations : IPenumbraOperations
{
    private readonly Queue<(LiveModReadResult Result, Action? OnCall)> _liveModReads = new();
    private readonly Queue<(SetModPathResult? Result, Exception? Exception, Action? OnCall)> _setModPathResponses = new();
    private readonly Queue<(RefreshResult Result, Action? OnCall)> _refreshResults = new();
    private readonly List<(string Identifier, string TargetPath)> _setModPathCalls = [];

    public IReadOnlyList<(string Identifier, string TargetPath)> SetModPathCalls => _setModPathCalls;

    public void EnqueueLiveModRead(LiveModReadResult result, Action? onCall = null) =>
        _liveModReads.Enqueue((result, onCall));

    public void EnqueueSetModPathResult(SetModPathResult result, Action? onCall = null) =>
        _setModPathResponses.Enqueue((result, null, onCall));

    public void EnqueueSetModPathException(Exception exception) =>
        _setModPathResponses.Enqueue((null, exception, null));

    public void EnqueueRefreshResult(RefreshResult result, Action? onCall = null) =>
        _refreshResults.Enqueue((result, onCall));

    public LiveModReadResult GetLiveMods()
    {
        if (_liveModReads.Count == 0)
            throw new InvalidOperationException("FakePenumbraOperations.GetLiveMods called with no queued result.");

        var (result, onCall) = _liveModReads.Dequeue();
        onCall?.Invoke();
        return result;
    }

    public SetModPathResult SetModPath(string identifier, string targetPath)
    {
        _setModPathCalls.Add((identifier, targetPath));

        if (_setModPathResponses.Count == 0)
            throw new InvalidOperationException("FakePenumbraOperations.SetModPath called with no queued response.");

        var (result, exception, onCall) = _setModPathResponses.Dequeue();
        onCall?.Invoke();
        if (exception is not null)
            throw exception;

        return result!;
    }

    public RefreshResult RequestPostMutationRefresh()
    {
        if (_refreshResults.Count == 0)
            throw new InvalidOperationException("FakePenumbraOperations.RequestPostMutationRefresh called with no queued result.");

        var (result, onCall) = _refreshResults.Dequeue();
        onCall?.Invoke();
        return result;
    }
}
