namespace PenumbraOrganizer.Plugin.Organizer.NpcNames;

public sealed class NpcNameListDocument
{
    public int Version { get; set; } = NpcNameListCodec.CurrentVersion;
    public List<string> NPCs { get; set; } = [];
    public List<string> Enemies { get; set; } = [];
    public List<string> Bosses { get; set; } = [];
    public List<string> Excluded { get; set; } = [];
}
