namespace PenumbraOrganizer.Plugin.Tests.Organizer.Templates;

using PenumbraOrganizer.Plugin.Organizer.Templates;

public class TemplateDuplicateResolverTests
{
    [Fact]
    public void Resolve_NoDuplicates_KeepsEveryEntryWithoutWarnings()
    {
        var result = TemplateDuplicateResolver.Resolve([
            new TemplateEntry("bibo+ medieval", "Gear/Top"),
            new TemplateEntry("some hair", "Hair"),
        ]);

        Assert.Equal(2, result.EntriesByNormalizedName.Count);
        Assert.Equal("Gear/Top", result.EntriesByNormalizedName["bibo+ medieval"]);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Resolve_DuplicatesAgreeingOnFolder_KeepsOneAndWarns()
    {
        var result = TemplateDuplicateResolver.Resolve([
            new TemplateEntry("bibo+ medieval", "Gear/Top"),
            new TemplateEntry("bibo+ medieval", "Gear/Top"),
        ]);

        Assert.Equal("Gear/Top", result.EntriesByNormalizedName["bibo+ medieval"]);
        Assert.Equal(
            [new TemplateWarning(TemplateWarningCode.DuplicateEntry, "bibo+ medieval")],
            result.Warnings);
    }

    // The whole group is dropped rather than picking one: keeping an arbitrary winner publishes a
    // silent choice between two genuinely different intents.
    [Fact]
    public void Resolve_DuplicatesDisagreeingOnFolder_KeepsNoneAndWarns()
    {
        var result = TemplateDuplicateResolver.Resolve([
            new TemplateEntry("bibo+ medieval", "Gear/Top"),
            new TemplateEntry("bibo+ medieval", "Characters/Nyx"),
        ]);

        Assert.False(result.EntriesByNormalizedName.ContainsKey("bibo+ medieval"));
        Assert.Equal(
            [new TemplateWarning(TemplateWarningCode.ConflictingDuplicateEntry, "bibo+ medieval")],
            result.Warnings);
    }

    // Array order must never decide meaning: reversing the input changes nothing.
    [Fact]
    public void Resolve_IsOrderIndependent()
    {
        TemplateEntry[] entries = [
            new("a", "Gear"),
            new("dup", "Gear/Top"),
            new("dup", "Characters"),
            new("b", "Hair"),
        ];

        var forward = TemplateDuplicateResolver.Resolve(entries);
        var reversed = TemplateDuplicateResolver.Resolve(entries.Reverse());

        Assert.Equal(
            forward.EntriesByNormalizedName.OrderBy(p => p.Key, StringComparer.Ordinal),
            reversed.EntriesByNormalizedName.OrderBy(p => p.Key, StringComparer.Ordinal));
        Assert.Equal(forward.Warnings, reversed.Warnings);
    }

    [Fact]
    public void Resolve_InvalidDestinationPath_SkipsEntryAndWarns()
    {
        var result = TemplateDuplicateResolver.Resolve([new TemplateEntry("bad", "Gear//Top")]);

        Assert.Empty(result.EntriesByNormalizedName);
        Assert.Equal(
            [new TemplateWarning(TemplateWarningCode.InvalidEntryPath, "bad")],
            result.Warnings);
    }

    // Entry keys are external input, not something the author's tool can be trusted to have done.
    [Fact]
    public void Resolve_UnnormalizedKey_IsRenormalized()
    {
        var result = TemplateDuplicateResolver.Resolve([
            new TemplateEntry("Bibo+ Medieval (Penumbra)_1_1_0", "Gear/Top"),
        ]);

        Assert.Equal("Gear/Top", result.EntriesByNormalizedName["bibo+ medieval"]);
    }

    // Re-normalization can itself create a collision; the same rule then applies to the result.
    [Fact]
    public void Resolve_RenormalizationCreatingConflict_DropsGroup()
    {
        var result = TemplateDuplicateResolver.Resolve([
            new TemplateEntry("Bibo+ Medieval_1_0", "Gear/Top"),
            new TemplateEntry("bibo+  medieval", "Characters/Nyx"),
        ]);

        Assert.Empty(result.EntriesByNormalizedName);
        Assert.Equal(
            [new TemplateWarning(TemplateWarningCode.ConflictingDuplicateEntry, "bibo+ medieval")],
            result.Warnings);
    }

    [Fact]
    public void Resolve_KeyNormalizingToEmpty_IsSkipped()
    {
        var result = TemplateDuplicateResolver.Resolve([new TemplateEntry("___", "Gear")]);

        Assert.Empty(result.EntriesByNormalizedName);
        Assert.Equal(
            [new TemplateWarning(TemplateWarningCode.InvalidEntryPath, "___")],
            result.Warnings);
    }
}
