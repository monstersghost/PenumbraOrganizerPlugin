using System.Text.Json;
using System.Text.Json.Serialization;

namespace PenumbraOrganizer.Plugin.Organizer;

public enum OrganizationJsonParseStatus
{
    Ok,
    MalformedJson,
    UnsupportedVersion,
}

public sealed record OrganizationJsonParseResult(OrganizationJson? Data, OrganizationJsonParseStatus Status);

public static class OrganizationJsonCodec
{
    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    // Never throws. Data is non-null exactly when Status == Ok. MalformedJson and
    // UnsupportedVersion stay distinct because the UI reports them as different states.
    public static OrganizationJsonParseResult Parse(string json)
    {
        if (json is null)
            return new OrganizationJsonParseResult(null, OrganizationJsonParseStatus.MalformedJson);

        OrganizationJson? data;
        try
        {
            data = JsonSerializer.Deserialize<OrganizationJson>(json);
        }
        catch (JsonException)
        {
            return new OrganizationJsonParseResult(null, OrganizationJsonParseStatus.MalformedJson);
        }

        if (data is null)
            return new OrganizationJsonParseResult(null, OrganizationJsonParseStatus.MalformedJson);
        if (data.Version != 1)
            return new OrganizationJsonParseResult(null, OrganizationJsonParseStatus.UnsupportedVersion);

        return new OrganizationJsonParseResult(data, OrganizationJsonParseStatus.Ok);
    }

    public static string Serialize(OrganizationJson data) =>
        JsonSerializer.Serialize(data, SerializeOptions);
}
