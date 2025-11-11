using DSharpPlusPlus.Entities;

namespace DSharpPlusPlus.EventArgs;

/// <summary>
/// Fired when an event is completed.
/// </summary>
public class ScheduledGuildEventCompletedEventArgs : DiscordEventArgs
{
    /// <summary>
    /// The event that finished.
    /// </summary>
    public DiscordScheduledGuildEvent Event { get; internal set; }

    internal ScheduledGuildEventCompletedEventArgs() : base() { }
}
