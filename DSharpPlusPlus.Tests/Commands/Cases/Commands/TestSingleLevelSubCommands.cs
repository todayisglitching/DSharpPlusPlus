using System.Threading.Tasks;
using DSharpPlusPlus.Commands;
using DSharpPlusPlus.Commands.ArgumentModifiers;
using DSharpPlusPlus.Commands.Processors.TextCommands;

namespace DSharpPlusPlus.Tests.Commands.Cases.Commands;

public class TestSingleLevelSubCommands
{
    [Command("tag")]
    public class TagCommand
    {
        [Command("add")]
        public static ValueTask AddAsync(TextCommandContext context, string name, [RemainingText] string content) => default;

        [Command("get")]
        public static ValueTask GetAsync(CommandContext context, string name) => default;
    }

    [Command("empty")]
    public class EmptyCommand { }
}
