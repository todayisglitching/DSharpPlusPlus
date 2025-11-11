using DSharpPlusPlus.Entities;

namespace DSharpPlusPlus.EventArgs;

/// <summary>
/// Represents arguments for EntitlementDeleted event.
/// </summary>
public class EntitlementDeletedEventArgs : DiscordEventArgs
{
    /// <summary>
    /// Entitlement which was deleted
    /// </summary>
    public DiscordEntitlement Entitlement { get; internal set; }
}
