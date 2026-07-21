using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PenumbraOrganizer.Plugin.Organizer.Operations;

public enum OperationType { Apply, Restore }

public sealed record OperationPlanItem(
    string Identifier, string OriginalRawPath, string IntendedRawPath, string DisplayName);

public sealed record OperationPlan(
    Guid Id,
    OperationType Type,
    DateTimeOffset CreatedAt,
    int SchemaVersion,
    string IntegrityHash,
    IReadOnlyList<OperationPlanItem> Items)
{
    public const int CurrentSchemaVersion = 1;

    public static OperationPlan Create(OperationType type, IReadOnlyList<OperationPlanItem> items) =>
        new(Guid.NewGuid(), type, DateTimeOffset.UtcNow, CurrentSchemaVersion, ComputeIntegrityHash(items), items);

    public bool Verify() => IntegrityHash == ComputeIntegrityHash(Items);

    // Ordered by Identifier so hash computation doesn't depend on list order, and hashed over
    // normalized (not raw) intended paths so a Penumbra reload that reshuffles a transient
    // " (N)" duplicate-marker suffix can never spuriously invalidate a saved plan. See
    // PenumbraPathSemantics.Normalize and design doc section 3/6.
    public static string ComputeIntegrityHash(IReadOnlyList<OperationPlanItem> items)
    {
        var canonical = items
            .OrderBy(i => i.Identifier, StringComparer.Ordinal)
            .Select(i => $"{i.Identifier}{PenumbraPathSemantics.Normalize(i.IntendedRawPath, i.DisplayName)}");
        var bytes = Encoding.UTF8.GetBytes(string.Concat(canonical));
        return Convert.ToHexString(SHA256.HashData(bytes));
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
