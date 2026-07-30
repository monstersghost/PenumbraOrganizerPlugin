namespace PenumbraOrganizer.Plugin.Tests.Organizer.Templates;

using PenumbraOrganizer.Plugin.Organizer.Templates;

public class TemplateSlugTests
{
    [Theory]
    [InlineData("Detailed type sort", "detailed-type-sort")]
    [InlineData("Akako's layout", "akakos-layout")]
    [InlineData("  spaced  out  ", "spaced-out")]
    [InlineData("UPPER Case", "upper-case")]
    [InlineData("with_underscores", "with_underscores")]
    public void From_OrdinaryNames_ProduceReadableSlugs(string name, string expected)
    {
        Assert.Equal(expected, TemplateSlug.From(name));
    }

    // A template name is untrusted: it arrives inside a document a stranger published. None of
    // these may produce a path that escapes the templates folder or names a device.
    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("C:\\Windows\\System32")]
    [InlineData("a/b\\c")]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("PRN")]
    [InlineData("AUX")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    [InlineData("LPT9")]
    [InlineData("trailing dots...")]
    [InlineData("trailing space ")]
    public void From_HostileNames_ProduceSafeSingleSegmentSlugs(string name)
    {
        var slug = TemplateSlug.From(name);

        Assert.NotEmpty(slug);
        Assert.DoesNotContain('/', slug);
        Assert.DoesNotContain('\\', slug);
        Assert.DoesNotContain(':', slug);
        Assert.False(slug is "." or "..");
        Assert.DoesNotContain("..", slug);
        Assert.False(slug.EndsWith('.') || slug.EndsWith(' '));
        Assert.DoesNotContain(slug, Path.GetInvalidFileNameChars().Select(c => c.ToString()));
        Assert.Equal(slug, Path.GetFileName(slug));
    }

    // Reserved device names are only dangerous as the whole stem, so they are suffixed rather
    // than stripped -- "console-tweaks" must not be mangled.
    [Fact]
    public void From_ReservedDeviceName_IsSuffixedNotStripped()
    {
        Assert.Equal("con-template", TemplateSlug.From("CON"));
        Assert.Equal("console-tweaks", TemplateSlug.From("Console Tweaks"));
    }

    [Fact]
    public void From_NameWithNothingUsable_FallsBackToAConstant()
    {
        Assert.Equal("template", TemplateSlug.From("///"));
        Assert.Equal("template", TemplateSlug.From(""));
    }

    [Fact]
    public void From_VeryLongName_IsTruncated()
    {
        var slug = TemplateSlug.From(new string('a', 500));

        Assert.True(slug.Length <= 64, $"Slug was {slug.Length} chars.");
    }

    [Fact]
    public void MakeUnique_UntakenSlug_IsUnchanged()
    {
        Assert.Equal("layout", TemplateSlug.MakeUnique("layout", new HashSet<string>()));
    }

    // Two templates may legitimately share a display name; importing one must never overwrite
    // a template already on disk.
    [Fact]
    public void MakeUnique_TakenSlug_GetsNumericSuffix()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "layout", "layout-2" };

        Assert.Equal("layout-3", TemplateSlug.MakeUnique("layout", taken));
    }

    [Fact]
    public void MakeUnique_IsCaseInsensitive()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Layout" };

        Assert.Equal("layout-2", TemplateSlug.MakeUnique("layout", taken));
    }
}
