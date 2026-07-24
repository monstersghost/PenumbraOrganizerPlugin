namespace PenumbraOrganizer.Plugin.Organizer.Operations;

/// <summary>
/// One atomic read feeding both classification and (in a later plan) Continue's residual replanning -
/// never two independent GetLiveMods() calls that could disagree if the library changes mid-flow.
/// LiveStateFingerprint hashes PenumbraPathSemantics-normalized paths, so it proves semantic live-
/// state continuity, not raw byte-for-byte path identity - a purely cosmetic raw-path change (e.g.
/// Penumbra's own " (N)" suffix reshuffling) will not change it, by design.
/// </summary>
public sealed record RecoveryAssessment(
    LiveModSnapshot LiveSnapshot,
    IReadOnlyList<ItemRecoveryClassification> Classifications,
    string LiveStateFingerprint);

public static class RecoveryAssessmentBuilder
{
    public static RecoveryAssessment Build(OperationPlan plan, LiveModSnapshot liveSnapshot)
    {
        var classifications = RecoveryClassifier.Classify(plan, liveSnapshot);
        var fingerprint = ComputeFingerprint(liveSnapshot);
        return new RecoveryAssessment(liveSnapshot, classifications, fingerprint);
    }

    private static string ComputeFingerprint(LiveModSnapshot liveSnapshot)
    {
        var sb = new System.Text.StringBuilder();
        void Field(string value) => sb.Append(System.Text.Encoding.UTF8.GetByteCount(value)
            .ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':').Append(value);

        foreach (var (identifier, mod) in liveSnapshot.Mods.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            Field(identifier);
            Field(mod.Name);
            Field(PenumbraPathSemantics.Normalize(mod.FullPath, mod.Name));
        }

        Field(liveSnapshot.DuplicateIdentifiers.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var dup in liveSnapshot.DuplicateIdentifiers.OrderBy(d => d, StringComparer.Ordinal))
            Field(dup);

        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
