using System.Collections.Generic;
using DSharpPlusPlus.Entities;

namespace DSharpPlusPlus.EventArgs;

/// <summary>
/// Represents arguments for GuildDownloadCompleted event.
/// </summary>
public class GuildDownloadCompletedEventArgs : DiscordEventArgs
{
    /// <summary>
    /// Gets the dictionary of guilds that just finished downloading.
    /// </summary>
    public IReadOnlyDictionary<ulong, DiscordGuild> Guilds { get; }

    internal GuildDownloadCompletedEventArgs(IReadOnlyDictionary<ulong, DiscordGuild> guilds)
        : base() => this.Guilds = guilds;
}
