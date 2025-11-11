using System.Threading.Tasks;

namespace DSharpPlusPlus.Commands.ContextChecks;

internal sealed class RequireNsfwCheck : IContextCheck<RequireNsfwAttribute>
{
    public ValueTask<string?> ExecuteCheckAsync(RequireNsfwAttribute attribute, CommandContext context) =>
        ValueTask.FromResult(context.Channel.IsPrivate || context.Channel.IsNsfw || (context.Guild is not null && context.Guild.IsNsfw)
            ? null
            : "This command must be executed in a NSFW channel."
        );
}
