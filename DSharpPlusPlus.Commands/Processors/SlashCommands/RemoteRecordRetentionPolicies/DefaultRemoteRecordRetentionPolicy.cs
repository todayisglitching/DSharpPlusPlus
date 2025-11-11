using System.Threading.Tasks;

using DSharpPlusPlus.Entities;

namespace DSharpPlusPlus.Commands.Processors.SlashCommands.RemoteRecordRetentionPolicies;

internal sealed class DefaultRemoteRecordRetentionPolicy : IRemoteRecordRetentionPolicy
{
    public Task<bool> CheckDeletionStatusAsync(DiscordApplicationCommand command)
        => Task.FromResult(command.Type != DiscordApplicationCommandType.ActivityEntryPoint);
}
