using System.Text.RegularExpressions;

namespace PenumbraOrganizer.Plugin.Organizer.Classification;

/// <summary>
/// Decides which part of a body setup a Body mod is, from its game-file paths and its name.
/// Anything unclear is a Replacer, so a wrong guess costs a mod a place in the Replacer folder
/// rather than losing it among add-ons.
/// </summary>
public static class BodyPartClassifier
{
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly Regex SkeletonName = new(@"\b(skeleton|skel)\b", Opts);
    private static readonly Regex PhysicsName = new(@"\b(physics|jiggle|bounce)\b", Opts);
    private static readonly Regex HandsFeetName = new(@"\b(hands?|feet|foot|nails|toenails|claws)\b", Opts);
    private static readonly Regex TailEarName = new(@"\b(tails?|ears?|horns?)\b", Opts);
    // Clothing that covers the (usually nude) body. Named SFW after what the author calls it.
    private static readonly Regex SfwName = new(
        @"\b(sfw|lingerie|bra|panties|undies|underwear|bikini|swimsuit|smallclothes|knickers|thong)\b", Opts);
    // c0101b0001_glv.mdl: the slot suffix names the body piece (top, glv = hands, dwn = legs, sho = feet).
    private static readonly Regex BodySlotSuffix = new(@"_([a-z]{3})\.mdl$", Opts);

    public static BodyPart Classify(string name, IEnumerable<string>? gamePaths)
    {
        bool skeleton = false, physics = false, tailEarModel = false;
        var bodyModelSlots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (gamePaths is not null)
        {
            foreach (var raw in gamePaths)
            {
                if (string.IsNullOrEmpty(raw))
                    continue;
                var path = raw.Replace('\\', '/');
                if (path.EndsWith(".sklb", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".skp", StringComparison.OrdinalIgnoreCase))
                    skeleton = true;
                else if (path.EndsWith(".phyb", StringComparison.OrdinalIgnoreCase))
                    physics = true;
                else if (path.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
                {
                    if (path.Contains("/obj/body/", StringComparison.OrdinalIgnoreCase))
                        bodyModelSlots.Add(BodySlotSuffix.Match(path) is { Success: true } m ? m.Groups[1].Value : "?");
                    else if (path.Contains("/obj/tail/", StringComparison.OrdinalIgnoreCase)
                             || path.Contains("/obj/zear/", StringComparison.OrdinalIgnoreCase))
                        tailEarModel = true;
                }
            }
        }

        // Body models decide it before skeleton or physics files do, because a replacer often ships
        // a skeleton beside its models. Models for the hands (glv) and feet (sho) ONLY are an add-on
        // for those parts; anything with a torso (top), legs (dwn) or an unknown slot is a Replacer.
        if (bodyModelSlots.Count > 0)
            return bodyModelSlots.All(s => s is "glv" or "sho") ? BodyPart.HandsAndFeet : BodyPart.Replacer;
        // Tail, ear and horn mods routinely ship their own skeleton and physics files, so they are
        // decided before those are. A body model above still wins: that is a replacer.
        if (tailEarModel || TailEarName.IsMatch(name))
            return BodyPart.TailsEarsHorns;
        if (skeleton && physics)
            return BodyPart.Replacer;
        if (skeleton)
            return BodyPart.Skeleton;
        if (physics)
            return BodyPart.Physics;

        var byName = new List<BodyPart>(4);
        if (SfwName.IsMatch(name)) byName.Add(BodyPart.Sfw);
        if (SkeletonName.IsMatch(name)) byName.Add(BodyPart.Skeleton);
        if (PhysicsName.IsMatch(name)) byName.Add(BodyPart.Physics);
        if (HandsFeetName.IsMatch(name)) byName.Add(BodyPart.HandsAndFeet);
        return byName.Count == 1 ? byName[0] : BodyPart.Replacer;
    }
}
