namespace PenumbraOrganizer.Plugin.Organizer;

public enum CommandKind
{
    Toggle,
    SortNew,
    SortRoot,
    Help,
    Unknown,
}

public readonly record struct ParsedCommand(CommandKind Kind, bool Force);

/// <summary>Parses the argument text of the /porganizer chat command.</summary>
public static class CommandParser
{
    public static ParsedCommand Parse(string? args)
    {
        var parts = (args ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return new ParsedCommand(CommandKind.Toggle, false);

        var verb = parts[0].ToLowerInvariant();
        var force = parts.Length == 2 && parts[1].Equals("force", StringComparison.OrdinalIgnoreCase);
        var extraOk = parts.Length == 1 || force;

        return verb switch
        {
            "sortnew" when extraOk => new ParsedCommand(CommandKind.SortNew, force),
            "sortroot" when extraOk => new ParsedCommand(CommandKind.SortRoot, force),
            "help" when parts.Length == 1 => new ParsedCommand(CommandKind.Help, false),
            _ => new ParsedCommand(CommandKind.Unknown, false),
        };
    }
}
