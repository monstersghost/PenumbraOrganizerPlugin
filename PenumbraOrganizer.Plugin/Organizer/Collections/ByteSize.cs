using System.Globalization;

namespace PenumbraOrganizer.Plugin.Organizer.Collections;

public static class ByteSize
{
    private static readonly string[] Units = { "KB", "MB", "GB", "TB" };

    public static string Format(long bytes)
    {
        if (bytes <= 0)
            return "0 B";
        if (bytes < 1024)
            return bytes.ToString(CultureInfo.InvariantCulture) + " B";

        double value = bytes;
        var unit = -1;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return value.ToString("0.0", CultureInfo.InvariantCulture) + " " + Units[unit];
    }
}
