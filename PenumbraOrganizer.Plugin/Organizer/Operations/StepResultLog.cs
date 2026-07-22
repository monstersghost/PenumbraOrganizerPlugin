using System.Text.Json;
using System.Text.Json.Serialization;

namespace PenumbraOrganizer.Plugin.Organizer.Operations;

public enum OperationStepDisposition { Succeeded, Failed, SkippedAfterEarlierFailure, SkippedAlreadySatisfied }

/// <summary> One execution step's durable outcome. IpcResultName/DurationMilliseconds are null for
/// skipped dispositions - no IPC call was ever attempted for those. Design doc section 5a. </summary>
public sealed record OperationStepResult(
    int StepIndex,
    string Identifier,
    OperationStepDisposition Disposition,
    string? IpcResultName,
    string? FailureDetail,
    DateTimeOffset RecordedAt,
    long? DurationMilliseconds);

/// <summary>
/// Append-only results.jsonl - one JSON object per line. Never rewrites the whole file on write
/// (that cost grows with operation size, which is exactly what this design avoids); a corrupt or
/// truncated line anywhere in the file (not just the last one) is skipped on read, never fails the
/// whole read. Design doc section 5a.
/// </summary>
public static class StepResultLog
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static void Append(string path, OperationStepResult result)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var line = JsonSerializer.Serialize(result, SerializerOptions);
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream);
        writer.WriteLine(line);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    public static IReadOnlyList<OperationStepResult> ReadAll(string path)
    {
        if (!File.Exists(path))
            return [];

        var results = new List<OperationStepResult>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            OperationStepResult? result;
            try
            {
                result = JsonSerializer.Deserialize<OperationStepResult>(line, SerializerOptions);
            }
            catch (JsonException)
            {
                continue; // corrupt or truncated line - skip, don't fail the whole read
            }

            if (result is not null)
                results.Add(result);
        }

        return results;
    }
}
