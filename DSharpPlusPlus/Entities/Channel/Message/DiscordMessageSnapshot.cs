using Newtonsoft.Json;

namespace DSharpPlusPlus.Entities;

/// <summary>
/// Represents a Discord message snapshot.
/// </summary>
public sealed class DiscordMessageSnapshot
{
    /// <summary>
    /// Gets the message object for the message snapshot.
    /// </summary>
    [JsonProperty("message", NullValueHandling = NullValueHandling.Ignore)]
    public DiscordMessageSnapshotContent Message { get; set; }
}
