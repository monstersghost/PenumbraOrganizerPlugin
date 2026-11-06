using PenumbraOrganizer.Plugin.Organizer.Classification;
using Xunit;

namespace PenumbraOrganizer.Plugin.Tests.Organizer.Classification;

public sealed class BodyPartClassifierTests
{
    [Fact]
    public void Sklb_file_alone_is_skeleton() =>
        Assert.Equal(BodyPart.Skeleton, BodyPartClassifier.Classify(
            "Rue+ extras", ["chara/human/c0101/skeleton/base/b0001/skl_c0101b0001.sklb"]));

    [Fact]
    public void Skp_file_alone_is_skeleton() =>
        Assert.Equal(BodyPart.Skeleton, BodyPartClassifier.Classify("x", ["chara/human/c0101/skeleton/base/b0001/skl_c0101b0001.skp"]));

    [Fact]
    public void Phyb_file_alone_is_physics() =>
        Assert.Equal(BodyPart.Physics, BodyPartClassifier.Classify("x", ["chara/human/c0101/skeleton/base/b0001/phy_c0101b0001.phyb"]));

    [Fact]
    public void Skeleton_and_physics_files_together_are_a_replacer() =>
        Assert.Equal(BodyPart.Replacer, BodyPartClassifier.Classify("x",
            ["a/skl.sklb", "a/phy.phyb"]));

    [Fact]
    public void Body_model_present_makes_a_replacer_even_with_a_skeleton_file() =>
        Assert.Equal(BodyPart.Replacer, BodyPartClassifier.Classify("Rue+",
            ["chara/human/c0101/obj/body/b0001/model/c0101b0001_top.mdl", "a/skl.sklb"]));

    [Theory]
    [InlineData("chara/human/c0101/obj/body/b0001/model/c0101b0001_glv.mdl")]
    [InlineData("CHARA\\human\\c0101\\obj\\body\\b0001\\model\\c0101b0001_sho.MDL")]
    public void Hand_or_foot_body_models_alone_are_hands_and_feet(string path) =>
        Assert.Equal(BodyPart.HandsAndFeet, BodyPartClassifier.Classify("Pretty thing", [path]));

    [Fact]
    public void Hand_and_foot_models_together_are_hands_and_feet() =>
        Assert.Equal(BodyPart.HandsAndFeet, BodyPartClassifier.Classify("x",
            ["chara/human/c0101/obj/body/b0001/model/c0101b0001_glv.mdl",
             "chara/human/c0101/obj/body/b0001/model/c0101b0001_sho.mdl"]));

    [Fact]
    public void Hand_model_with_a_torso_model_is_a_replacer() =>
        Assert.Equal(BodyPart.Replacer, BodyPartClassifier.Classify("x",
            ["chara/human/c0101/obj/body/b0001/model/c0101b0001_glv.mdl",
             "chara/human/c0101/obj/body/b0001/model/c0101b0001_top.mdl"]));

    [Fact]
    public void Body_model_with_an_unknown_suffix_is_a_replacer() =>
        Assert.Equal(BodyPart.Replacer, BodyPartClassifier.Classify("x",
            ["chara/human/c0101/obj/body/b0001/model/c0101b0001_zzz.mdl"]));

    [Theory]
    [InlineData("[B] Fluffy Tail - 3")]
    [InlineData("[Myth] Oni Horns _ Small")]
    [InlineData("Nhagi - Mausi - Ears n Tail - Miqo F")]
    [InlineData("VULPIN [MIQO'TE MALE TAILS]")]
    public void Tail_ear_and_horn_names_are_tails_ears_and_horns(string name) =>
        Assert.Equal(BodyPart.TailsEarsHorns, BodyPartClassifier.Classify(name, []));

    [Theory]
    [InlineData("chara/human/c0101/obj/tail/t0001/model/c0101t0001_til.mdl")]
    [InlineData("chara/human/c0801/obj/zear/z0001/model/c0801z0001_zer.mdl")]
    public void Tail_and_ear_model_files_are_tails_ears_and_horns(string path) =>
        Assert.Equal(BodyPart.TailsEarsHorns, BodyPartClassifier.Classify("Something", [path]));

