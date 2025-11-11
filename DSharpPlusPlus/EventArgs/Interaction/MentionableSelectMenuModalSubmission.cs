using System.Collections.Generic;

using DSharpPlusPlus.Entities;

namespace DSharpPlusPlus.EventArgs;

/// <summary>
/// Provides information about a select menu for roles and users submitted through a modal.
/// </summary>
public sealed class MentionableSelectMenuModalSubmission : IModalSubmission
{
    /// <inheritdoc/>
    public DiscordComponentType ComponentType => DiscordComponentType.MentionableSelect;

    /// <inheritdoc/>
    public string CustomId { get; internal set; }

    /// <summary>
    /// The snowflake identifiers of the roles and users submitted.
    /// </summary>
    public IReadOnlyList<ulong> Ids { get; internal set; }

    internal MentionableSelectMenuModalSubmission(string customId, IReadOnlyList<ulong> ids)
    {
        this.CustomId = customId;
        this.Ids = ids;
    }
}
