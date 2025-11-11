using DSharpPlusPlus.Commands.Processors.SlashCommands;

namespace DSharpPlusPlus.Commands.Processors.MessageCommands;

/// <summary>
/// Indicates that the command was invoked via a message interaction.
/// </summary>
public record MessageCommandContext : SlashCommandContext;
