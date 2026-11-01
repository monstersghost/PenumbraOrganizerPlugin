using PenumbraOrganizer.Plugin.Organizer.Classification;
using Xunit;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Classification;

public class HairPriorityResolverTests
{
    private static IReadOnlySet<int> Set(params int[] numbers) => numbers.ToHashSet();

    private static IReadOnlyDictionary<string, IReadOnlySet<int>> Races(params (string Code, int[] Numbers)[] races) =>
        races.ToDictionary(r => r.Code, r => Set(r.Numbers), StringComparer.Ordinal);

    private static readonly HairPriority None = new(null, false);
    private static readonly HairPriority Fallback = new(null, true);

    // Devilry Hair With Bow: 108 for most races, Viera female uses 3.
    private static readonly IReadOnlySet<int> DevilryFlat = Set(3, 108);
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<int>> DevilryByRace =
        Races(("0201", [108]), ("0401", [108]), ("0801", [108]), ("1401", [108]), ("1801", [3]));

    // Zutail: female Midlander has 147 and 169; most other races have 169 only.
    private static readonly IReadOnlySet<int> ZutailFlat = Set(147, 169);
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<int>> ZutailByRace =
        Races(("0201", [147, 169]), ("0401", [169]), ("0601", [169]), ("0801", [169]),
              ("1001", [169]), ("1201", [169]), ("1401", [147, 169]));

    [Fact]
    public void Step1_PriorityWithOneNumber_FilesUnderIt()
    {
        // Devilry, priority Viera female: c1801 has exactly one number, 3.
        Assert.Equal(3, HairPriorityResolver.Resolve(DevilryFlat, DevilryByRace, new("1801", false)));
    }

    [Fact]
    public void Step1_PriorityWithOneNumber_IgnoresTheOtherRaces()
    {
        // Devilry, priority Midlander female: c0201 has one number, 108, even though the mod has two.
        Assert.Equal(108, HairPriorityResolver.Resolve(DevilryFlat, DevilryByRace, new("0201", false)));
    }

    [Fact]
    public void Step2_PriorityAbsent_FallsThroughToDetection()
    {
        // Zutail, priority Viera female: no c1801 folder, so detection gives 147 and 169, flat.
        Assert.Null(HairPriorityResolver.Resolve(ZutailFlat, ZutailByRace, new("1801", false)));
    }

    [Fact]
    public void Step2_PriorityWithTwoNumbers_FallsThroughToDetection()
    {
        // Zutail, priority Au Ra female: c1401 has 147 and 169, not exactly one, so flat.
        Assert.Null(HairPriorityResolver.Resolve(ZutailFlat, ZutailByRace, new("1401", false)));
    }

    [Fact]
    public void Step3_NoPriority_OneDetectedNumber_FilesUnderIt()
    {
        // Devilry has two numbers, so use a single-number mod for this row.
        var flat = Set(115);
        var byRace = Races(("0201", [115]), ("0801", [115]));
        Assert.Equal(115, HairPriorityResolver.Resolve(flat, byRace, None));
    }

    [Fact]
    public void Step5_NoPriority_TwoDetectedNumbers_StaysFlat()
    {
        Assert.Null(HairPriorityResolver.Resolve(DevilryFlat, DevilryByRace, None));
    }

    [Fact]
    public void Step4_MidlanderFallback_FemaleOnlyWithOneNumber_Resolves()
    {
        // Two detected numbers, no priority, only Midlander female present with one number.
        var flat = Set(108, 3);
        var byRace = Races(("0201", [108]), ("1801", [3]));
        Assert.Equal(108, HairPriorityResolver.Resolve(flat, byRace, Fallback));
    }

    [Fact]
    public void Step5_MidlanderFallbackOff_TwoDetectedNumbers_StaysFlat()
    {
        var flat = Set(108, 3);
        var byRace = Races(("0201", [108]), ("1801", [3]));
        Assert.Null(HairPriorityResolver.Resolve(flat, byRace, None));
    }

    [Fact]
    public void Midlander_BothGendersSameSingleNumber_Resolves()
    {
        var flat = Set(111, 3);
        var byRace = Races(("0101", [111]), ("0201", [111]), ("1801", [3]));
        Assert.Equal(111, HairPriorityResolver.Resolve(flat, byRace, Fallback));
    }

    [Fact]
    public void Midlander_BothGendersDifferent_StaysFlat()
    {
        // Lumi's Hairstyle V2 shape: male 111, female 110.
        var flat = Set(110, 111);
        var byRace = Races(("0101", [111]), ("0201", [110]));
        Assert.Null(HairPriorityResolver.Resolve(flat, byRace, Fallback));
    }

    [Fact]
    public void Midlander_FemaleOnlyWithTwoNumbers_StaysFlat()
    {
        // Zutail's Midlander female shape: 147 and 169, no male Midlander.
        Assert.Null(HairPriorityResolver.Resolve(ZutailFlat, ZutailByRace, Fallback));
    }

    [Fact]
    public void Midlander_NoMidlanderEntries_FallbackDoesNotApply()
    {
        var flat = Set(108, 3);
        var byRace = Races(("0401", [108]), ("1801", [3]));
        Assert.Null(HairPriorityResolver.Resolve(flat, byRace, Fallback));
    }

    [Fact]
    public void Rule6_TwoNumbersWithinOneRace_NeverPicksOne()
    {
        // Sampo's Midlander male shape: four numbers in c0101. Priority on it is "not exactly one",
        // and with no other detection evidence the answer is flat, not an arbitrary member.
        var flat = Set(122, 162, 163, 164);
        var byRace = Races(("0101", [122, 162, 163, 164]));
        Assert.Null(HairPriorityResolver.Resolve(flat, byRace, new("0101", true)));
    }

    [Fact]
    public void UnreadableEvidence_IsNeverGuessedFrom()
    {
        Assert.Null(HairPriorityResolver.Resolve(null, null, new("1801", true)));
    }

    [Fact]
    public void Zero_DetectedNumbers_StaysFlat()
    {
        Assert.Null(HairPriorityResolver.Resolve(Set(), Races(), new("1801", true)));
    }
}
