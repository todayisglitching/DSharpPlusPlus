using Newtonsoft.Json;

namespace DSharpPlusPlus.VoiceNext.Entities;

internal sealed class VoiceServerUpdatePayload
{
    [JsonProperty("token")]
    public string Token { get; set; }

    [JsonProperty("guild_id")]
    public ulong GuildId { get; set; }

    [JsonProperty("endpoint")]
    public string Endpoint { get; set; }
}
