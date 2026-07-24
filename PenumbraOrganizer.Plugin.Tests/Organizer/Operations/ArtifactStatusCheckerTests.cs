using PenumbraOrganizer.Plugin.Organizer;
using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class ArtifactStatusCheckerTests
{
    [Fact]
    public void CheckPlan_FileMissing_ReturnsMissing()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var (status, plan) = ArtifactStatusChecker.CheckPlan(dir.FullName);

            Assert.Equal(ArtifactCheckStatus.Missing, status);
            Assert.Null(plan);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void CheckPlan_FileCorrupt_ReturnsInvalid()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            File.WriteAllText(OperationBundlePaths.PlanPath(dir.FullName), "{ not valid json");

            var (status, plan) = ArtifactStatusChecker.CheckPlan(dir.FullName);

            Assert.Equal(ArtifactCheckStatus.Invalid, status);
            Assert.Null(plan);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void CheckPlan_FileValid_ReturnsValidWithParsedPlan()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var plan = OperationPlan.Create(
                OperationType.Apply, [new(0, "mod-a", "Weapons/A", OperationStepKind.FinalMove, 0)],
                [new("mod-a", "Gear/A", "Weapons/A", "mod-a")]);
            OperationPlanCodec.Save(OperationBundlePaths.PlanPath(dir.FullName), plan);

            var (status, loaded) = ArtifactStatusChecker.CheckPlan(dir.FullName);

            Assert.Equal(ArtifactCheckStatus.Valid, status);
            Assert.NotNull(loaded);
            Assert.Equal(plan.OperationId, loaded!.OperationId);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void CheckSnapshot_FileMissing_ReturnsMissing()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var (status, snapshot) = ArtifactStatusChecker.CheckSnapshot(dir.FullName);

            Assert.Equal(ArtifactCheckStatus.Missing, status);
            Assert.Null(snapshot);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void CheckSnapshot_FileCorrupt_ReturnsInvalid()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            File.WriteAllText(OperationBundlePaths.SnapshotPath(dir.FullName), "{ not valid json");

            var (status, snapshot) = ArtifactStatusChecker.CheckSnapshot(dir.FullName);

            Assert.Equal(ArtifactCheckStatus.Invalid, status);
            Assert.Null(snapshot);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void CheckSnapshot_FileValid_ReturnsValidWithParsedSnapshot()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var snapshot = new RollbackSnapshot(
                Guid.NewGuid(), DateTimeOffset.UtcNow, "label", "auto",
                new Dictionary<string, string> { ["mod-a"] = "Weapons/A" });
            OperationSnapshotCodec.Save(OperationBundlePaths.SnapshotPath(dir.FullName), snapshot);

            var (status, loaded) = ArtifactStatusChecker.CheckSnapshot(dir.FullName);

            Assert.Equal(ArtifactCheckStatus.Valid, status);
            Assert.NotNull(loaded);
            Assert.Equal(snapshot.Id, loaded!.Id);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
