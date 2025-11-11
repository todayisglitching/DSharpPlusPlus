using Newtonsoft.Json;

namespace DSharpPlusPlus.Net.Abstractions.Rest;

public sealed class DiscordRolePosition
{
    [JsonProperty("id", NullValueHandling = NullValueHandling.Ignore)]
    public ulong RoleId { get; set; }

    [JsonProperty("position", NullValueHandling = NullValueHandling.Ignore)]
    public int Position { get; set; }
}
