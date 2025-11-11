using System;
using DSharpPlusPlus.Commands.ContextChecks.ParameterChecks;
using DSharpPlusPlus.Entities;

namespace DSharpPlusPlus.Commands.ArgumentModifiers;

/// <summary>
/// Specifies what channel types the parameter supports.
/// </summary>
/// <param name="channelTypes">The required types of channels.</param>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
public sealed class ChannelTypesAttribute(params DiscordChannelType[] channelTypes) : ParameterCheckAttribute
{
    /// <summary>
    /// Gets the channel types allowed for this parameter.
    /// </summary>
    public DiscordChannelType[] ChannelTypes { get; init; } = channelTypes;
}
