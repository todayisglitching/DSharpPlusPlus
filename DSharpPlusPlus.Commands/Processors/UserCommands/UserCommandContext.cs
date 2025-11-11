using DSharpPlusPlus.Commands.Processors.SlashCommands;

namespace DSharpPlusPlus.Commands.Processors.UserCommands;

/// <summary>
/// Indicates that the command was invoked via a user interaction.
/// </summary>
public record UserCommandContext : SlashCommandContext;
