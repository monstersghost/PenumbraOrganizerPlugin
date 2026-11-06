namespace PenumbraOrganizer.Plugin.Organizer.Classification;

/// <summary>Which piece of a body setup a Body-category mod is. Replacer is the default and the fallback.</summary>
public enum BodyPart { Replacer, Skeleton, Physics, HandsAndFeet }

public static class BodyPartFolders
{
    public static string Folder(BodyPart part) => part switch
    {
        BodyPart.Replacer => "Replacer",
        BodyPart.Skeleton => "Skeleton",
        BodyPart.Physics => "Physics",
        BodyPart.HandsAndFeet => "Hands and Feet",
        _ => throw new ArgumentOutOfRangeException(nameof(part), part, "Unknown body part."),
    };
}
