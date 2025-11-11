using System.Globalization;
using System.Threading.Tasks;
using DSharpPlusPlus.Commands.Processors.SlashCommands;
using DSharpPlusPlus.Commands.Processors.TextCommands;
using DSharpPlusPlus.Entities;

namespace DSharpPlusPlus.Commands.Converters;

public class FloatConverter : ISlashArgumentConverter<float>, ITextArgumentConverter<float>
{
    public DiscordApplicationCommandOptionType ParameterType => DiscordApplicationCommandOptionType.Number;
    public ConverterInputType RequiresText => ConverterInputType.Always;
    public string ReadableName => "Decimal Number";

    public Task<Optional<float>> ConvertAsync(ConverterContext context) =>
        float.TryParse(context.Argument?.ToString(), CultureInfo.InvariantCulture, out float result)
            ? Task.FromResult(Optional.FromValue(result))
            : Task.FromResult(Optional.FromNoValue<float>());
}