    [Fact]
    public void A_tail_mod_that_ships_physics_is_still_tails() =>
        Assert.Equal(BodyPart.TailsEarsHorns, BodyPartClassifier.Classify("Fluffy Tail", ["a/tail.phyb", "a/skl.sklb"]));

    [Fact]
    public void A_body_model_beats_a_tail_signal() =>
        Assert.Equal(BodyPart.Replacer, BodyPartClassifier.Classify("Rue+ with tail",
            ["chara/human/c0101/obj/body/b0001/model/c0101b0001_top.mdl"]));

    [Theory]
    [InlineData("Elegy - Ivette Bra & Panties")]
    [InlineData("Yet Another Lingerie Set++")]
    [InlineData("Calvin Klein Undies")]
    [InlineData("Pythia Body 3.0 - SFW Gear Addon")]
    [InlineData("Cute bikini")]
    public void Clothing_that_covers_the_body_is_sfw(string name) =>
        Assert.Equal(BodyPart.Sfw, BodyPartClassifier.Classify(name, []));

    [Fact]
    public void Sfw_keywords_do_not_match_inside_words() =>
        Assert.Equal(BodyPart.Replacer, BodyPartClassifier.Classify("Brasserie body", []));

    [Fact]
    public void Sfw_folder_name() =>
        Assert.Equal("SFW", BodyPartFolders.Folder(BodyPart.Sfw));

    [Fact]
    public void Tails_folder_name() =>
        Assert.Equal("Tails, Ears and Horns", BodyPartFolders.Folder(BodyPart.TailsEarsHorns));

    [Fact]
    public void Null_and_empty_paths_are_skipped() =>
        Assert.Equal(BodyPart.Skeleton, BodyPartClassifier.Classify("x", [null!, "", "a/skl.sklb"]));

    [Theory]
    [InlineData("Rue+ Skeleton", BodyPart.Skeleton)]
    [InlineData("Jiggle physics pack", BodyPart.Physics)]
    [InlineData("Rue+ Hands", BodyPart.HandsAndFeet)]
    [InlineData("Pretty feet", BodyPart.HandsAndFeet)]
    [InlineData("Toenails", BodyPart.HandsAndFeet)]
    [InlineData("Rue+", BodyPart.Replacer)]
    public void Name_keywords_decide_when_there_is_no_file_evidence(string name, BodyPart expected) =>
        Assert.Equal(expected, BodyPartClassifier.Classify(name, []));

    [Theory]
    [InlineData("Handsome face paint")]
    [InlineData("Skeletal armour")]
    [InlineData("Bouncer outfit")]
    public void Keywords_need_word_boundaries(string name) =>
        Assert.Equal(BodyPart.Replacer, BodyPartClassifier.Classify(name, []));

    [Fact]
    public void Two_name_parts_fall_back_to_replacer() =>
        Assert.Equal(BodyPart.Replacer, BodyPartClassifier.Classify("Skeleton and physics", []));

    [Fact]
    public void File_evidence_beats_the_name() =>
        Assert.Equal(BodyPart.Physics, BodyPartClassifier.Classify("Rue+ Hands", ["a/phy.phyb"]));

    [Fact]
    public void Null_paths_are_treated_as_none() =>
        Assert.Equal(BodyPart.Replacer, BodyPartClassifier.Classify("Rue+", null));

    [Theory]
    [InlineData(BodyPart.Replacer, "Replacer")]
    [InlineData(BodyPart.Skeleton, "Skeleton")]
    [InlineData(BodyPart.Physics, "Physics")]
    [InlineData(BodyPart.HandsAndFeet, "Hands and Feet")]
    public void Folder_names(BodyPart part, string folder) =>
        Assert.Equal(folder, BodyPartFolders.Folder(part));
}
