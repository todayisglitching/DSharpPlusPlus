using System.Globalization;
using System.Threading.Tasks;
using DSharpPlusPlus.Commands.Processors.SlashCommands;
using DSharpPlusPlus.Commands.Processors.TextCommands;
using DSharpPlusPlus.Entities;

namespace DSharpPlusPlus.Commands.Converters;

public class UInt32Converter : ISlashArgumentConverter<uint>, ITextArgumentConverter<uint>
{
    public DiscordApplicationCommandOptionType ParameterType => DiscordApplicationCommandOptionType.Integer;
    public ConverterInputType RequiresText => ConverterInputType.Always;
    public string ReadableName => "Positive Integer";

    public Task<Optional<uint>> ConvertAsync(ConverterContext context) =>
        uint.TryParse(context.Argument?.ToString(), CultureInfo.InvariantCulture, out uint result)
            ? Task.FromResult(Optional.FromValue(result))
            : Task.FromResult(Optional.FromNoValue<uint>());
}
