using Newtonsoft.Json;

namespace DSharpPlusPlus.VoiceNext.Entities;

internal sealed class VoiceUserJoinPayload
{
    [JsonProperty("user_id")]
    public ulong UserId { get; private set; }

    [JsonProperty("audio_ssrc")]
    public uint Ssrc { get; private set; }
}
