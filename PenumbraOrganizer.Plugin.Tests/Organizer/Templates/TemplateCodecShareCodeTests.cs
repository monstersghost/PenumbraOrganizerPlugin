namespace PenumbraOrganizer.Plugin.Tests.Organizer.Templates;

using System.IO.Compression;
using System.Text;
using PenumbraOrganizer.Plugin.Organizer.Templates;

public class TemplateCodecShareCodeTests
{
    private static OrganizationTemplate SampleTemplate() => new()
    {
        FormatVersion = 1,
        Name = "Detailed type sort",
        FallbackStrategy = "TypeThenCreator",
        Folders = ["Gear/Top"],
        Entries = [new TemplateEntry("bibo+ medieval", "Gear/Top")],
    };

    [Fact]
    public void EncodeShareCode_StartsWithPrefix()
    {
        Assert.StartsWith("POT1:", TemplateCodec.EncodeShareCode(SampleTemplate()));
    }

    [Fact]
    public void EncodeThenDecodeShareCode_RoundTrips()
    {
        var result = TemplateCodec.DecodeShareCode(TemplateCodec.EncodeShareCode(SampleTemplate()));

        Assert.True(result.Succeeded);
        Assert.Equal("Detailed type sort", result.Template!.Name);
        Assert.Equal("Gear/Top", result.Template.EntriesByNormalizedName["bibo+ medieval"]);
    }

    [Fact]
    public void DecodeShareCode_SurroundingWhitespace_IsTolerated()
    {
        var code = TemplateCodec.EncodeShareCode(SampleTemplate());

        Assert.True(TemplateCodec.DecodeShareCode($"  {code}\n").Succeeded);
    }

    [Fact]
    public void DecodeShareCode_MissingPrefix_Fails()
    {
        var result = TemplateCodec.DecodeShareCode("bm90aGluZw==");

        Assert.False(result.Succeeded);
        Assert.Equal(TemplateDecodeError.MissingPrefix, result.Error);
    }

    [Fact]
    public void DecodeShareCode_InvalidBase64_Fails()
    {
        var result = TemplateCodec.DecodeShareCode("POT1:!!!not base64!!!");

        Assert.False(result.Succeeded);
        Assert.Equal(TemplateDecodeError.InvalidBase64, result.Error);
    }

    [Fact]
    public void DecodeShareCode_ValidBase64ThatIsNotDeflate_Fails()
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("this is not a deflate stream"));

        var result = TemplateCodec.DecodeShareCode("POT1:" + payload);

        Assert.False(result.Succeeded);
        Assert.Equal(TemplateDecodeError.InvalidDeflate, result.Error);
    }

    [Fact]
    public void DecodeShareCode_CompressedInputOverLimit_Fails()
    {
        var oversize = Convert.ToBase64String(new byte[TemplateLimits.MaxCompressedBytes + 1]);

        var result = TemplateCodec.DecodeShareCode("POT1:" + oversize);

        Assert.False(result.Succeeded);
        Assert.Equal(TemplateDecodeError.PayloadTooLarge, result.Error);
    }

    // A small compressed payload can inflate to something enormous, so the cap must be enforced
    // DURING inflation rather than after it.
    [Fact]
    public void DecodeShareCode_ZipBomb_FailsWithoutAllocatingTheWholePayload()
    {
        using var buffer = new MemoryStream();
        using (var deflate = new DeflateStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var chunk = new byte[64 * 1024];
            for (var i = 0; i < 256; i++)
                deflate.Write(chunk, 0, chunk.Length);
        }

        var code = "POT1:" + Convert.ToBase64String(buffer.ToArray());
        var result = TemplateCodec.DecodeShareCode(code);

        Assert.False(result.Succeeded);
        Assert.Equal(TemplateDecodeError.PayloadTooLarge, result.Error);
    }

    [Fact]
    public void DecodeShareCode_ValidTransportButBadDocument_ReportsDocumentError()
    {
        var badDocument = new OrganizationTemplate
        {
            FormatVersion = 99,
            Name = "x",
            FallbackStrategy = "TypeOnly",
        };

        var result = TemplateCodec.DecodeShareCode(TemplateCodec.EncodeShareCode(badDocument));

        Assert.False(result.Succeeded);
        Assert.Equal(TemplateDecodeError.UnsupportedFormatVersion, result.Error);
    }
}
