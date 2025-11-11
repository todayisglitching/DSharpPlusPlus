using System;

namespace DSharpPlusPlus.Commands.Trees.Metadata;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Delegate)]
public sealed class TextAliasAttribute(params string[] aliases) : Attribute
{
    public string[] Aliases { get; init; } = aliases;
}
