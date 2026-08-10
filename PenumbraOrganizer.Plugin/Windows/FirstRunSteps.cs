namespace PenumbraOrganizer.Plugin.Windows;

/// <summary>
/// Navigation and completion for the first-run walkthrough.
/// </summary>
/// <remarks>
/// Deliberately free of ImGui and Dalamud so it can be tested; <see cref="FirstRunWindow"/> is the
/// thin drawing shell around it. The test project cannot construct a Dalamud <c>Window</c> or enter
/// an ImGui frame, so every rule below would otherwise be unverifiable.
/// <para>
/// public for the same reason everything else here is: no <c>InternalsVisibleTo</c> in this repo.
/// </para>
/// </remarks>
public sealed class FirstRunSteps
{
    /// <summary>The walkthrough, in order. Order lives here, not in the resource.</summary>
    public static IReadOnlyList<HelpTopic> DefaultSteps { get; } =
    [
        HelpTopics.FirstRunWelcome,
        HelpTopics.FirstRunScan,
        HelpTopics.FirstRunProtect,
        HelpTopics.FirstRunSort,
        HelpTopics.FirstRunReview,
        HelpTopics.FirstRunApply,
        HelpTopics.FirstRunHelp,
    ];

    private readonly IReadOnlyList<HelpTopic> _steps;
    private readonly bool _penumbraAvailable;

    public FirstRunSteps(IReadOnlyList<HelpTopic> steps, bool penumbraAvailable)
    {
        _steps = steps;
        _penumbraAvailable = penumbraAvailable;
    }

    public FirstRunSteps(bool penumbraAvailable) : this(DefaultSteps, penumbraAvailable) { }

    public int Index { get; private set; }

    public bool IsFinished { get; private set; }

    /// <summary>
    /// True when the run reached a real ending. Always false on the Penumbra-unavailable path,
    /// whichever way that notice is dismissed - it makes no decision, so it must not consume the
    /// one first run the user gets.
    /// </summary>
    public bool ShouldMarkSeen { get; private set; }

    /// <summary>Whether the walkthrough proper is running, as opposed to the one-step notice.</summary>
    public bool IsWalkthrough => _penumbraAvailable;

    public bool IsLastStep => !_penumbraAvailable || Index >= _steps.Count - 1;

    public bool CanGoBack => _penumbraAvailable && Index > 0;

    /// <summary>Total steps, for a "3 of 7" style indicator. One when Penumbra is unavailable.</summary>
    public int Count => _penumbraAvailable ? _steps.Count : 1;

    public HelpTopic Current => _penumbraAvailable
        ? _steps[Index]
        : HelpTopics.FirstRunPenumbraUnavailable;

    /// <summary>
    /// Advances, or finishes past the last step. Finishing this way marks the run seen only when
    /// Penumbra was available - see <see cref="ShouldMarkSeen"/>.
    /// </summary>
    public void Next()
    {
        if (!_penumbraAvailable)
        {
            Finish(markSeen: false);
            return;
        }

        if (Index < _steps.Count - 1)
        {
            Index++;
            return;
        }

        Finish(markSeen: true);
    }

    /// <summary>No-op at the first step, and on the Penumbra-unavailable notice.</summary>
    public void Back()
    {
        if (CanGoBack)
            Index--;
    }

    /// <summary>
    /// An explicit decision not to read it, which counts as having seen it. Not reachable from the
    /// Penumbra-unavailable notice, which is a single step and so renders no Skip button - and
    /// would not mark the run seen even if it did.
    /// </summary>
    public void Skip() => Finish(markSeen: true);

    /// <summary>
    /// The window's close button. Same outcome as <see cref="Skip"/> - and this is the likeliest
    /// exit of the three. A Dalamud Window renders an X by default, so writing the flag only on
    /// Next-from-last and Skip would hand step one to that user every session, forever.
    /// </summary>
    public void Closed() => Finish(markSeen: true);

    private void Finish(bool markSeen)
    {
        IsFinished = true;

        // Two rules, and the second one is load-bearing in a way that is easy to miss.
        //
        // Or-ing never clears an already-earned true: Closed() arrives after Skip() in the same
        // frame, because dismissing the window IS a close.
        //
        // But that alone made the Penumbra-unavailable path unreachable in production. Every exit
        // in FirstRunWindow routes through IsOpen = false, so Dalamud calls OnClose, which calls
        // Closed(), which asks for markSeen: true - overwriting the false that Next() set on the
        // unavailable path. The run got consumed and the notice never appeared again, while four
        // separate pieces of user-facing text promised the opposite. Gating on _penumbraAvailable
        // here rather than at each call site makes that promise hold whichever button was pressed.
        ShouldMarkSeen |= markSeen && _penumbraAvailable;
    }
}
