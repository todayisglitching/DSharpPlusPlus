using DSharpPlusPlus.EventArgs;

namespace DSharpPlusPlus.Commands.EventArgs;

public sealed class CommandExecutedEventArgs : DiscordEventArgs
{
    public required CommandContext Context { get; init; }
    public required object? CommandObject { get; init; }
}
