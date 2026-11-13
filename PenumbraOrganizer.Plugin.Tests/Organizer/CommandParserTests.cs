using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class CommandParserTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Empty_IsToggle(string? args) =>
        Assert.Equal(new ParsedCommand(CommandKind.Toggle, false), CommandParser.Parse(args));

    [Theory]
    [InlineData("sortnew", CommandKind.SortNew, false)]
    [InlineData(" SortNew ", CommandKind.SortNew, false)]
    [InlineData("sortnew force", CommandKind.SortNew, true)]
    [InlineData("sortroot", CommandKind.SortRoot, false)]
    [InlineData("sortroot FORCE", CommandKind.SortRoot, true)]
    [InlineData("help", CommandKind.Help, false)]
    public void Known(string args, CommandKind kind, bool force) =>
        Assert.Equal(new ParsedCommand(kind, force), CommandParser.Parse(args));

    [Theory]
    [InlineData("bogus")]
    [InlineData("sortnew now")]
    public void Unknown(string args) =>
        Assert.Equal(CommandKind.Unknown, CommandParser.Parse(args).Kind);
}
