using System.Collections.Generic;
using DSharpPlusPlus.Commands.ContextChecks.ParameterChecks;
using DSharpPlusPlus.Commands.Trees;

namespace DSharpPlusPlus.Commands.Exceptions;

public class ParameterChecksFailedException : CommandsException
{
    public Command Command { get; init; }
    public IReadOnlyList<ParameterCheckFailedData> Errors { get; init; }

    public ParameterChecksFailedException(IReadOnlyList<ParameterCheckFailedData> errors, Command command, string? message = null) : base(message ?? $"Checks for {command.FullName} failed.")
    {
        this.Command = command;
        this.Errors = errors;
    }
}
