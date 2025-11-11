using DSharpPlusPlus.Entities;

namespace DSharpPlusPlus.EventArgs;

/// <summary>
/// Fired when an event is deleted.
/// </summary>
public class ScheduledGuildEventDeletedEventArgs : DiscordEventArgs
{
    /// <summary>
    /// The event that was deleted.
    /// </summary>
    public DiscordScheduledGuildEvent Event { get; internal set; }

    internal ScheduledGuildEventDeletedEventArgs() : base() { }
}
