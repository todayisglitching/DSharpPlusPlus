using System;
using DSharpPlusPlus.Commands.ContextChecks;

namespace DSharpPlusPlus.Commands.Processors.TextCommands.ContextChecks;

[AttributeUsage(AttributeTargets.Parameter)]
public class TextMessageReplyAttribute(bool require = false) : ContextCheckAttribute
{
    public bool RequiresReply { get; init; } = require;
}
