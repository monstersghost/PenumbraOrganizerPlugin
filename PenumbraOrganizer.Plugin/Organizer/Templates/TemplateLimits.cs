namespace PenumbraOrganizer.Plugin.Organizer.Templates;

/// <summary>
/// Hard limits enforced during decode. Successful inflation does not make a document
/// structurally sane, so these are checked separately from the transport size caps.
/// </summary>
public static class TemplateLimits
{
    public const int MaxCompressedBytes = 1_048_576;    // 1 MB
    public const int MaxDecompressedBytes = 8_388_608;  // 8 MB
    public const int MaxEntries = 20_000;
    public const int MaxFolders = 5_000;
    public const int MaxFolderLabels = 500;
    public const int MaxStringLength = 512;
    public const int MaxPathDepth = 16;
    public const int MaxSegmentLength = 128;
}
