using System.Globalization;
using System.Threading.Tasks;
using DSharpPlusPlus.Commands.Processors.SlashCommands;
using DSharpPlusPlus.Commands.Processors.TextCommands;
using DSharpPlusPlus.Entities;

namespace DSharpPlusPlus.Commands.Converters;

public class UInt16Converter : ISlashArgumentConverter<ushort>, ITextArgumentConverter<ushort>
{
    public DiscordApplicationCommandOptionType ParameterType => DiscordApplicationCommandOptionType.Integer;
    public ConverterInputType RequiresText => ConverterInputType.Always;
    public string ReadableName => "Positive Small Integer";

    public Task<Optional<ushort>> ConvertAsync(ConverterContext context) =>
        ushort.TryParse(context.Argument?.ToString(), CultureInfo.InvariantCulture, out ushort result)
            ? Task.FromResult(Optional.FromValue(result))
            : Task.FromResult(Optional.FromNoValue<ushort>());
}
