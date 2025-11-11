using System.Collections.Generic;
using DSharpPlusPlus.Commands.Trees;
using DSharpPlusPlus.EventArgs;

namespace DSharpPlusPlus.Commands.EventArgs;

/// <summary>
/// The event args passed to <see cref="CommandsExtension.ConfiguringCommands"/> event.
/// </summary>
public sealed class ConfigureCommandsEventArgs : DiscordEventArgs
{
    /// <summary>
    /// The collection of command trees to be built when the event is done.
    /// </summary>
    /// <remarks>
    /// This collection is mutable and can be modified to add or remove command trees.
    /// </remarks>
    public required List<CommandBuilder> CommandTrees { get; init; }
}
