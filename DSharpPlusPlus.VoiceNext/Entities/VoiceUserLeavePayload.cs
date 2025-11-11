using Newtonsoft.Json;

namespace DSharpPlusPlus.VoiceNext.Entities;

internal sealed class VoiceUserLeavePayload
{
    [JsonProperty("user_id")]
    public ulong UserId { get; set; }
}
