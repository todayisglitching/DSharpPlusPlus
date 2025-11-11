using DSharpPlusPlus.Commands.Converters;
using DSharpPlusPlus.Entities;

namespace DSharpPlusPlus.Commands.Processors.SlashCommands;

public interface ISlashArgumentConverter : IArgumentConverter
{
    public DiscordApplicationCommandOptionType ParameterType { get; }
}

public interface ISlashArgumentConverter<T> : ISlashArgumentConverter, IArgumentConverter<T>;
