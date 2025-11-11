using DSharpPlusPlus.Commands.Converters;

namespace DSharpPlusPlus.Commands.Processors.TextCommands;

public interface ITextArgumentConverter : IArgumentConverter
{
    public ConverterInputType RequiresText { get; }
}

public interface ITextArgumentConverter<T> : ITextArgumentConverter, IArgumentConverter<T>;
