namespace PenumbraOrganizer.Plugin.Organizer.Templates;

/// <summary>
/// Validates every externally supplied path in a template — entry destinations, the folders list,
/// and both the keys and the replacement values of folderLabels. Validating only entry
/// destinations would leave three other routes for a malformed path to reach a proposal.
/// </summary>
public static class TemplatePathValidator
{
    /// <summary>
    /// True for "" (root, matching the workbook's folder-only convention) and for any
    /// '/'-separated path with no leading/trailing separator, no empty or whitespace-only
    /// segment, no control characters, and within the depth, segment-length, and total-length
    /// limits.
    /// </summary>
    public static bool IsValidFolder(string folder)
    {
        if (folder.Length == 0)
            return true;

        if (folder.Length > TemplateLimits.MaxStringLength)
            return false;

        if (folder.StartsWith('/') || folder.EndsWith('/'))
            return false;

        if (folder.Any(char.IsControl))
            return false;

        var segments = folder.Split('/');
        if (segments.Length > TemplateLimits.MaxPathDepth)
            return false;

        return segments.All(segment =>
            !string.IsNullOrWhiteSpace(segment) && segment.Length <= TemplateLimits.MaxSegmentLength);
    }
}
