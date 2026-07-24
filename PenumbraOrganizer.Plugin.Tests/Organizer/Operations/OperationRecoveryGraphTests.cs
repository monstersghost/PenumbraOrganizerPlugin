using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class OperationRecoveryGraphTests
{
    private static OperationJournal Journal(Guid id, Guid? recoveryOf) => new(
        SchemaVersion: OperationJournal.CurrentSchemaVersion,
        OperationId: id,
        Type: OperationType.Apply,
        Stage: OperationStage.Mutating,
        Resolution: OperationResolution.None,
        SuccessorOperationId: null,
        CancellationRequested: false,
        StartedAt: DateTimeOffset.UtcNow,
        TotalSteps: 10,
        ProcessedStepCount: 3,
        LastCompletedIdentifier: null,
        SnapshotId: Guid.NewGuid(),
        PlanId: Guid.NewGuid(),
        TargetHash: "irrelevant",
        RecoveryOfOperationId: recoveryOf,
        UpdatedAt: DateTimeOffset.UtcNow);

    [Fact]
    public void Analyze_EmptyList_NoRecoveryNeeded()
    {
        var result = OperationRecoveryGraph.Analyze([]);

        Assert.Equal(OperationRecoveryGraphStatus.NoRecoveryNeeded, result.Status);
        Assert.Empty(result.AuthoritativeOperationIds);
        Assert.Empty(result.AllOperationIds);
    }

    [Fact]
    public void Analyze_SingleUnrelatedJournal_SingleAuthoritative()
    {
        var id = Guid.NewGuid();
        var result = OperationRecoveryGraph.Analyze([Journal(id, recoveryOf: null)]);

        Assert.Equal(OperationRecoveryGraphStatus.SingleAuthoritative, result.Status);
        Assert.Equal([id], result.AuthoritativeOperationIds);
    }

    [Fact]
    public void Analyze_JournalWhoseParentIsNotInTheSet_TreatedAsItsOwnSingleAuthoritativeComponent()
    {
        // The parent already terminalized cleanly and isn't in this "non-terminal journals" list -
        // the common case. The child must not be treated as part of a cycle or an orphaned edge.
        var childId = Guid.NewGuid();
        var result = OperationRecoveryGraph.Analyze([Journal(childId, recoveryOf: Guid.NewGuid())]);

        Assert.Equal(OperationRecoveryGraphStatus.SingleAuthoritative, result.Status);
        Assert.Equal([childId], result.AuthoritativeOperationIds);
    }

    [Fact]
    public void Analyze_TwoJournalChain_ChildIsAuthoritativeParentIsNot()
    {
        var parentId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var journals = new[] { Journal(parentId, recoveryOf: null), Journal(childId, recoveryOf: parentId) };

        var result = OperationRecoveryGraph.Analyze(journals);

        Assert.Equal(OperationRecoveryGraphStatus.SingleAuthoritative, result.Status);
        Assert.Equal([childId], result.AuthoritativeOperationIds);
        Assert.Equal(2, result.AllOperationIds.Count);
    }

    [Fact]
    public void Analyze_ThreeJournalChain_OnlyTheYoungestGrandchildIsAuthoritative()
    {
        var grandparentId = Guid.NewGuid();
        var parentId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var journals = new[]
        {
            Journal(grandparentId, recoveryOf: null),
            Journal(parentId, recoveryOf: grandparentId),
            Journal(childId, recoveryOf: parentId),
        };

        var result = OperationRecoveryGraph.Analyze(journals);

        Assert.Equal(OperationRecoveryGraphStatus.SingleAuthoritative, result.Status);
        Assert.Equal([childId], result.AuthoritativeOperationIds);
        Assert.Equal(3, result.AllOperationIds.Count);
    }

    [Fact]
    public void Analyze_TwoIndependentJournals_MultipleDisconnectedRoots()
    {
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var journals = new[] { Journal(idA, recoveryOf: null), Journal(idB, recoveryOf: null) };

        var result = OperationRecoveryGraph.Analyze(journals);

        Assert.Equal(OperationRecoveryGraphStatus.MultipleDisconnectedRoots, result.Status);
        Assert.Equal(new HashSet<Guid> { idA, idB }, result.AuthoritativeOperationIds.ToHashSet());
    }

    [Fact]
    public void Analyze_DirectCycle_CycleDetected()
    {
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var journals = new[] { Journal(idA, recoveryOf: idB), Journal(idB, recoveryOf: idA) };

        var result = OperationRecoveryGraph.Analyze(journals);

        Assert.Equal(OperationRecoveryGraphStatus.CycleDetected, result.Status);
        Assert.Equal(new HashSet<Guid> { idA, idB }, result.AuthoritativeOperationIds.ToHashSet());
    }

    [Fact]
    public void Analyze_ThreeWayCycle_CycleDetected()
    {
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var idC = Guid.NewGuid();
        var journals = new[] { Journal(idA, recoveryOf: idB), Journal(idB, recoveryOf: idC), Journal(idC, recoveryOf: idA) };

        var result = OperationRecoveryGraph.Analyze(journals);

        Assert.Equal(OperationRecoveryGraphStatus.CycleDetected, result.Status);
    }

    [Fact]
    public void Analyze_SelfReferencingJournal_CycleDetected()
    {
        // Corrupted data: a journal claiming to be a recovery of itself. Must not infinite-loop.
        var id = Guid.NewGuid();
        var result = OperationRecoveryGraph.Analyze([Journal(id, recoveryOf: id)]);

        Assert.Equal(OperationRecoveryGraphStatus.CycleDetected, result.Status);
        Assert.Equal(new HashSet<Guid> { id }, result.AuthoritativeOperationIds.ToHashSet());
    }

    [Fact]
    public void Analyze_TwoLeavesShareTerminalizedAncestorNotInSet_MultipleDisconnectedRootsNotCycle()
    {
        // Two independent non-terminal journals whose RecoveryOfOperationId points at the SAME
        // already-terminalized (and therefore absent) parent. Sharing an out-of-set ancestor must
        // not be mistaken for a relationship between the two journals themselves.
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var missingParent = Guid.NewGuid();
        var journals = new[] { Journal(idA, recoveryOf: missingParent), Journal(idB, recoveryOf: missingParent) };

        var result = OperationRecoveryGraph.Analyze(journals);

        Assert.Equal(OperationRecoveryGraphStatus.MultipleDisconnectedRoots, result.Status);
        Assert.Equal(new HashSet<Guid> { idA, idB }, result.AuthoritativeOperationIds.ToHashSet());
    }

    [Fact]
    public void Analyze_TwoSeparateTailsFeedingIntoTheSameCycle_BothTailsIncludedInCycleMembers()
    {
        var tailD = Guid.NewGuid();
        var tailE = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var idC = Guid.NewGuid();
        var journals = new[]
        {
            Journal(tailD, recoveryOf: idB),
            Journal(tailE, recoveryOf: idC),
            Journal(idB, recoveryOf: idC),
            Journal(idC, recoveryOf: idB),
        };

        var result = OperationRecoveryGraph.Analyze(journals);

        Assert.Equal(OperationRecoveryGraphStatus.CycleDetected, result.Status);
        Assert.Equal(new HashSet<Guid> { tailD, tailE, idB, idC }, result.AuthoritativeOperationIds.ToHashSet());
    }

    [Fact]
    public void Analyze_TwoSeparateTailsFeedingIntoTheSameCycle_ResultIsIndependentOfInputOrder()
    {
        var tailD = Guid.NewGuid();
        var tailE = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var idC = Guid.NewGuid();
        var journalsForward = new[]
        {
            Journal(tailD, recoveryOf: idB),
            Journal(tailE, recoveryOf: idC),
            Journal(idB, recoveryOf: idC),
            Journal(idC, recoveryOf: idB),
        };
        var journalsReversed = journalsForward.Reverse().ToArray();

        var resultForward = OperationRecoveryGraph.Analyze(journalsForward);
        var resultReversed = OperationRecoveryGraph.Analyze(journalsReversed);

        Assert.Equal(resultForward.AuthoritativeOperationIds.ToHashSet(), resultReversed.AuthoritativeOperationIds.ToHashSet());
    }
}
