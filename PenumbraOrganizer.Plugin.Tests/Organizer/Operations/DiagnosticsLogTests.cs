using PenumbraOrganizer.Plugin.Organizer.Operations;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Operations;

public class DiagnosticsLogTests
{
    private static DiagnosticEvent SlowCallEvent(Guid? operationId = null) => new(
        operationId, DiagnosticEventKind.SlowCall, DateTimeOffset.UtcNow,
        DurationMilliseconds: 75, ExceptionTypeName: null, ExceptionMessage: null, TruncatedStackTrace: null,
        Identifier: "mod-a");

    [Fact]
    public void ReadAll_FileDoesNotExist_ReturnsEmpty()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            Assert.Empty(DiagnosticsLog.ReadAll(Path.Combine(dir.FullName, "missing.jsonl")));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void AppendThenReadAll_SingleEvent_RoundTrips()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "diagnostics.jsonl");
            var evt = SlowCallEvent(Guid.NewGuid());

            DiagnosticsLog.Append(path, evt);
            var events = DiagnosticsLog.ReadAll(path);

            var single = Assert.Single(events);
            Assert.Equal(evt, single);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Append_NullOperationId_RoundTripsAsNull()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "diagnostics.jsonl");
            DiagnosticsLog.Append(path, SlowCallEvent(operationId: null));

            var single = Assert.Single(DiagnosticsLog.ReadAll(path));
            Assert.Null(single.OperationId);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Append_TruncatesStackTraceLongerThan2000Characters()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "diagnostics.jsonl");
            var longTrace = new string('x', 5000);
            var evt = new DiagnosticEvent(
                null, DiagnosticEventKind.Exception, DateTimeOffset.UtcNow,
                null, "System.InvalidOperationException", "boom", longTrace, null);

            DiagnosticsLog.Append(path, evt);
            var single = Assert.Single(DiagnosticsLog.ReadAll(path));

            Assert.NotNull(single.TruncatedStackTrace);
            Assert.True(single.TruncatedStackTrace!.Length <= 2000);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Append_DoesNotThrowWhenFileIsLocked()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "diagnostics.jsonl");
            File.WriteAllText(path, "");
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            var exception = Record.Exception(() => DiagnosticsLog.Append(path, SlowCallEvent()));

            Assert.Null(exception);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Append_BeyondRetentionCap_KeepsOnlyNewestEvents()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "diagnostics.jsonl");
            for (var i = 0; i < 2005; i++)
                DiagnosticsLog.Append(path, SlowCallEvent());

            var events = DiagnosticsLog.ReadAll(path);

            Assert.Equal(2000, events.Count);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ReadAll_ToleratesCorruptLine()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "diagnostics.jsonl");
            DiagnosticsLog.Append(path, SlowCallEvent());
            File.AppendAllText(path, "not valid json" + Environment.NewLine);

            var events = DiagnosticsLog.ReadAll(path);

            Assert.Single(events);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
