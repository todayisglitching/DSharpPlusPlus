using System.Collections.Generic;

using DSharpPlusPlus.Entities;

namespace DSharpPlusPlus.EventArgs;

/// <summary>
/// Provides information about a string select menu submitted through a modal.
/// </summary>
public sealed class SelectMenuModalSubmission : IModalSubmission
{
    /// <inheritdoc/>
    public DiscordComponentType ComponentType => DiscordComponentType.StringSelect;

    /// <inheritdoc/>
    public string CustomId { get; internal set; }

    /// <summary>
    /// The values selected from the menu.
    /// </summary>
    public IReadOnlyList<string> Values { get; internal set; }

    internal SelectMenuModalSubmission(string customId, IReadOnlyList<string> values)
    {
        this.CustomId = customId;
        this.Values = values;
    }
}
