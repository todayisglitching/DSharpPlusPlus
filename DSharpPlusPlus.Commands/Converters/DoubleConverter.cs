using System.Globalization;
using System.Threading.Tasks;
using DSharpPlusPlus.Commands.Processors.SlashCommands;
using DSharpPlusPlus.Commands.Processors.TextCommands;
using DSharpPlusPlus.Entities;

namespace DSharpPlusPlus.Commands.Converters;

public class DoubleConverter : ISlashArgumentConverter<double>, ITextArgumentConverter<double>
{
    public DiscordApplicationCommandOptionType ParameterType => DiscordApplicationCommandOptionType.Number;
    public ConverterInputType RequiresText => ConverterInputType.Always;
    public string ReadableName => "Decimal Number";

    public Task<Optional<double>> ConvertAsync(ConverterContext context) =>
        double.TryParse(context.Argument?.ToString(), CultureInfo.InvariantCulture, out double result)
            ? Task.FromResult(Optional.FromValue(result))
            : Task.FromResult(Optional.FromNoValue<double>());
}
