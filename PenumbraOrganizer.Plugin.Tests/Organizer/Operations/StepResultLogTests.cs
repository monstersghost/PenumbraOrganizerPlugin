using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class StepResultLogTests
{
    private static OperationStepResult Sample(int stepIndex, string identifier, OperationStepDisposition disposition) => new(
        stepIndex, identifier, disposition,
        IpcResultName: disposition == OperationStepDisposition.Succeeded ? "Success" : "PathRenameFailed",
        FailureDetail: disposition == OperationStepDisposition.Succeeded ? null : "collision",
        RecordedAt: DateTimeOffset.UtcNow,
        DurationMilliseconds: disposition == OperationStepDisposition.Succeeded ? 5 : null);

    [Fact]
    public void ReadAll_FileDoesNotExist_ReturnsEmpty()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var result = StepResultLog.ReadAll(Path.Combine(dir.FullName, "missing.jsonl"));
            Assert.Empty(result);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void AppendThenReadAll_SingleResult_RoundTrips()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "results.jsonl");
            var sample = Sample(0, "mod-a", OperationStepDisposition.Succeeded);

            StepResultLog.Append(path, sample);
            var results = StepResultLog.ReadAll(path);

            var single = Assert.Single(results);
            Assert.Equal(sample, single);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void AppendMultipleTimes_ReadAll_ReturnsAllInAppendOrder()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "results.jsonl");
            StepResultLog.Append(path, Sample(0, "mod-a", OperationStepDisposition.Succeeded));
            StepResultLog.Append(path, Sample(1, "mod-b", OperationStepDisposition.Failed));
            StepResultLog.Append(path, Sample(2, "mod-c", OperationStepDisposition.SkippedAfterEarlierFailure));

            var results = StepResultLog.ReadAll(path);

            Assert.Equal([0, 1, 2], results.Select(r => r.StepIndex));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Append_CreatesDestinationDirectoryIfMissing()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "nested", "results.jsonl");
            StepResultLog.Append(path, Sample(0, "mod-a", OperationStepDisposition.Succeeded));

            Assert.True(File.Exists(path));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ReadAll_ToleratesTruncatedTrailingLine()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "results.jsonl");
            StepResultLog.Append(path, Sample(0, "mod-a", OperationStepDisposition.Succeeded));
            // Simulate a crash mid-write of the second line: a truncated JSON fragment, no closing brace.
            File.AppendAllText(path, "{\"StepIndex\":1,\"Identifier\":\"mod-b\",\"Disposi");

            var results = StepResultLog.ReadAll(path);

            var single = Assert.Single(results);
            Assert.Equal(0, single.StepIndex);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ReadAll_ToleratesCorruptMiddleLine()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "results.jsonl");
            StepResultLog.Append(path, Sample(0, "mod-a", OperationStepDisposition.Succeeded));
            File.AppendAllText(path, "not valid json at all" + Environment.NewLine);
            StepResultLog.Append(path, Sample(2, "mod-c", OperationStepDisposition.Succeeded));

            var results = StepResultLog.ReadAll(path);

            Assert.Equal([0, 2], results.Select(r => r.StepIndex));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Append_WritesDispositionAsAString()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "results.jsonl");
            StepResultLog.Append(path, Sample(0, "mod-a", OperationStepDisposition.SkippedAfterEarlierFailure));

            var text = File.ReadAllText(path);
            Assert.Contains("\"SkippedAfterEarlierFailure\"", text);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
