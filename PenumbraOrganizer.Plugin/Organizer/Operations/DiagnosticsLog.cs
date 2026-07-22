using System.Text.Json;
using System.Text.Json.Serialization;

namespace PenumbraOrganizer.Plugin.Organizer.Operations;

public enum DiagnosticEventKind { SlowCall, SlowLiveSnapshot, Exception }

/// <summary> One diagnostic event. OperationId is null for events outside any active operation.
/// Exception* fields are populated only for Kind == Exception. Design doc section 10. </summary>
public sealed record DiagnosticEvent(
    Guid? OperationId,
    DiagnosticEventKind Kind,
    DateTimeOffset RecordedAt,
    long? DurationMilliseconds,
    string? ExceptionTypeName,
    string? ExceptionMessage,
    string? TruncatedStackTrace);

/// <summary>
/// Global (not per-operation) append-only diagnostics.jsonl - the durable source a future
/// diagnostics dump reads from. Append never throws: a diagnostics write failure must not become a
/// new failure mode for the operation that triggered it (design doc section 10). This class has no
/// Dalamud dependency and does not itself log a swallowed failure anywhere - a higher-level sink
/// built in a later plan wraps this and is responsible for the "log to the ordinary Dalamud log as a
/// fallback" behavior the design doc describes.
/// </summary>
public static class DiagnosticsLog
{
    private const int MaxRetainedEvents = 2000;
    private const int MaxStackTraceLength = 2000;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static void Append(string path, DiagnosticEvent evt)
    {
        try
        {
            if (evt.TruncatedStackTrace is { Length: > MaxStackTraceLength } trace)
                evt = evt with { TruncatedStackTrace = trace[..MaxStackTraceLength] };

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var line = JsonSerializer.Serialize(evt, SerializerOptions);
            using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
            using (var writer = new StreamWriter(stream))
            {
                writer.WriteLine(line);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            TrimIfOverCap(path);
        }
        catch (Exception)
        {
            // Diagnostics existing to explain failures must not become a new failure mode itself.
        }
    }

    private static void TrimIfOverCap(string path)
    {
        var lines = File.ReadAllLines(path);
        if (lines.Length <= MaxRetainedEvents)
            return;

        var newest = lines[^MaxRetainedEvents..];
        AtomicFile.CreateOrReplace(path, string.Join(Environment.NewLine, newest) + Environment.NewLine);
    }

    public static IReadOnlyList<DiagnosticEvent> ReadAll(string path)
    {
        if (!File.Exists(path))
            return [];

        var events = new List<DiagnosticEvent>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            DiagnosticEvent? evt;
            try
            {
                evt = JsonSerializer.Deserialize<DiagnosticEvent>(line, SerializerOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (evt is not null)
                events.Add(evt);
        }

        return events;
    }
}
