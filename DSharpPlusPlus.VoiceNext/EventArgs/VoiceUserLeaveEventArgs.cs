using DSharpPlusPlus.Entities;
using DSharpPlusPlus.EventArgs;

namespace DSharpPlusPlus.VoiceNext.EventArgs;

/// <summary>
/// Arguments for <see cref="VoiceNextConnection.UserLeft"/>.
/// </summary>
public sealed class VoiceUserLeaveEventArgs : DiscordEventArgs
{
    /// <summary>
    /// Gets the user who left.
    /// </summary>
    public DiscordUser User { get; internal set; }

    /// <summary>
    /// Gets the SSRC of the user who left.
    /// </summary>
    public uint Ssrc { get; internal set; }

    internal VoiceUserLeaveEventArgs() : base() { }
}
