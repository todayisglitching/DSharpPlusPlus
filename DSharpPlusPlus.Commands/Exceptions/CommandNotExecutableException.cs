using System;
using DSharpPlusPlus.Commands.Trees;

namespace DSharpPlusPlus.Commands.Exceptions;

public sealed class CommandNotExecutableException : CommandsException
{
    public Command Command { get; init; }

    public CommandNotExecutableException(Command command, string? message = null) : base(message ?? $"Command {command.Name} is not executable.")
    {
        ArgumentNullException.ThrowIfNull(command, nameof(command));
        this.Command = command;
    }
}
