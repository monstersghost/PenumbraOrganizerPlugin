namespace PenumbraOrganizer.Plugin.Tests.Organizer.Classification;

using System.Text.Json;
using PenumbraOrganizer.Core.Classification;
using PenumbraOrganizer.Plugin.Organizer.Classification;

public class ModEquipmentFileReaderTests
{
    private static DirectoryInfo MakeTempModDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "PenumbraOrganizer.Plugin.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return new DirectoryInfo(path);
    }

    private static void WriteJson(DirectoryInfo modDirectory, string fileName, string json) =>
        File.WriteAllText(Path.Combine(modDirectory.FullName, fileName), json);

    [Fact]
    public void ReadEquipmentSlots_SingleSlotFromFiles_ResolvesFeet()
    {
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", """
            {"Files":{"chara/equipment/e0387/model/c0101e0387_sho.mdl":"files/sho.mdl"},"Manipulations":[]}
            """);

        var result = ModEquipmentFileReader.ReadEquipmentSlots(mod);

        Assert.NotNull(result);
        Assert.Equal([EquipmentSlot.Feet], result);
    }

    [Fact]
    public void ReadEquipmentSlots_SingleSlotFromEqpManipulation_ResolvesTop()
    {
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", """
            {"Files":{},"Manipulations":[{"Type":"Eqp","Manipulation":{"Entry":16129,"SetId":6040,"Slot":"Body"}}]}
            """);

        var result = ModEquipmentFileReader.ReadEquipmentSlots(mod);

        Assert.NotNull(result);
        Assert.Equal([EquipmentSlot.Top], result);
    }

    [Fact]
    public void ReadEquipmentSlots_SingleSlotFromImcManipulation_ResolvesFeet()
    {
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", """
            {"Files":{},"Manipulations":[{"Type":"Imc","Manipulation":{"PrimaryId":227,"Variant":0,"EquipSlot":"Feet","BodySlot":"Unknown"}}]}
            """);

        var result = ModEquipmentFileReader.ReadEquipmentSlots(mod);

        Assert.NotNull(result);
        Assert.Equal([EquipmentSlot.Feet], result);
    }

    [Fact]
    public void ReadEquipmentSlots_EstManipulation_ContributesNothing()
    {
        // Est manipulations have a "Slot" too, but it means a customization slot (Hair/Face),
        // not equipment — must be excluded by the Type filter, not vocabulary non-overlap.
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", """
            {"Files":{},"Manipulations":[{"Type":"Est","Manipulation":{"Entry":161,"Gender":"Female","Race":"Miqote","SetId":157,"Slot":"Hair"}}]}
            """);

        var result = ModEquipmentFileReader.ReadEquipmentSlots(mod);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void ReadEquipmentSlots_MultipleGroupsDifferentSlots_ResolvesBothDistinctSlots()
    {
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", "{}");
        WriteJson(mod, "group_001_top.json", """
            {"Options":[{"Files":{"chara/equipment/e0686/model/c0201e0686_top.mdl":"x"},"Manipulations":[]}]}
            """);
        WriteJson(mod, "group_002_legs.json", """
            {"Options":[{"Files":{"chara/equipment/e0686/model/c0201e0686_dwn.mdl":"x"},"Manipulations":[]}]}
            """);

        var result = ModEquipmentFileReader.ReadEquipmentSlots(mod);

        Assert.NotNull(result);
        Assert.Equal(2, result!.Count);
        Assert.Contains(EquipmentSlot.Top, result);
        Assert.Contains(EquipmentSlot.Legs, result);
    }

    [Fact]
    public void ReadEquipmentSlots_MissingDirectory_ReturnsEmptySetNotNull()
    {
        var missingDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "PenumbraOrganizer.Plugin.Tests", "does-not-exist-" + Guid.NewGuid().ToString("N")));

        var result = ModEquipmentFileReader.ReadEquipmentSlots(missingDir);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void ReadEquipmentSlots_NoConfigFiles_ReturnsEmptySetNotNull()
    {
        var mod = MakeTempModDirectory(); // directory exists, but no default_mod.json/group_*.json

        var result = ModEquipmentFileReader.ReadEquipmentSlots(mod);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void ReadEquipmentSlots_OneMalformedFileAmongValidOnes_ReturnsNullNotPartialResult()
    {
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", """
            {"Files":{"chara/equipment/e0387/model/c0101e0387_sho.mdl":"x"},"Manipulations":[]}
            """);
        WriteJson(mod, "group_001_broken.json", "{ not valid json");

        var result = ModEquipmentFileReader.ReadEquipmentSlots(mod);

        Assert.Null(result); // the fail-closed fix: NOT a set containing only Feet
    }

    [Fact]
    public void ReadEquipmentSlots_NonEquipmentPath_ContributesNothing()
    {
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", """
            {"Files":{"chara/human/c0101/obj/face/f0001/model/c0101f0001_fac.mdl":"x"},"Manipulations":[]}
            """);

        var result = ModEquipmentFileReader.ReadEquipmentSlots(mod);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void ReadEquipmentSlots_ManipulationMissingTypeOrManipulationField_IgnoredNotCrash()
    {
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", """
            {"Files":{},"Manipulations":[{"Slot":"Body"},{"Type":"Eqp"}]}
            """);

        var result = ModEquipmentFileReader.ReadEquipmentSlots(mod);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void ReadEquipmentSlots_NestedOptionsWithinContainers_TraversalIsGenuinelyRecursive()
    {
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", """
            {"Containers":[{"Options":[{"Files":{"chara/equipment/e0387/model/c0101e0387_sho.mdl":"x"},"Manipulations":[]}]}]}
            """);

        var result = ModEquipmentFileReader.ReadEquipmentSlots(mod);

        Assert.NotNull(result);
        Assert.Equal([EquipmentSlot.Feet], result);
    }

    // --- Penumbra 1.7.0's meta.json layout (replaces default_mod.json + group_*.json) ---

    [Fact]
    public void ReadEquipmentSlots_MetaJsonDefaultDataFiles_ResolvesSlot()
    {
        var mod = MakeTempModDirectory();
        WriteJson(mod, "meta.json", """
            {"FileVersion":4,"Identifier":"abc","Name":"Test Mod",
             "DefaultData":{"Files":{"chara/equipment/e0387/model/c0101e0387_sho.mdl":"files/sho.mdl"},"Manipulations":[]}}
            """);

        var result = ModEquipmentFileReader.ReadEquipmentSlots(mod);

        Assert.NotNull(result);
        Assert.Equal([EquipmentSlot.Feet], result);
    }

    [Fact]
    public void ReadEquipmentSlots_MetaJsonGroupOptionManipulation_ResolvesSlot()
    {
        var mod = MakeTempModDirectory();
        WriteJson(mod, "meta.json", """
            {"FileVersion":4,"Identifier":"abc","Name":"Test Mod",
             "Groups":[{"Type":"Single","Id":"g1","Name":"Variant","Options":[
                {"Id":"o1","Name":"Default","Manipulations":[{"Type":"Eqp","Manipulation":{"Entry":16129,"SetId":6040,"Slot":"Body"}}]}
             ]}]}
            """);

        var result = ModEquipmentFileReader.ReadEquipmentSlots(mod);

        Assert.NotNull(result);
        Assert.Equal([EquipmentSlot.Top], result);
    }

    [Fact]
    public void ReadEquipmentSlots_MetaJsonCombiningGroupContainer_ResolvesSlot()
    {
        var mod = MakeTempModDirectory();
        WriteJson(mod, "meta.json", """
            {"FileVersion":4,"Identifier":"abc","Name":"Test Mod",
             "Groups":[{"Type":"Combining","Id":"g1","Name":"Combo","Containers":[
                {"Name":"c1","Files":{"chara/equipment/e0387/model/c0101e0387_dwn.mdl":"x"},"Manipulations":[]}
             ]}]}
            """);

        var result = ModEquipmentFileReader.ReadEquipmentSlots(mod);

        Assert.NotNull(result);
        Assert.Equal([EquipmentSlot.Legs], result);
    }

    [Fact]
    public void ReadEquipmentSlots_MetaJsonPresent_TakesPrecedenceOverStaleOldFormatFiles()
    {
        // Real Penumbra migrates old files to a backup rather than leaving them alongside
        // meta.json, but this pins the defensive precedence rule regardless.
        var mod = MakeTempModDirectory();
        WriteJson(mod, "meta.json", """
            {"FileVersion":4,"Identifier":"abc","Name":"Test Mod",
             "DefaultData":{"Files":{"chara/equipment/e0387/model/c0101e0387_sho.mdl":"x"},"Manipulations":[]}}
            """);
        WriteJson(mod, "default_mod.json", """
            {"Files":{"chara/equipment/e0686/model/c0201e0686_top.mdl":"x"},"Manipulations":[]}
            """);

        var result = ModEquipmentFileReader.ReadEquipmentSlots(mod);

        Assert.NotNull(result);
        Assert.Equal([EquipmentSlot.Feet], result); // only meta.json's slot, not default_mod.json's
    }

    [Fact]
    public void CountConfigFiles_MetaJsonPresent_CountsOne()
    {
        var mod = MakeTempModDirectory();
        WriteJson(mod, "meta.json", "{}");

        Assert.Equal(1, ModEquipmentFileReader.CountConfigFiles(mod));
    }

    [Fact]
    public void CountConfigFiles_OldFormatFiles_CountsDefaultPlusGroups()
    {
        var mod = MakeTempModDirectory();
        WriteJson(mod, "default_mod.json", "{}");
        WriteJson(mod, "group_001_top.json", "{}");
        WriteJson(mod, "group_002_legs.json", "{}");

        Assert.Equal(3, ModEquipmentFileReader.CountConfigFiles(mod));
    }

    [Fact]
    public void CountConfigFiles_NoConfigFiles_ReturnsZero()
    {
        var mod = MakeTempModDirectory();

        Assert.Equal(0, ModEquipmentFileReader.CountConfigFiles(mod));
    }
}
