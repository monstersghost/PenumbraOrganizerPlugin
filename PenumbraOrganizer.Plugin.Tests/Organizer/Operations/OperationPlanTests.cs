using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class OperationPlanTests
{
    private static readonly OperationPlanItem[] SampleItems =
    [
        new("mod-a", "Gear\\A", "Weapons\\A", "A"),
        new("mod-b", "Gear\\B", "Weapons\\B", "B"),
    ];

    [Fact]
    public void Create_ProducesAPlanThatVerifiesSuccessfully()
    {
        var plan = OperationPlan.Create(OperationType.Apply, SampleItems);

        Assert.True(plan.Verify());
        Assert.Equal(1, plan.SchemaVersion);
        Assert.Equal(2, plan.Items.Count);
    }

    [Fact]
    public void ComputeIntegrityHash_IsOrderIndependent()
    {
        var forward = OperationPlan.ComputeIntegrityHash(SampleItems);
        var reversed = OperationPlan.ComputeIntegrityHash(SampleItems.Reverse().ToList());

        Assert.Equal(forward, reversed);
    }

    [Fact]
    public void ComputeIntegrityHash_ChangesWhenAnIntendedPathChanges()
    {
        var original = OperationPlan.ComputeIntegrityHash(SampleItems);
        var mutated = new[]
        {
            SampleItems[0] with { IntendedRawPath = "Weapons\\Different" },
            SampleItems[1],
        };

        var mutatedHash = OperationPlan.ComputeIntegrityHash(mutated);

        Assert.NotEqual(original, mutatedHash);
    }

    [Fact]
    public void ComputeIntegrityHash_IsUnchangedByPenumbraDuplicateMarkerReshuffling()
    {
        // "A" and "A (3)" are the same persisted location per PenumbraPathSemantics when the
        // duplicate-marker base equals the mod's own display name — the hash must not care.
        var withoutMarker = new[] { SampleItems[0], SampleItems[1] };
        var withMarker = new[]
        {
            SampleItems[0] with { IntendedRawPath = "Weapons\\A (3)" },
            SampleItems[1],
        };

        Assert.Equal(
            OperationPlan.ComputeIntegrityHash(withoutMarker),
            OperationPlan.ComputeIntegrityHash(withMarker));
    }

    [Fact]
    public void SaveThenTryLoad_RoundTripsAndVerifies()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "plan.json");
            var plan = OperationPlan.Create(OperationType.Restore, SampleItems);

            OperationPlanCodec.Save(path, plan);
            var loaded = OperationPlanCodec.TryLoad(path, out var result);

            Assert.True(loaded);
            Assert.NotNull(result);
            Assert.Equal(plan.Id, result!.Id);
            Assert.Equal(plan.IntegrityHash, result.IntegrityHash);
            Assert.True(result.Verify());
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void TryLoad_ReturnsFalseWhenFileMissing()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var loaded = OperationPlanCodec.TryLoad(Path.Combine(dir.FullName, "missing.json"), out var result);

            Assert.False(loaded);
            Assert.Null(result);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void TryLoad_ReturnsFalseWhenIntegrityHashHasBeenTampered()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "plan.json");
            var plan = OperationPlan.Create(OperationType.Apply, SampleItems);
            OperationPlanCodec.Save(path, plan);

            var tamperedJson = File.ReadAllText(path).Replace(plan.IntegrityHash, "tampered-hash-value");
            File.WriteAllText(path, tamperedJson);

            var loaded = OperationPlanCodec.TryLoad(path, out var result);

            Assert.False(loaded);
            Assert.Null(result);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
