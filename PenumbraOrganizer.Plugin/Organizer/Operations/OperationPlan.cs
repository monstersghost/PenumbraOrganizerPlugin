using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PenumbraOrganizer.Plugin.Organizer.Operations;

public enum OperationType { Apply, Restore }

public enum OperationStepKind { FinalMove, CycleBreakingTemporaryMove }

/// <summary> One physical SetModPath action. Duplicates per identifier are allowed (a cycle emits a
/// temporary hop and then a final move for the same mod). </summary>
public sealed record OperationExecutionStep(
    int StepIndex, string Identifier, string TargetRawPath, OperationStepKind Kind, int GroupId);

/// <summary> The desired before/after state for one mod, one per identifier - what recovery compares
/// live state against. Carries the snapshot path explicitly so recovery never infers it from steps. </summary>
public sealed record OperationRecoveryTarget(
    string Identifier, string SnapshotRawPath, string FinalRawPath, string ModName);

public sealed record OperationPlan(
    int SchemaVersion,
    Guid OperationId,
    OperationType Type,
    DateTimeOffset CreatedAt,
    IReadOnlyList<OperationExecutionStep> ExecutionSteps,
    IReadOnlyList<OperationRecoveryTarget> RecoveryTargets,
    string IntegrityHash)
{
    public const int CurrentSchemaVersion = 2;

    public static OperationPlan Create(
        OperationType type,
        IReadOnlyList<OperationExecutionStep> executionSteps,
        IReadOnlyList<OperationRecoveryTarget> recoveryTargets)
    {
        Validate(executionSteps, recoveryTargets);
        return new OperationPlan(
            CurrentSchemaVersion, Guid.NewGuid(), type, DateTimeOffset.UtcNow,
            executionSteps, recoveryTargets, ComputeIntegrityHash(type, executionSteps, recoveryTargets));
    }

    public bool Verify() => IntegrityHash == ComputeIntegrityHash(Type, ExecutionSteps, RecoveryTargets);

    // Throws InvalidOperationException on any structural violation - a plan must never be persisted
    // in a state it would reject on reload. See design doc section 3 for the full invariant list.
    private static void Validate(
        IReadOnlyList<OperationExecutionStep> steps,
        IReadOnlyList<OperationRecoveryTarget> targets)
    {
        var targetByIdentifier = new Dictionary<string, OperationRecoveryTarget>(StringComparer.Ordinal);
        foreach (var t in targets)
            if (!targetByIdentifier.TryAdd(t.Identifier, t))
                throw new InvalidOperationException($"Duplicate recovery target identifier '{t.Identifier}'.");

        for (var i = 0; i < steps.Count; i++)
            if (steps[i].StepIndex != i)
                throw new InvalidOperationException(
                    $"Execution steps must have contiguous indices from 0; position {i} has StepIndex {steps[i].StepIndex}.");

        // GroupId: non-negative, first is 0, stays same or increments by exactly 1 in index order.
        // This alone guarantees 0-based, contiguous, non-interleaved group blocks.
        int? prevGroup = null;
        foreach (var s in steps)
        {
            if (s.GroupId < 0)
                throw new InvalidOperationException($"Step {s.StepIndex} has a negative GroupId ({s.GroupId}).");
            if (prevGroup is null)
            {
                if (s.GroupId != 0)
                    throw new InvalidOperationException($"First step must have GroupId 0; found {s.GroupId}.");
            }
            else if (s.GroupId != prevGroup && s.GroupId != prevGroup + 1)
            {
                throw new InvalidOperationException(
                    $"GroupId must stay equal or increment by 1 across steps in index order; went from {prevGroup} to {s.GroupId}.");
            }

            prevGroup = s.GroupId;
        }

        var groupByIdentifier = new Dictionary<string, int>(StringComparer.Ordinal);
        var lastStepByIdentifier = new Dictionary<string, OperationExecutionStep>(StringComparer.Ordinal);
        foreach (var s in steps)
        {
            if (!targetByIdentifier.ContainsKey(s.Identifier))
                throw new InvalidOperationException($"Execution step identifier '{s.Identifier}' has no recovery target.");
            if (groupByIdentifier.TryGetValue(s.Identifier, out var g))
            {
                if (g != s.GroupId)
                    throw new InvalidOperationException(
                        $"Identifier '{s.Identifier}' appears in more than one group ({g} and {s.GroupId}).");
            }
            else
            {
                groupByIdentifier[s.Identifier] = s.GroupId;
            }

            lastStepByIdentifier[s.Identifier] = s; // index-ordered, so the final write is the highest-index step
        }

        // Invariant 10 (explicit, defense-in-depth): every recovery target's identifier maps to exactly
        // one GroupId. This is recomputed independently from `steps` directly (not via groupByIdentifier
        // above) so it still catches a regression even if the "one identifier per group" check above is
        // ever refactored away.
        foreach (var t in targets)
        {
            var distinctGroupsForIdentifier = steps
                .Where(s => s.Identifier == t.Identifier)
                .Select(s => s.GroupId)
                .Distinct()
                .ToList();
            if (distinctGroupsForIdentifier.Count != 1)
                throw new InvalidOperationException(
                    $"Recovery target '{t.Identifier}' must map to exactly one GroupId; found {distinctGroupsForIdentifier.Count}.");
        }

        // Invariant 11 (explicit, defense-in-depth): a cycle-breaking temporary step and its identifier's
        // corresponding final step must share the same GroupId. Compared directly against
        // lastStepByIdentifier rather than relying on the per-step throw above.
        foreach (var s in steps)
        {
            if (s.Kind != OperationStepKind.CycleBreakingTemporaryMove)
                continue;
            if (!lastStepByIdentifier.TryGetValue(s.Identifier, out var finalStep))
                continue; // no execution step / no target for this identifier - caught elsewhere

            if (finalStep.GroupId != s.GroupId)
                throw new InvalidOperationException(
                    $"Identifier '{s.Identifier}' has a cycle-breaking temporary step in GroupId {s.GroupId} " +
                    $"but its final step is in GroupId {finalStep.GroupId}.");
        }

        foreach (var t in targets)
        {
            if (!lastStepByIdentifier.TryGetValue(t.Identifier, out var last))
                throw new InvalidOperationException($"Recovery target '{t.Identifier}' has no execution step.");
            if (last.Kind != OperationStepKind.FinalMove)
                throw new InvalidOperationException($"The last step for '{t.Identifier}' must be a FinalMove.");
            if (!PenumbraPathSemantics.AreEquivalent(last.TargetRawPath, t.FinalRawPath, t.ModName))
                throw new InvalidOperationException($"The last step for '{t.Identifier}' must target its FinalRawPath.");
        }
    }

    // Canonical, length-prefixed encoding (<utf8-byte-length>:<utf8-bytes> per field, concatenated,
    // no separators - unambiguous without depending on any character being absent from the data).
    // Covers every execution-relevant field including Kind and GroupId; excludes OperationId and
    // CreatedAt (identity, not executable content). Paths are normalized so a Penumbra reload that
    // reshuffles a " (N)" suffix cannot change the hash. Assumes validated input (Create validates
    // first): every step identifier resolves to a recovery target for the display-name lookup.
    public static string ComputeIntegrityHash(
        OperationType type,
        IReadOnlyList<OperationExecutionStep> steps,
        IReadOnlyList<OperationRecoveryTarget> targets)
    {
        var nameByIdentifier = targets.ToDictionary(t => t.Identifier, t => t.ModName, StringComparer.Ordinal);
        var sb = new StringBuilder();

        void Field(string value)
        {
            sb.Append(Encoding.UTF8.GetByteCount(value).ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
        }

        Field(CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture));
        Field(type.ToString());
        Field(steps.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var s in steps.OrderBy(s => s.StepIndex))
        {
            Field(s.StepIndex.ToString(CultureInfo.InvariantCulture));
            Field(s.Identifier);
            Field(PenumbraPathSemantics.Normalize(s.TargetRawPath, nameByIdentifier[s.Identifier]));
            Field(s.Kind.ToString());
            Field(s.GroupId.ToString(CultureInfo.InvariantCulture));
        }

        Field(targets.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var t in targets.OrderBy(t => t.Identifier, StringComparer.Ordinal))
        {
            Field(t.Identifier);
            Field(PenumbraPathSemantics.Normalize(t.SnapshotRawPath, t.ModName));
            Field(PenumbraPathSemantics.Normalize(t.FinalRawPath, t.ModName));
            Field(t.ModName);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}

public static class OperationPlanCodec
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static void Save(string path, OperationPlan plan)
    {
        if (!plan.Verify())
            throw new InvalidOperationException("Refusing to persist an OperationPlan that fails its own integrity check.");

        AtomicFile.CreateOrReplace(path, JsonSerializer.Serialize(plan, SerializerOptions));
    }

    public static bool TryLoad(string path, out OperationPlan? plan)
    {
        plan = null;
        if (!AtomicFile.TryReadValidated(path, out var contents) || contents is null)
            return false;

        OperationPlan? candidate;
        try
        {
            candidate = JsonSerializer.Deserialize<OperationPlan>(contents, SerializerOptions);
        }
        catch (JsonException)
        {
            return false;
        }

        if (candidate is null || candidate.SchemaVersion != OperationPlan.CurrentSchemaVersion || !candidate.Verify())
            return false;

        plan = candidate;
        return true;
    }
}
