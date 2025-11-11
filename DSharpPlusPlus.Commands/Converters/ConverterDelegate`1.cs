using System.Threading.Tasks;
using DSharpPlusPlus.Entities;

namespace DSharpPlusPlus.Commands.Converters;

public delegate ValueTask<IOptional> ConverterDelegate(ConverterContext context);
