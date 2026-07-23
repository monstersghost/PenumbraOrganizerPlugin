using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class FileDiagnosticsSinkTests
{
    [Fact]
    public void RecordSlowCall_AppendsASlowCallEventWithTheIdentifier()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "diagnostics.jsonl");
            var operationId = Guid.NewGuid();
            var sink = new FileDiagnosticsSink(path);

            sink.RecordSlowCall(operationId, "mod-a", TimeSpan.FromMilliseconds(75));

            var events = DiagnosticsLog.ReadAll(path);
            var single = Assert.Single(events);
            Assert.Equal(DiagnosticEventKind.SlowCall, single.Kind);
            Assert.Equal(operationId, single.OperationId);
            Assert.Equal(75, single.DurationMilliseconds);
            Assert.Equal("mod-a", single.Identifier);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void RecordSlowLiveSnapshot_AppendsAnEventWithNoIdentifier()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "diagnostics.jsonl");
            var sink = new FileDiagnosticsSink(path);

            sink.RecordSlowLiveSnapshot(null, TimeSpan.FromMilliseconds(120));

            var single = Assert.Single(DiagnosticsLog.ReadAll(path));
            Assert.Equal(DiagnosticEventKind.SlowLiveSnapshot, single.Kind);
            Assert.Null(single.OperationId);
            Assert.Null(single.Identifier);
            Assert.Equal(120, single.DurationMilliseconds);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void RecordSlowRefresh_AppendsASlowRefreshEvent()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "diagnostics.jsonl");
            var operationId = Guid.NewGuid();
            var sink = new FileDiagnosticsSink(path);

            sink.RecordSlowRefresh(operationId, TimeSpan.FromMilliseconds(90));

            var single = Assert.Single(DiagnosticsLog.ReadAll(path));
            Assert.Equal(DiagnosticEventKind.SlowRefresh, single.Kind);
            Assert.Equal(90, single.DurationMilliseconds);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
