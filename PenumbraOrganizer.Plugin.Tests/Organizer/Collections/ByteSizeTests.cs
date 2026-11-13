using PenumbraOrganizer.Plugin.Organizer.Collections;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Collections;

public class ByteSizeTests
{
    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1.0 KB")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(1048576L, "1.0 MB")]
    [InlineData(5L * 1024 * 1024 * 1024, "5.0 GB")]
    [InlineData(-5L, "0 B")]
    public void Formats(long bytes, string expected) => Assert.Equal(expected, ByteSize.Format(bytes));
}
