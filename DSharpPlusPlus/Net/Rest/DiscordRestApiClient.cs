using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using DSharpPlusPlus.Entities;
using DSharpPlusPlus.Entities.AuditLogs;
using DSharpPlusPlus.Exceptions;
using DSharpPlusPlus.Metrics;
using DSharpPlusPlus.Net.Abstractions;
using DSharpPlusPlus.Net.Abstractions.Rest;
using DSharpPlusPlus.Net.Serialization;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DSharpPlusPlus.Net;

// huge credits to dvoraks 8th symphony for being a source of sanity in the trying times of
// fixing this absolute catastrophy up at least somewhat

public sealed class DiscordRestApiClient
{
    private const string ReasonHeaderName = "X-Audit-Log-Reason";

    internal BaseDiscordClient? discord;
    internal RestClient rest;

    [ActivatorUtilitiesConstructor]
    public DiscordRestApiClient(RestClient rest) => this.rest = rest;

    // This is for meta-clients, such as the webhook client
    internal DiscordRestApiClient(TimeSpan timeout, ILogger logger)
        => this.rest = new(new(), timeout, logger);

    /// <inheritdoc cref="RestClient.GetRequestMetrics(bool)"/>
    internal RequestMetricsCollection GetRequestMetrics(bool sinceLastCall = false)
        => this.rest.GetRequestMetrics(sinceLastCall);

    internal void SetClient(BaseDiscordClient client)
        => this.discord = client;

    internal void SetToken(TokenType type, string token)
        => this.rest.SetToken(type, token);

    private DiscordMessage PrepareMessage(JToken msgRaw)
    {
        TransportUser author = msgRaw["author"]!.ToDiscordObject<TransportUser>();
        DiscordMessage message = msgRaw.ToDiscordObject<DiscordMessage>();
        message.Discord = this.discord!;
        PopulateMessage(author, message);

        JToken? referencedMsg = msgRaw["referenced_message"];
        if (message.MessageType == DiscordMessageType.Reply && referencedMsg is not null && message.ReferencedMessage is not null)
        {
            TransportUser referencedAuthor = referencedMsg["author"]!.ToDiscordObject<TransportUser>();
            message.ReferencedMessage.Discord = this.discord!;
            PopulateMessage(referencedAuthor, message.ReferencedMessage);
        }

        return message;
    }

    private void PopulateMessage(TransportUser author, DiscordMessage ret)
    {
        if (ret.Channel is null && ret.Discord is DiscordClient client)
        {
            ret.Channel = client.InternalGetCachedChannel(ret.ChannelId);
        }

        if (ret.GuildId is null || !ret.Discord.Guilds.TryGetValue(ret.GuildId.Value, out DiscordGuild? guild))
        {
            guild = ret.Channel?.Guild;
        }

        ret.GuildId ??= guild?.Id;

        // I can't think of a case where guildId will never be not null since the guildId is a gateway exclusive
        // property, however if that property is added later to the rest api response, this case would be hit.
        ret.Channel ??= ret.GuildId is null
            ? new DiscordDmChannel
            {
                Id = ret.ChannelId,
                Discord = this.discord!,
                Type = DiscordChannelType.Private
            }
            : new DiscordChannel
            {
                Id = ret.ChannelId,
                GuildId = ret.GuildId,
                Discord = this.discord!
            };

        //If this is a webhook, it shouldn't be in the user cache.
        if (author.IsBot && int.Parse(author.Discriminator) == 0)
        {
            ret.Author = new(author)
            {
                Discord = this.discord!
            };
        }
        else
        {
            // get and cache the user
            if (!this.discord!.UserCache.TryGetValue(author.Id, out DiscordUser? user))
            {
                user = new DiscordUser(author)
                {
                    Discord = this.discord
                };
            }

            this.discord.UserCache[author.Id] = user;

            // get the member object if applicable, if not set the message author to an user
            if (guild is not null)
            {
                if (!guild.Members.TryGetValue(author.Id, out DiscordMember? member))
                {
                    member = new(user)
                    {
                        Discord = this.discord,
                        guildId = guild.Id
                    };
                }

                ret.Author = member;
            }
            else
            {
                ret.Author = user!;
            }
        }

        ret.PopulateMentions();

        ret.reactions ??= [];
        foreach (DiscordReaction reaction in ret.reactions)
        {
            reaction.Emoji.Discord = this.discord!;
        }

        if(ret.MessageSnapshots != null)
        {
            foreach (DiscordMessageSnapshot snapshot in ret.MessageSnapshots)
            {
                snapshot.Message?.PopulateMentions();
            }
        }
    }

    #region Guild

    public async ValueTask<IReadOnlyList<DiscordGuild>> GetGuildsAsync
    (
        int? limit = null,
        ulong? before = null,
        ulong? after = null,
        bool? withCounts = null
    )
    {
        QueryUriBuilder builder = new($"{Endpoints.Users}/@me/{Endpoints.Guilds}");

        if (limit is not null)
        {
            if (limit is < 1 or > 200)
            {
                throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be a number between 1 and 200.");
            }
            builder.AddParameter("limit", limit.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (before is not null)
        {
            builder.AddParameter("before", before.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (after is not null)
        {
            builder.AddParameter("after", after.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (withCounts is not null)
        {
            builder.AddParameter("with_counts", withCounts.Value.ToString(CultureInfo.InvariantCulture));
        }

        RestRequest request = new()
        {
            Route = $"/{Endpoints.Users}/@me/{Endpoints.Guilds}",
            Url = builder.Build(),
            Method = HttpMethod.Get
        };

        RestResponse response = await this.rest.ExecuteRequestAsync(request);

        JArray jArray = JArray.Parse(response.Response!);

        List<DiscordGuild> guilds = new(200);

        foreach (JToken token in jArray)
        {
            DiscordGuild guildRest = token.ToDiscordObject<DiscordGuild>();

            if (guildRest.roles is not null)
            {
                foreach (DiscordRole role in guildRest.roles.Values)
                {
                    role.guildId = guildRest.Id;
                    role.Discord = this.discord!;
                }
            }

            guildRest.Discord = this.discord!;
            guilds.Add(guildRest);
        }

        return guilds;
    }

    public async ValueTask<IReadOnlyList<DiscordMember>> SearchMembersAsync
    (
        ulong guildId,
        string name,
        int? limit = null
    )
    {
        QueryUriBuilder builder = new($"{Endpoints.Guilds}/{guildId}/{Endpoints.Members}/{Endpoints.Search}");
        builder.AddParameter("query", name);

        if (limit is not null)
        {
            builder.AddParameter("limit", limit.Value.ToString(CultureInfo.InvariantCulture));
        }

        RestRequest request = new()
        {
            Route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Members}/{Endpoints.Search}",
            Url = builder.Build(),
            Method = HttpMethod.Get
        };

        RestResponse response = await this.rest.ExecuteRequestAsync(request);

        JArray array = JArray.Parse(response.Response!);
        IReadOnlyList<TransportMember> transportMembers = array.ToDiscordObject<IReadOnlyList<TransportMember>>();

        List<DiscordMember> members = [];

        foreach (TransportMember transport in transportMembers)
        {
            DiscordUser usr = new(transport.User) { Discord = this.discord! };

            this.discord!.UpdateUserCache(usr);

            members.Add(new DiscordMember(transport) { Discord = this.discord, guildId = guildId });
        }

        return members;
    }

    public async ValueTask<DiscordBan> GetGuildBanAsync
    (
        ulong guildId,
        ulong userId
    )
    {
        RestRequest request = new()
        {
            Route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Bans}/:user_id",
            Url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Bans}/{userId}",
            Method = HttpMethod.Get
        };

        RestResponse response = await this.rest.ExecuteRequestAsync(request);

        JObject json = JObject.Parse(response.Response!);

        DiscordBan ban = json.ToDiscordObject<DiscordBan>();

        if (!this.discord!.TryGetCachedUserInternal(ban.RawUser.Id, out DiscordUser? user))
        {
            user = new DiscordUser(ban.RawUser) { Discord = this.discord };
            user = this.discord.UpdateUserCache(user);
        }

        ban.User = user;

        return ban;
    }

    public async ValueTask<DiscordGuild> CreateGuildAsync
    (
        string name,
        string regionId,
        Optional<string> iconb64 = default,
        DiscordVerificationLevel? verificationLevel = null,
        DiscordDefaultMessageNotifications? defaultMessageNotifications = null,
        DiscordSystemChannelFlags? systemChannelFlags = null
    )
    {
        RestGuildCreatePayload payload = new()
        {
            Name = name,
            RegionId = regionId,
            DefaultMessageNotifications = defaultMessageNotifications,
            VerificationLevel = verificationLevel,
            IconBase64 = iconb64,
            SystemChannelFlags = systemChannelFlags
        };

        RestRequest request = new()
        {
            Route = $"{Endpoints.Guilds}",
            Url = $"{Endpoints.Guilds}",
            Payload = DiscordJson.SerializeObject(payload),
            Method = HttpMethod.Post
        };

        RestResponse response = await this.rest.ExecuteRequestAsync(request);

        JObject json = JObject.Parse(response.Response!);
        JArray rawMembers = (JArray)json["members"]!;
        DiscordGuild guild = json.ToDiscordObject<DiscordGuild>();

        if (this.discord is DiscordClient dc)
        {
            // this looks wrong. TODO: investigate double-fired event?
            await dc.OnGuildCreateEventAsync(guild, rawMembers, null!);
        }

        return guild;
    }

    public async ValueTask<DiscordGuild> CreateGuildFromTemplateAsync
    (
        string templateCode,
        string name,
        Optional<string> iconb64 = default
    )
    {
        RestGuildCreateFromTemplatePayload payload = new()
        {
            Name = name,
            IconBase64 = iconb64
        };

        RestRequest request = new()
        {
            Route = $"{Endpoints.Guilds}/{Endpoints.Templates}/:template_code",
            Url = $"{Endpoints.Guilds}/{Endpoints.Templates}/{templateCode}",
            Payload = DiscordJson.SerializeObject(payload),
            Method = HttpMethod.Post
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        JObject json = JObject.Parse(res.Response!);
        JArray rawMembers = (JArray)json["members"]!;
        DiscordGuild guild = json.ToDiscordObject<DiscordGuild>();

        if (this.discord is DiscordClient dc)
        {
            await dc.OnGuildCreateEventAsync(guild, rawMembers, null!);
        }

        return guild;
    }

    public async ValueTask DeleteGuildAsync
    (
        ulong guildId
    )
    {
        RestRequest request = new()
        {
            Route = $"{Endpoints.Guilds}/{guildId}",
            Url = $"{Endpoints.Guilds}/{guildId}",
            Method = HttpMethod.Delete
        };

        _ = await this.rest.ExecuteRequestAsync(request);
    }

    public async ValueTask<DiscordGuild> ModifyGuildAsync
    (
        ulong guildId,
        Optional<string> name = default,
        Optional<string> region = default,
        Optional<DiscordVerificationLevel> verificationLevel = default,
        Optional<DiscordDefaultMessageNotifications> defaultMessageNotifications = default,
        Optional<DiscordMfaLevel> mfaLevel = default,
        Optional<DiscordExplicitContentFilter> explicitContentFilter = default,
        Optional<ulong?> afkChannelId = default,
        Optional<int> afkTimeout = default,
        Optional<string> iconb64 = default,
        Optional<ulong> ownerId = default,
        Optional<string> splashb64 = default,
        Optional<ulong?> systemChannelId = default,
        Optional<string> banner = default,
        Optional<string> description = default,
        Optional<string> discoverySplash = default,
        Optional<IEnumerable<string>> features = default,
        Optional<string> preferredLocale = default,
        Optional<ulong?> publicUpdatesChannelId = default,
        Optional<ulong?> rulesChannelId = default,
        Optional<DiscordSystemChannelFlags> systemChannelFlags = default,
        string? reason = null
    )
    {
        RestGuildModifyPayload payload = new()
        {
            Name = name,
            RegionId = region,
            VerificationLevel = verificationLevel,
            DefaultMessageNotifications = defaultMessageNotifications,
            MfaLevel = mfaLevel,
            ExplicitContentFilter = explicitContentFilter,
            AfkChannelId = afkChannelId,
            AfkTimeout = afkTimeout,
            IconBase64 = iconb64,
            SplashBase64 = splashb64,
            OwnerId = ownerId,
            SystemChannelId = systemChannelId,
            Banner = banner,
            Description = description,
            DiscoverySplash = discoverySplash,
            Features = features,
            PreferredLocale = preferredLocale,
            PublicUpdatesChannelId = publicUpdatesChannelId,
            RulesChannelId = rulesChannelId,
            SystemChannelFlags = systemChannelFlags
        };

        RestRequest request = new()
        {
            Route = $"{Endpoints.Guilds}/{guildId}",
            Url = $"{Endpoints.Guilds}/{guildId}",
            Method = HttpMethod.Patch,
            Payload = DiscordJson.SerializeObject(payload),
            Headers = string.IsNullOrWhiteSpace(reason)
                ? null
                : new Dictionary<string, string>
                {
                    [ReasonHeaderName] = reason
                }
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        JObject json = JObject.Parse(res.Response!);
        JArray rawMembers = (JArray)json["members"]!;
        DiscordGuild guild = json.ToDiscordObject<DiscordGuild>();
        foreach (DiscordRole r in guild.roles.Values)
        {
            r.guildId = guild.Id;
        }

        if (this.discord is DiscordClient dc)
        {
            await dc.OnGuildUpdateEventAsync(guild, rawMembers!);
        }

        return guild;
    }

    public async ValueTask<IReadOnlyList<DiscordBan>> GetGuildBansAsync
    (
        ulong guildId,
        int? limit = null,
        ulong? before = null,
        ulong? after = null
    )
    {
        QueryUriBuilder builder = new($"{Endpoints.Guilds}/{guildId}/{Endpoints.Bans}");

        if (limit is not null)
        {
            builder.AddParameter("limit", limit.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (before is not null)
        {
            builder.AddParameter("before", before.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (after is not null)
        {
            builder.AddParameter("after", after.Value.ToString(CultureInfo.InvariantCulture));
        }

        RestRequest request = new()
        {
            Route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Bans}",
            Url = builder.Build(),
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        List<DiscordBan> bans = JsonConvert.DeserializeObject<IEnumerable<DiscordBan>>(res.Response!)!
        .Select(xb =>
        {
            if (!this.discord!.TryGetCachedUserInternal(xb.RawUser.Id, out DiscordUser? user))
            {
                user = new DiscordUser(xb.RawUser) { Discord = this.discord };
                user = this.discord.UpdateUserCache(user);
            }

            xb.User = user;
            return xb;
        })
        .ToList();

        return bans;
    }

    public async ValueTask CreateGuildBanAsync
    (
        ulong guildId,
        ulong userId,
        int deleteMessageSeconds,
        string? reason = null
    )
    {
        if (deleteMessageSeconds is < 0 or > 604800)
        {
            throw new ArgumentException("Delete message seconds must be a number between 0 and 604800 (7 Days).", nameof(deleteMessageSeconds));
        }

        QueryUriBuilder builder = new($"{Endpoints.Guilds}/{guildId}/{Endpoints.Bans}/{userId}");

        builder.AddParameter("delete_message_seconds", deleteMessageSeconds.ToString(CultureInfo.InvariantCulture));

        RestRequest request = new()
        {
            Route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Bans}/:user_id",
            Url = builder.Build(),
            Method = HttpMethod.Put,
            Headers = string.IsNullOrWhiteSpace(reason)
                ? null
                : new Dictionary<string, string>
                {
                    [ReasonHeaderName] = reason
                }
        };

        _ = await this.rest.ExecuteRequestAsync(request);
    }

    public async ValueTask RemoveGuildBanAsync
    (
        ulong guildId,
        ulong userId,
        string? reason = null
    )
    {
        RestRequest request = new()
        {
            Route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Bans}/:user_id",
            Url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Bans}/{userId}",
            Method = HttpMethod.Delete,
            Headers = string.IsNullOrWhiteSpace(reason)
                ? null
                : new Dictionary<string, string>
                {
                    [ReasonHeaderName] = reason
                }
        };

        _ = await this.rest.ExecuteRequestAsync(request);
    }

    public async ValueTask<DiscordBulkBan> CreateGuildBulkBanAsync(ulong guildId, IEnumerable<ulong> userIds, int? deleteMessagesSeconds = null, string? reason = null)
    {
        if (userIds.TryGetNonEnumeratedCount(out int count) && count > 200)
        {
            throw new ArgumentException("You can only ban up to 200 users at once.");
        }
        else if (userIds.Count() > 200)
        {
            throw new ArgumentException("You can only ban up to 200 users at once.");
        }

        if (deleteMessagesSeconds is not null and (< 0 or > 604800))
        {
            throw new ArgumentException("Delete message seconds must be a number between 0 and 604800 (7 days).", nameof(deleteMessagesSeconds));
        }

        RestRequest request = new()
        {
            Route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.BulkBan}",
            Url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.BulkBan}",
            Method = HttpMethod.Post,
            Headers = string.IsNullOrWhiteSpace(reason)
                ? null
                : new Dictionary<string, string>
                {
                    [ReasonHeaderName] = reason
                },
            Payload = DiscordJson.SerializeObject(new RestGuildBulkBanPayload
            {
                DeleteMessageSeconds = deleteMessagesSeconds,
                UserIds = userIds
            })
        };

        RestResponse response = await this.rest.ExecuteRequestAsync(request);

        DiscordBulkBan bulkBan = JsonConvert.DeserializeObject<DiscordBulkBan>(response.Response!)!;

        List<DiscordUser> bannedUsers = new(bulkBan.BannedUserIds.Count());
        foreach (ulong userId in bulkBan.BannedUserIds)
        {
            if (!this.discord!.TryGetCachedUserInternal(userId, out DiscordUser? user))
            {
                user = new DiscordUser(new TransportUser { Id = userId }) { Discord = this.discord };
                user = this.discord.UpdateUserCache(user);
            }

            bannedUsers.Add(user);
        }
        bulkBan.BannedUsers = bannedUsers;

        List<DiscordUser> failedUsers = new(bulkBan.FailedUserIds.Count());
        foreach (ulong userId in bulkBan.FailedUserIds)
        {
            if (!this.discord!.TryGetCachedUserInternal(userId, out DiscordUser? user))
            {
                user = new DiscordUser(new TransportUser { Id = userId }) { Discord = this.discord };
                user = this.discord.UpdateUserCache(user);
            }

            failedUsers.Add(user);
        }
        bulkBan.FailedUsers = failedUsers;

        return bulkBan;
    }

    public async ValueTask LeaveGuildAsync
    (
        ulong guildId
    )
    {
        RestRequest request = new()
        {
            Route = $"{Endpoints.Users}/{Endpoints.Me}/{Endpoints.Guilds}/{guildId}",
            Url = $"{Endpoints.Users}/{Endpoints.Me}/{Endpoints.Guilds}/{guildId}",
            Method = HttpMethod.Delete
        };

        _ = await this.rest.ExecuteRequestAsync(request);
    }

    public async ValueTask<DiscordMember?> AddGuildMemberAsync
    (
        ulong guildId,
        ulong userId,
        string accessToken,
        bool? muted = null,
        bool? deafened = null,
        string? nick = null,
        IEnumerable<ulong>? roles = null
    )
    {
        RestGuildMemberAddPayload payload = new()
        {
            AccessToken = accessToken,
            Nickname = nick ?? "",
            Roles = roles ?? [],
            Deaf = deafened ?? false,
            Mute = muted ?? false
        };

        RestRequest request = new()
        {
            Route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Members}/:user_id",
            Url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Members}/{userId}",
            Method = HttpMethod.Put,
            Payload = DiscordJson.SerializeObject(payload)
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        if (res.ResponseCode == HttpStatusCode.NoContent)
        {
            // User was already in the guild, Discord doesn't return the member object in this case
            return null;
        }

        TransportMember transport = JsonConvert.DeserializeObject<TransportMember>(res.Response!)!;

        DiscordUser usr = new(transport.User) { Discord = this.discord! };

        this.discord!.UpdateUserCache(usr);

        return new DiscordMember(transport) { Discord = this.discord!, guildId = guildId };
    }

    public async ValueTask<IReadOnlyList<DiscordMember>> ListGuildMembersAsync
    (
        ulong guildId,
        int? limit = null,
        ulong? after = null
    )
    {
        QueryUriBuilder builder = new($"{Endpoints.Guilds}/{guildId}/{Endpoints.Members}");

        if (limit is not null and > 0)
        {
            builder.AddParameter("limit", limit.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (after is not null)
        {
            builder.AddParameter("after", after.Value.ToString(CultureInfo.InvariantCulture));
        }

        RestRequest request = new()
        {
            Route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Members}",
            Url = builder.Build(),
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        List<TransportMember> rawMembers = JsonConvert.DeserializeObject<List<TransportMember>>(res.Response!)!;
        List<DiscordMember> members = new(rawMembers.Count);

        foreach (TransportMember tm in rawMembers)
        {
            this.discord.UpdateUserCache(new(tm.User)
            {
                Discord = this.discord
            });

            DiscordMember member = new(tm)
            {
                Discord = this.discord,
                guildId = guildId
            };

            members.Add(member);
        }

        return members;
    }

    public async ValueTask AddGuildMemberRoleAsync
    (
        ulong guildId,
        ulong userId,
        ulong roleId,
        string? reason = null
    )
    {
        RestRequest request = new()
        {
            Route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Members}/:user_id/{Endpoints.Roles}/:role_id",
            Url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Members}/{userId}/{Endpoints.Roles}/{roleId}",
            Method = HttpMethod.Put,
            Headers = string.IsNullOrWhiteSpace(reason)
                ? null
                : new Dictionary<string, string>
                {
                    [ReasonHeaderName] = reason
                }
        };

        _ = await this.rest.ExecuteRequestAsync(request);
    }

    public async ValueTask RemoveGuildMemberRoleAsync
    (
        ulong guildId,
        ulong userId,
        ulong roleId,
        string reason
    )
    {
        RestRequest request = new()
        {
            Route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Members}/:user_id/{Endpoints.Roles}/:role_id",
            Url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Members}/{userId}/{Endpoints.Roles}/{roleId}",
            Method = HttpMethod.Delete,
            Headers = string.IsNullOrWhiteSpace(reason)
                ? null
                : new Dictionary<string, string>
                {
                    [ReasonHeaderName] = reason
                }
        };

        _ = await this.rest.ExecuteRequestAsync(request);
    }

    public async ValueTask ModifyGuildChannelPositionAsync
    (
        ulong guildId,
        IEnumerable<DiscordChannelPosition> payload,
        string? reason = null
    )
    {
        RestRequest request = new()
        {
            Route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Channels}",
            Url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Channels}",
            Method = HttpMethod.Patch,
            Payload = DiscordJson.SerializeObject(payload),
            Headers = string.IsNullOrWhiteSpace(reason)
                ? null
                : new Dictionary<string, string>
                {
                    [ReasonHeaderName] = reason
                }
        };

        await this.rest.ExecuteRequestAsync(request);
    }

    // TODO: should probably return an IReadOnlyList here, unsure as to the extent of the breaking change
    public async ValueTask<DiscordRole[]> ModifyGuildRolePositionsAsync
    (
        ulong guildId,
        IEnumerable<DiscordRolePosition> newRolePositions,
        string? reason = null
    )
    {
        RestRequest request = new()
        {
            Route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Roles}",
            Url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Roles}",
            Method = HttpMethod.Patch,
            Payload = DiscordJson.SerializeObject(newRolePositions),
            Headers = string.IsNullOrWhiteSpace(reason)
                ? null
                : new Dictionary<string, string>
                {
                    [ReasonHeaderName] = reason
                }
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        DiscordRole[] ret = JsonConvert.DeserializeObject<DiscordRole[]>(res.Response!)!;
        foreach (DiscordRole role in ret)
        {
            role.Discord = this.discord!;
            role.guildId = guildId;
        }

        return ret;
    }

    public async Task<IAsyncEnumerable<DiscordAuditLogEntry>> GetAuditLogsAsync
    (
        DiscordGuild guild,
        int limit,
        ulong? after = null,
        ulong? before = null,
        ulong? userId = null,
        DiscordAuditLogActionType? actionType = null,
        CancellationToken ct = default
    )
    {
        AuditLog auditLog = await GetAuditLogsAsync(guild.Id, limit, after, before, userId, actionType);
        return AuditLogParser.ParseAuditLogToEntriesAsync(guild, auditLog, ct);
    }

    internal async ValueTask<AuditLog> GetAuditLogsAsync
    (
        ulong guildId,
        int limit,
        ulong? after = null,
        ulong? before = null,
        ulong? userId = null,
        DiscordAuditLogActionType? actionType = null
    )
    {
        QueryUriBuilder builder = new($"{Endpoints.Guilds}/{guildId}/{Endpoints.AuditLogs}");

        builder.AddParameter("limit", limit.ToString(CultureInfo.InvariantCulture));

        if (after is not null)
        {
            builder.AddParameter("after", after.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (before is not null)
        {
            builder.AddParameter("before", before.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (userId is not null)
        {
            builder.AddParameter("user_id", userId.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (actionType is not null)
        {
            builder.AddParameter("action_type", ((int)actionType.Value).ToString(CultureInfo.InvariantCulture));
        }

        RestRequest request = new()
        {
            Route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.AuditLogs}",
            Url = builder.Build(),
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        return JsonConvert.DeserializeObject<AuditLog>(res.Response!)!;
    }

    public async ValueTask<DiscordInvite> GetGuildVanityUrlAsync
    (
        ulong guildId
    )
    {
        RestRequest request = new()
        {
            Route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.VanityUrl}",
            Url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.VanityUrl}",
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        return JsonConvert.DeserializeObject<DiscordInvite>(res.Response!)!;
    }

    public async ValueTask<DiscordWidget> GetGuildWidgetAsync
    (
        ulong guildId
    )
    {
        RestRequest request = new()
        {
            Route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.WidgetJson}",
            Url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.WidgetJson}",
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        // TODO: this should really be cleaned up
        JObject json = JObject.Parse(res.Response!);
        JArray rawChannels = (JArray)json["channels"]!;

        DiscordWidget ret = json.ToDiscordObject<DiscordWidget>();
        ret.Discord = this.discord!;
        ret.Guild = this.discord!.Guilds[guildId];

        ret.Channels = ret.Guild is null
            ? rawChannels.Select(r => new DiscordChannel
            {
                Id = (ulong)r["id"]!,
                Name = r["name"]!.ToString(),
                Position = (int)r["position"]!
            }).ToList()
            : rawChannels.Select(r =>
            {
                DiscordChannel c = ret.Guild.GetChannel((ulong)r["id"]!);
                c.Position = (int)r["position"]!;
                return c;
            }).ToList();

        return ret;
    }

    public async ValueTask<DiscordWidgetSettings> GetGuildWidgetSettingsAsync
    (
        ulong guildId
    )
    {
        RestRequest request = new()
        {
            Route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Widget}",
            Url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Widget}",
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        DiscordWidgetSettings ret = JsonConvert.DeserializeObject<DiscordWidgetSettings>(res.Response!)!;
        ret.Guild = this.discord!.Guilds[guildId];

        return ret;
    }

    public async ValueTask<DiscordWidgetSettings> ModifyGuildWidgetSettingsAsync
    (
        ulong guildId,
        bool? isEnabled = null,
        ulong? channelId = null,
        string? reason = null
    )
    {
        RestGuildWidgetSettingsPayload payload = new()
        {
            Enabled = isEnabled,
            ChannelId = channelId
        };

        RestRequest request = new()
        {
            Route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Widget}",
            Url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Widget}",
            Method = HttpMethod.Patch,
            Payload = DiscordJson.SerializeObject(payload),
            Headers = reason is null
                ? null
                : new Dictionary<string, string>
                {
                    [ReasonHeaderName] = reason
                }
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        DiscordWidgetSettings ret = JsonConvert.DeserializeObject<DiscordWidgetSettings>(res.Response!)!;
        ret.Guild = this.discord!.Guilds[guildId];

        return ret;
    }

    public async ValueTask<IReadOnlyList<DiscordGuildTemplate>> GetGuildTemplatesAsync
    (
        ulong guildId
    )
    {
        RestRequest request = new()
        {
            Route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Templates}",
            Url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Templates}",
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        IEnumerable<DiscordGuildTemplate> templates =
            JsonConvert.DeserializeObject<IEnumerable<DiscordGuildTemplate>>(res.Response!)!;

        return templates.ToList();
    }

    public async ValueTask<DiscordGuildTemplate> CreateGuildTemplateAsync
    (
        ulong guildId,
        string name,
        string description
    )
    {
        RestGuildTemplateCreateOrModifyPayload payload = new()
        {
            Name = name,
            Description = description
        };

        RestRequest request = new()
        {
            Route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Templates}",
            Url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Templates}",
            Method = HttpMethod.Post,
            Payload = DiscordJson.SerializeObject(payload)
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        return JsonConvert.DeserializeObject<DiscordGuildTemplate>(res.Response!)!;
    }

    public async ValueTask<DiscordGuildTemplate> SyncGuildTemplateAsync
    (
        ulong guildId,
        string templateCode
    )
    {
        RestRequest request = new()
        {
            Route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Templates}/:template_code",
            Url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Templates}/{templateCode}",
            Method = HttpMethod.Put
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        return JsonConvert.DeserializeObject<DiscordGuildTemplate>(res.Response!)!;
    }

    public async ValueTask<DiscordGuildTemplate> ModifyGuildTemplateAsync
    (
        ulong guildId,
        string templateCode,
        string? name = null,
        string? description = null
    )
    {
        RestGuildTemplateCreateOrModifyPayload payload = new()
        {
            Name = name,
            Description = description
        };

        RestRequest request = new()
        {
            Route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Templates}/:template_code",
            Url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Templates}/{templateCode}",
            Method = HttpMethod.Patch,
            Payload = DiscordJson.SerializeObject(payload)
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        return JsonConvert.DeserializeObject<DiscordGuildTemplate>(res.Response!)!;
    }

    public async ValueTask<DiscordGuildTemplate> DeleteGuildTemplateAsync
    (
        ulong guildId,
        string templateCode
    )
    {
        RestRequest request = new()
        {
            Route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Templates}/:template_code",
            Url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Templates}/{templateCode}",
            Method = HttpMethod.Delete
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        return JsonConvert.DeserializeObject<DiscordGuildTemplate>(res.Response!)!;
    }

    public async ValueTask<DiscordGuildMembershipScreening> GetGuildMembershipScreeningFormAsync
    (
        ulong guildId
    )
    {
        RestRequest request = new()
        {
            Route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.MemberVerification}",
            Url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.MemberVerification}",
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        return JsonConvert.DeserializeObject<DiscordGuildMembershipScreening>(res.Response!)!;
    }

    public async ValueTask<DiscordGuildMembershipScreening> ModifyGuildMembershipScreeningFormAsync
    (
        ulong guildId,
        Optional<bool> enabled = default,
        Optional<DiscordGuildMembershipScreeningField[]> fields = default,
        Optional<string> description = default
    )
    {
        RestGuildMembershipScreeningFormModifyPayload payload = new()
        {
            Enabled = enabled,
            Description = description,
            Fields = fields
        };

        RestRequest request = new()
        {
            Route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.MemberVerification}",
            Url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.MemberVerification}",
            Method = HttpMethod.Patch,
            Payload = DiscordJson.SerializeObject(payload)
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        return JsonConvert.DeserializeObject<DiscordGuildMembershipScreening>(res.Response!)!;
    }

    public async ValueTask<DiscordGuildWelcomeScreen> GetGuildWelcomeScreenAsync
    (
        ulong guildId
    )
    {
        RestRequest request = new()
        {
            Route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.WelcomeScreen}",
            Url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.WelcomeScreen}",
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        return JsonConvert.DeserializeObject<DiscordGuildWelcomeScreen>(res.Response!)!;
    }

    public async ValueTask<DiscordGuildWelcomeScreen> ModifyGuildWelcomeScreenAsync
    (
        ulong guildId,
        Optional<bool> enabled = default,
        Optional<IEnumerable<DiscordGuildWelcomeScreenChannel>> welcomeChannels = default,
        Optional<string> description = default,
        string? reason = null
    )
    {
        RestGuildWelcomeScreenModifyPayload payload = new()
        {
            Enabled = enabled,
            WelcomeChannels = welcomeChannels,
            Description = description
        };

        RestRequest request = new()
        {
            Route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.WelcomeScreen}",
            Url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.WelcomeScreen}",
            Method = HttpMethod.Patch,
            Payload = DiscordJson.SerializeObject(payload),
            Headers = reason is null
                ? null
                : new Dictionary<string, string>
                {
                    [ReasonHeaderName] = reason
                }
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        return JsonConvert.DeserializeObject<DiscordGuildWelcomeScreen>(res.Response!)!;
    }

    public async ValueTask<DiscordVoiceState> GetCurrentUserVoiceStateAsync(ulong guildId)
    {
        RestRequest request = new()
        {
            Route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.VoiceStates}/:user_id",
            Url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.VoiceStates}/{Endpoints.Me}",
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        DiscordVoiceState result = JsonConvert.DeserializeObject<DiscordVoiceState>(res.Response!)!;

        result.Discord = this.discord!;

        return result;
    }
    
    public async ValueTask<DiscordVoiceState> GetUserVoiceStateAsync(ulong guildId, ulong userId)
    {
        RestRequest request = new()
        {
            Route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.VoiceStates}/:user_id",
            Url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.VoiceStates}/{userId}",
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        DiscordVoiceState result = JsonConvert.DeserializeObject<DiscordVoiceState>(res.Response!)!;

        result.Discord = this.discord!;

        return result;
    }
    
    internal async ValueTask UpdateCurrentUserVoiceStateAsync
    (
        ulong guildId,
        ulong channelId,
        bool? suppress = null,
        DateTimeOffset? requestToSpeakTimestamp = null
    )
    {
        RestGuildUpdateCurrentUserVoiceStatePayload payload = new()
        {
            ChannelId = channelId,
            Suppress = suppress,
            RequestToSpeakTimestamp = requestToSpeakTimestamp
        };

        RestRequest request = new()
        {
            Route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.VoiceStates}/@me",
            Url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.VoiceStates}/@me",
            Method = HttpMethod.Patch,
            Payload = DiscordJson.SerializeObject(payload)
        };

        _ = await this.rest.ExecuteRequestAsync(request);
    }

    public async ValueTask UpdateUserVoiceStateAsync
    (
        ulong guildId,
        ulong userId,
        ulong channelId,
        bool? suppress = null
    )
    {
        RestGuildUpdateUserVoiceStatePayload payload = new()
        {
            ChannelId = channelId,
            Suppress = suppress
        };

        RestRequest request = new()
        {
            Route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.VoiceStates}/:user_id",
            Url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.VoiceStates}/{userId}",
            Method = HttpMethod.Patch,
            Payload = DiscordJson.SerializeObject(payload)
        };

        _ = await this.rest.ExecuteRequestAsync(request);
    }
    #endregion

    #region Stickers

    public async ValueTask<DiscordMessageSticker> GetGuildStickerAsync
    (
        ulong guildId,
        ulong stickerId
    )
    {
        RestRequest request = new()
        {
            Route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Stickers}/:sticker_id",
            Url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Stickers}/{stickerId}",
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);
        JObject json = JObject.Parse(res.Response!);

        DiscordMessageSticker ret = json.ToDiscordObject<DiscordMessageSticker>();

        if (json["user"] is JObject jusr) // Null = Missing stickers perm //
        {
            TransportUser tsr = jusr.ToDiscordObject<TransportUser>();
            DiscordUser usr = new(tsr) { Discord = this.discord! };
            ret.User = usr;
        }

        ret.Discord = this.discord!;
        return ret;
    }

    public async ValueTask<DiscordMessageSticker> GetStickerAsync
    (
        ulong stickerId
    )
    {
        RestRequest request = new()
        {
            Route = $"{Endpoints.Stickers}/:sticker_id",
            Url = $"{Endpoints.Stickers}/{stickerId}",
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);
        JObject json = JObject.Parse(res.Response!);

        DiscordMessageSticker ret = json.ToDiscordObject<DiscordMessageSticker>();

        if (json["user"] is JObject jusr) // Null = Missing stickers perm //
        {
            TransportUser tsr = jusr.ToDiscordObject<TransportUser>();
            DiscordUser usr = new(tsr) { Discord = this.discord! };
            ret.User = usr;
        }

        ret.Discord = this.discord!;
        return ret;
    }

    public async ValueTask<IReadOnlyList<DiscordMessageStickerPack>> GetStickerPacksAsync()
    {
        RestRequest request = new()
        {
            Route = $"{Endpoints.Stickerpacks}",
            Url = $"{Endpoints.Stickerpacks}",
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        JArray json = (JArray)JObject.Parse(res.Response!)["sticker_packs"]!;
        DiscordMessageStickerPack[] ret = json.ToDiscordObject<DiscordMessageStickerPack[]>();

        return ret;
    }

    public async ValueTask<IReadOnlyList<DiscordMessageSticker>> GetGuildStickersAsync
    (
        ulong guildId
    )
    {
        RestRequest request = new()
        {
            Route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Stickers}",
            Url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Stickers}",
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);
        JArray json = JArray.Parse(res.Response!);

        DiscordMessageSticker[] ret = json.ToDiscordObject<DiscordMessageSticker[]>();

        for (int i = 0; i < ret.Length; i++)
        {
            DiscordMessageSticker sticker = ret[i];
            sticker.Discord = this.discord!;

            if (json[i]["user"] is JObject jusr) // Null = Missing stickers perm //
            {
                TransportUser transportUser = jusr.ToDiscordObject<TransportUser>();
                DiscordUser user = new(transportUser)
                {
                    Discord = this.discord!
                };

                // The sticker would've already populated, but this is just to ensure everything is up to date
                sticker.User = user;
            }
        }

        return ret;
    }

    public async ValueTask<DiscordMessageSticker> CreateGuildStickerAsync
    (
        ulong guildId,
        string name,
        string description,
        string tags,
        DiscordMessageFile file,
        string? reason = null
    )
    {
        MultipartRestRequest request = new()
        {
            Route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Stickers}",
            Url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Stickers}",
            Method = HttpMethod.Post,
            Headers = reason is null
                ? null
                : new Dictionary<string, string>()
                {
                    [ReasonHeaderName] = reason
                },
            Files = new DiscordMessageFile[]
            {
                file
            },
            Values = new Dictionary<string, string>()
            {
                ["name"] = name,
                ["description"] = description,
                ["tags"] = tags,
            }
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);
        JObject json = JObject.Parse(res.Response!);

        DiscordMessageSticker ret = json.ToDiscordObject<DiscordMessageSticker>();

        if (json["user"] is JObject rawUser) // Null = Missing stickers perm //
        {
            TransportUser transportUser = rawUser.ToDiscordObject<TransportUser>();

            DiscordUser user = new(transportUser)
            {
                Discord = this.discord!
            };

            ret.User = user;
        }

        ret.Discord = this.discord!;

        return ret;
    }

    public async ValueTask<DiscordMessageSticker> ModifyStickerAsync
    (
        ulong guildId,
        ulong stickerId,
        Optional<string> name = default,
        Optional<string> description = default,
        Optional<string> tags = default,
        string? reason = null
    )
    {
        RestStickerModifyPayload payload = new()
        {
            Name = name,
            Description = description,
            Tags = tags
        };

        RestRequest request = new()
        {
            Route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Stickers}/:sticker_id",
            Url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Stickers}/{stickerId}",
            Method = HttpMethod.Patch,
            Payload = DiscordJson.SerializeObject(payload),
            Headers = reason is null
                ? null
                : new Dictionary<string, string>
                {
                    [ReasonHeaderName] = reason
                }
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);
        DiscordMessageSticker ret = JObject.Parse(res.Response!).ToDiscordObject<DiscordMessageSticker>();
        ret.Discord = this.discord!;

        return ret;
    }

    public async ValueTask DeleteStickerAsync
    (
        ulong guildId,
        ulong stickerId,
        string? reason = null
    )
    {
        RestRequest request = new()
        {
            Route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Stickers}/:sticker_id",
            Url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Stickers}/{stickerId}",
            Method = HttpMethod.Delete,
            Headers = reason is null
                ? null
                : new Dictionary<string, string>()
                {
                    [ReasonHeaderName] = reason
                }
        };

        await this.rest.ExecuteRequestAsync(request);
    }

    #endregion

    #region Channel
    public async ValueTask<DiscordChannel> CreateGuildChannelAsync
    (
        ulong guildId,
        string name,
        DiscordChannelType type,
        ulong? parent,
        Optional<string> topic,
        int? bitrate,
        int? userLimit,
        IEnumerable<DiscordOverwriteBuilder>? overwrites,
        bool? nsfw,
        Optional<int?> perUserRateLimit,
        DiscordVideoQualityMode? qualityMode,
        int? position,
        string reason,
        DiscordAutoArchiveDuration? defaultAutoArchiveDuration,
        DefaultReaction? defaultReactionEmoji,
        IEnumerable<DiscordForumTagBuilder>? forumTags,
        DiscordDefaultSortOrder? defaultSortOrder

    )
    {
        List<DiscordRestOverwrite> restOverwrites = [];
        if (overwrites != null)
        {
            foreach (DiscordOverwriteBuilder ow in overwrites)
            {
                restOverwrites.Add(ow.Build());
            }
        }

        RestChannelCreatePayload pld = new()
        {
            Name = name,
            Type = type,
            Parent = parent,
            Topic = topic,
            Bitrate = bitrate,
            UserLimit = userLimit,
            PermissionOverwrites = restOverwrites,
            Nsfw = nsfw,
            PerUserRateLimit = perUserRateLimit,
            QualityMode = qualityMode,
            Position = position,
            DefaultAutoArchiveDuration = defaultAutoArchiveDuration,
            DefaultReaction = defaultReactionEmoji,
            AvailableTags = forumTags,
            DefaultSortOrder = defaultSortOrder
        };

        Dictionary<string, string> headers = [];
        if (!string.IsNullOrWhiteSpace(reason))
        {
            headers[ReasonHeaderName] = reason;
        }

        RestRequest request = new()
        {
            Route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Channels}",
            Url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Channels}",
            Method = HttpMethod.Post,
            Payload = DiscordJson.SerializeObject(pld),
            Headers = headers
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        DiscordChannel ret = JsonConvert.DeserializeObject<DiscordChannel>(res.Response!)!;
        ret.Discord = this.discord!;

        foreach (DiscordOverwrite xo in ret.permissionOverwrites)
        {
            xo.Discord = this.discord!;
            xo.channelId = ret.Id;
        }

        return ret;
    }

    public async ValueTask ModifyChannelAsync
    (
        ulong channelId,
        string name,
        int? position = null,
        Optional<string> topic = default,
        bool? nsfw = null,
        Optional<ulong?> parent = default,
        int? bitrate = null,
        int? userLimit = null,
        Optional<int?> perUserRateLimit = default,
        Optional<string> rtcRegion = default,
        DiscordVideoQualityMode? qualityMode = null,
        Optional<DiscordChannelType> type = default,
        IEnumerable<DiscordOverwriteBuilder>? permissionOverwrites = null,
        Optional<DiscordChannelFlags> flags = default,
        IEnumerable<DiscordForumTagBuilder>? availableTags = null,
        Optional<DiscordAutoArchiveDuration?> defaultAutoArchiveDuration = default,
        Optional<DefaultReaction?> defaultReactionEmoji = default,
        Optional<int> defaultPerUserRatelimit = default,
        Optional<DiscordDefaultSortOrder?> defaultSortOrder = default,
        Optional<DiscordDefaultForumLayout> defaultForumLayout = default,
        string? reason = null
    )
    {
        List<DiscordRestOverwrite>? restOverwrites = null;
        if (permissionOverwrites is not null)
        {
            restOverwrites = [];
            foreach (DiscordOverwriteBuilder ow in permissionOverwrites)
            {
                restOverwrites.Add(ow.Build());
            }
        }

        RestChannelModifyPayload pld = new()
        {
            Name = name,
            Position = position,
            Topic = topic,
            Nsfw = nsfw,
            Parent = parent,
            Bitrate = bitrate,
            UserLimit = userLimit,
            PerUserRateLimit = perUserRateLimit,
            RtcRegion = rtcRegion,
            QualityMode = qualityMode,
            Type = type,
            PermissionOverwrites = restOverwrites,
            Flags = flags,
            AvailableTags = availableTags,
            DefaultAutoArchiveDuration = defaultAutoArchiveDuration,
            DefaultReaction = defaultReactionEmoji,
            DefaultPerUserRateLimit = defaultPerUserRatelimit,
            DefaultForumLayout = defaultForumLayout,
            DefaultSortOrder = defaultSortOrder
        };

        Dictionary<string, string> headers = [];
        if (!string.IsNullOrWhiteSpace(reason))
        {
            headers[ReasonHeaderName] = reason;
        }

        RestRequest request = new()
        {
            Route = $"{Endpoints.Channels}/{channelId}",
            Url = $"{Endpoints.Channels}/{channelId}",
            Method = HttpMethod.Patch,
            Payload = DiscordJson.SerializeObject(pld),
            Headers = headers
        };

        await this.rest.ExecuteRequestAsync(request);
    }

    public async ValueTask ModifyThreadChannelAsync
    (
        ulong channelId,
        string name,
        int? position = null,
        Optional<string> topic = default,
        bool? nsfw = null,
        Optional<ulong?> parent = default,
        int? bitrate = null,
        int? userLimit = null,
        Optional<int?> perUserRateLimit = default,
        Optional<string> rtcRegion = default,
        DiscordVideoQualityMode? qualityMode = null,
        Optional<DiscordChannelType> type = default,
        IEnumerable<DiscordOverwriteBuilder>? permissionOverwrites = null,
        bool? isArchived = null,
        DiscordAutoArchiveDuration? autoArchiveDuration = null,
        bool? locked = null,
        IEnumerable<ulong>? appliedTags = null,
        bool? isInvitable = null,
        string? reason = null
    )
    {
        List<DiscordRestOverwrite>? restOverwrites = null;
        if (permissionOverwrites is not null)
        {
            restOverwrites = [];
            foreach (DiscordOverwriteBuilder ow in permissionOverwrites)
            {
                restOverwrites.Add(ow.Build());
            }
        }

        RestThreadChannelModifyPayload pld = new()
        {
            Name = name,
            Position = position,
            Topic = topic,
            Nsfw = nsfw,
            Parent = parent,
            Bitrate = bitrate,
            UserLimit = userLimit,
            PerUserRateLimit = perUserRateLimit,
            RtcRegion = rtcRegion,
            QualityMode = qualityMode,
            Type = type,
            PermissionOverwrites = restOverwrites,
            IsArchived = isArchived,
            ArchiveDuration = autoArchiveDuration,
            Locked = locked,
            IsInvitable = isInvitable,
            AppliedTags = appliedTags
        };

        Dictionary<string, string> headers = [];
        if (!string.IsNullOrWhiteSpace(reason))
        {
            headers.Add(ReasonHeaderName, reason);
        }

        RestRequest request = new()
        {
            Route = $"{Endpoints.Channels}/{channelId}",
            Url = $"{Endpoints.Channels}/{channelId}",
            Method = HttpMethod.Patch,
            Payload = DiscordJson.SerializeObject(pld),
            Headers = headers
        };

        await this.rest.ExecuteRequestAsync(request);
    }

    public async ValueTask<IReadOnlyList<DiscordScheduledGuildEvent>> GetScheduledGuildEventsAsync
    (
        ulong guildId,
        bool withUserCounts = false
    )
    {
        QueryUriBuilder url = new($"{Endpoints.Guilds}/{guildId}/{Endpoints.Events}");
        url.AddParameter("with_user_count", withUserCounts.ToString());

        RestRequest request = new()
        {
            Route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Events}",
            Url = url.Build(),
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        DiscordScheduledGuildEvent[] ret = JsonConvert.DeserializeObject<DiscordScheduledGuildEvent[]>(res.Response!)!;

        foreach (DiscordScheduledGuildEvent? scheduledGuildEvent in ret)
        {
            scheduledGuildEvent.Discord = this.discord!;

            if (scheduledGuildEvent.Creator is not null)
            {
                scheduledGuildEvent.Creator.Discord = this.discord!;
            }
        }

        return ret.AsReadOnly();
    }

    public async ValueTask<DiscordScheduledGuildEvent> CreateScheduledGuildEventAsync
    (
        ulong guildId,
        string name,
        string description,
        DateTimeOffset startTime,
        DiscordScheduledGuildEventType type,
        DiscordScheduledGuildEventPrivacyLevel privacyLevel,
        DiscordScheduledGuildEventMetadata? metadata = null,
        DateTimeOffset? endTime = null,
        ulong? channelId = null,
        Stream? image = null,
        string? reason = null
    )
    {
        Dictionary<string, string> headers = [];

        if (!string.IsNullOrWhiteSpace(reason))
        {
            headers[ReasonHeaderName] = reason;
        }

        RestScheduledGuildEventCreatePayload pld = new()
        {
            Name = name,
            Description = description,
            ChannelId = channelId,
            StartTime = startTime,
            EndTime = endTime,
            Type = type,
            PrivacyLevel = privacyLevel,
            Metadata = metadata
        };

        if (image is not null)
        {
            using InlineMediaTool imageTool = new(image);

            pld.CoverImage = imageTool.GetBase64();
        }

        RestRequest request = new()
        {
            Route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Events}",
            Url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Events}",
            Method = HttpMethod.Post,
            Payload = DiscordJson.SerializeObject(pld),
            Headers = headers
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        DiscordScheduledGuildEvent ret = JsonConvert.DeserializeObject<DiscordScheduledGuildEvent>(res.Response!)!;

        ret.Discord = this.discord!;

        if (ret.Creator is not null)
        {
            ret.Creator.Discord = this.discord!;
        }

        return ret;
    }

    public async ValueTask DeleteScheduledGuildEventAsync
    (
        ulong guildId,
        ulong guildScheduledEventId,
        string? reason = null
    )
    {
        RestRequest request = new()
        {
            Route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Events}/:guild_scheduled_event_id",
            Url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Events}/{guildScheduledEventId}",
            Method = HttpMethod.Delete,
            Headers = new Dictionary<string, string>
            {
                [ReasonHeaderName] = reason
            }
        };

        await this.rest.ExecuteRequestAsync(request);
    }

    public async ValueTask<IReadOnlyList<DiscordUser>> GetScheduledGuildEventUsersAsync
    (
        ulong guildId,
        ulong guildScheduledEventId,
        bool withMembers = false,
        int limit = 100,
        ulong? before = null,
        ulong? after = null
    )
    {
        string route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Events}/:guild_scheduled_event_id/{Endpoints.Users}";

        QueryUriBuilder url = new($"{Endpoints.Guilds}/{guildId}/{Endpoints.Events}/{guildScheduledEventId}/{Endpoints.Users}");

        url.AddParameter("with_members", withMembers.ToString());

        if (limit > 0)
        {
            url.AddParameter("limit", limit.ToString(CultureInfo.InvariantCulture));
        }

        if (before != null)
        {
            url.AddParameter("before", before.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (after != null)
        {
            url.AddParameter("after", after.Value.ToString(CultureInfo.InvariantCulture));
        }

        RestRequest request = new()
        {
            Route = route,
            Url = url.Build(),
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        JToken jto = JToken.Parse(res.Response!);

        return (jto as JArray ?? jto["users"] as JArray)!
            .Select
            (
                j => (DiscordUser)j.SelectToken("member")?.ToDiscordObject<DiscordMember>()!
                    ?? j.SelectToken("user")!.ToDiscordObject<DiscordUser>()
            )
            .ToArray();
    }

    public async ValueTask<DiscordScheduledGuildEvent> GetScheduledGuildEventAsync
    (
        ulong guildId,
        ulong guildScheduledEventId
    )
    {
        string route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Events}/:guild_scheduled_event_id";
        string url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Events}/{guildScheduledEventId}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        DiscordScheduledGuildEvent ret = JsonConvert.DeserializeObject<DiscordScheduledGuildEvent>(res.Response!)!;

        ret.Discord = this.discord!;

        if (ret.Creator is not null)
        {
            ret.Creator.Discord = this.discord!;
        }

        return ret;
    }

    public async ValueTask<DiscordScheduledGuildEvent> ModifyScheduledGuildEventAsync
    (
        ulong guildId,
        ulong guildScheduledEventId,
        Optional<string> name = default,
        Optional<string> description = default,
        Optional<ulong?> channelId = default,
        Optional<DateTimeOffset> startTime = default,
        Optional<DateTimeOffset> endTime = default,
        Optional<DiscordScheduledGuildEventType> type = default,
        Optional<DiscordScheduledGuildEventPrivacyLevel> privacyLevel = default,
        Optional<DiscordScheduledGuildEventMetadata> metadata = default,
        Optional<DiscordScheduledGuildEventStatus> status = default,
        Optional<Stream> coverImage = default,
        string? reason = null
    )
    {
        string route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Events}/:guild_scheduled_event_id";
        string url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Events}/{guildScheduledEventId}";

        Dictionary<string, string> headers = [];
        if (!string.IsNullOrWhiteSpace(reason))
        {
            headers[ReasonHeaderName] = reason;
        }

        RestScheduledGuildEventModifyPayload pld = new()
        {
            Name = name,
            Description = description,
            ChannelId = channelId,
            StartTime = startTime,
            EndTime = endTime,
            Type = type,
            PrivacyLevel = privacyLevel,
            Metadata = metadata,
            Status = status
        };

        if (coverImage.HasValue)
        {
            using InlineMediaTool imageTool = new(coverImage.Value);

            pld.CoverImage = imageTool.GetBase64();
        }

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Patch,
            Payload = DiscordJson.SerializeObject(pld),
            Headers = headers
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        DiscordScheduledGuildEvent ret = JsonConvert.DeserializeObject<DiscordScheduledGuildEvent>(res.Response!)!;

        ret.Discord = this.discord!;

        if (ret.Creator is not null)
        {
            ret.Creator.Discord = this.discord!;
        }

        return ret;
    }

    public async ValueTask<DiscordChannel> GetChannelAsync
    (
        ulong channelId
    )
    {
        string route = $"{Endpoints.Channels}/{channelId}";
        string url = $"{Endpoints.Channels}/{channelId}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        DiscordChannel ret = JsonConvert.DeserializeObject<DiscordChannel>(res.Response!)!;

        // this is really weird, we should consider doing this better
        if (ret.IsThread)
        {
            ret = JsonConvert.DeserializeObject<DiscordThreadChannel>(res.Response!)!;
        }

        ret.Discord = this.discord!;
        foreach (DiscordOverwrite xo in ret.permissionOverwrites)
        {
            xo.Discord = this.discord!;
            xo.channelId = ret.Id;
        }

        return ret;
    }

    public async ValueTask DeleteChannelAsync
    (
        ulong channelId,
        string reason
    )
    {
        Dictionary<string, string> headers = [];
        if (!string.IsNullOrWhiteSpace(reason))
        {
            headers[ReasonHeaderName] = reason;
        }

        RestRequest request = new()
        {
            Route = $"{Endpoints.Channels}/{channelId}",
            Url = new($"{Endpoints.Channels}/{channelId}"),
            Method = HttpMethod.Delete,
            Headers = headers
        };

        await this.rest.ExecuteRequestAsync(request);
    }

    public async ValueTask<DiscordMessage> GetMessageAsync
    (
        ulong channelId,
        ulong messageId
    )
    {
        string route = $"{Endpoints.Channels}/{channelId}/{Endpoints.Messages}/:message_id";
        string url = $"{Endpoints.Channels}/{channelId}/{Endpoints.Messages}/{messageId}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        DiscordMessage ret = PrepareMessage(JObject.Parse(res.Response!));

        return ret;
    }

    public async ValueTask<DiscordMessage> ForwardMessageAsync(ulong channelId, ulong originChannelId, ulong messageId)
    {
        RestChannelMessageCreatePayload pld = new()
        {
            HasContent = false,
            MessageReference = new InternalDiscordMessageReference
            {
                MessageId = messageId,
                ChannelId = originChannelId,
                Type = DiscordMessageReferenceType.Forward
            }
        };

        string route = $"{Endpoints.Channels}/{channelId}/{Endpoints.Messages}";
        string url = $"{Endpoints.Channels}/{channelId}/{Endpoints.Messages}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Post,
            Payload = DiscordJson.SerializeObject(pld)
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        DiscordMessage ret = PrepareMessage(JObject.Parse(res.Response!));

        return ret;
    }

    public async ValueTask<DiscordMessage> CreateMessageAsync
    (
        ulong channelId,
        string? content,
        IEnumerable<DiscordEmbed>? embeds,
        ulong? replyMessageId,
        bool mentionReply,
        bool failOnInvalidReply,
        bool suppressNotifications
    )
    {
        if (content != null && content.Length > 2000)
        {
            throw new ArgumentException("Message content length cannot exceed 2000 characters.");
        }

        if (!embeds?.Any() ?? true)
        {
            if (content == null)
            {
                throw new ArgumentException("You must specify message content or an embed.");
            }

            if (content.Length == 0)
            {
                throw new ArgumentException("Message content must not be empty.");
            }
        }

        if (embeds is not null)
        {
            foreach (DiscordEmbed embed in embeds)
            {
                if (embed.Title?.Length > 256)
                {
                    throw new ArgumentException("Embed title length must not exceed 256 characters.");
                }

                if (embed.Description?.Length > 4096)
                {
                    throw new ArgumentException("Embed description length must not exceed 4096 characters.");
                }

                if (embed.Fields?.Count > 25)
                {
                    throw new ArgumentException("Embed field count must not exceed 25.");
                }

                if (embed.Fields is not null)
                {
                    foreach (DiscordEmbedField field in embed.Fields)
                    {
                        if (field.Name.Length > 256)
                        {
                            throw new ArgumentException("Embed field name length must not exceed 256 characters.");
                        }

                        if (field.Value.Length > 1024)
                        {
                            throw new ArgumentException("Embed field value length must not exceed 1024 characters.");
                        }
                    }
                }

                if (embed.Footer?.Text.Length > 2048)
                {
                    throw new ArgumentException("Embed footer text length must not exceed 2048 characters.");
                }

                if (embed.Author?.Name.Length > 256)
                {
                    throw new ArgumentException("Embed author name length must not exceed 256 characters.");
                }

                int totalCharacter = 0;
                totalCharacter += embed.Title?.Length ?? 0;
                totalCharacter += embed.Description?.Length ?? 0;
                totalCharacter += embed.Fields?.Sum(xf => xf.Name.Length + xf.Value.Length) ?? 0;
                totalCharacter += embed.Footer?.Text.Length ?? 0;
                totalCharacter += embed.Author?.Name.Length ?? 0;
                if (totalCharacter > 6000)
                {
                    throw new ArgumentException("Embed total length must not exceed 6000 characters.");
                }

                if (embed.Timestamp != null)
                {
                    embed.Timestamp = embed.Timestamp.Value.ToUniversalTime();
                }
            }
        }

        RestChannelMessageCreatePayload pld = new()
        {
            HasContent = content != null,
            Content = content,
            IsTts = false,
            HasEmbed = embeds?.Any() ?? false,
            Embeds = embeds,
            Flags = suppressNotifications ? DiscordMessageFlags.SuppressNotifications : 0,
        };

        if (replyMessageId != null)
        {
            pld.MessageReference = new InternalDiscordMessageReference
            {
                MessageId = replyMessageId,
                FailIfNotExists = failOnInvalidReply
            };
        }

        if (replyMessageId != null)
        {
            pld.Mentions = new DiscordMentions(Mentions.All, mentionReply);
        }

        string route = $"{Endpoints.Channels}/{channelId}/{Endpoints.Messages}";
        string url = $"{Endpoints.Channels}/{channelId}/{Endpoints.Messages}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Post,
            Payload = DiscordJson.SerializeObject(pld)
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        DiscordMessage ret = PrepareMessage(JObject.Parse(res.Response!));

        return ret;
    }

    public async ValueTask<DiscordMessage> CreateMessageAsync
    (
        ulong channelId,
        DiscordMessageBuilder builder
    )
    {
        builder.Validate();

        if (builder.Embeds != null)
        {
            foreach (DiscordEmbed embed in builder.Embeds)
            {
                if (embed?.Timestamp != null)
                {
                    embed.Timestamp = embed.Timestamp.Value.ToUniversalTime();
                }
            }
        }

        RestChannelMessageCreatePayload pld = new()
        {
            HasContent = builder.Content != null,
            Content = builder.Content,
            StickersIds = builder.stickers?.Where(s => s != null).Select(s => s.Id).ToArray(),
            IsTts = builder.IsTts,
            HasEmbed = builder.Embeds != null,
            Embeds = builder.Embeds,
            Components = builder.Components,
            Flags = builder.Flags,
            Poll = builder.Poll?.BuildInternal(),
        };

        if (builder.ReplyId != null)
        {
            pld.MessageReference = new InternalDiscordMessageReference { MessageId = builder.ReplyId, FailIfNotExists = builder.FailOnInvalidReply };
        }

        pld.Mentions = new DiscordMentions(builder.Mentions ?? Mentions.None, builder.MentionOnReply);

        if (builder.Files.Count == 0)
        {
            string route = $"{Endpoints.Channels}/{channelId}/{Endpoints.Messages}";
            string url = $"{Endpoints.Channels}/{channelId}/{Endpoints.Messages}";

            RestRequest request = new()
            {
                Route = route,
                Url = url,
                Method = HttpMethod.Post,
                Payload = DiscordJson.SerializeObject(pld)
            };

            RestResponse res = await this.rest.ExecuteRequestAsync(request);

            DiscordMessage ret = PrepareMessage(JObject.Parse(res.Response!));

            return ret;
        }
        else
        {
            Dictionary<string, string> values = new()
            {
                ["payload_json"] = DiscordJson.SerializeObject(pld)
            };

            string route = $"{Endpoints.Channels}/{channelId}/{Endpoints.Messages}";
            string url = $"{Endpoints.Channels}/{channelId}/{Endpoints.Messages}";

            MultipartRestRequest request = new()
            {
                Route = route,
                Url = url,
                Method = HttpMethod.Post,
                Values = values,
                Files = builder.Files
            };

            RestResponse res;
            try
            {
                res = await this.rest.ExecuteRequestAsync(request);
            }
            finally
            {
                builder.ResetFileStreamPositions();
            }

            return PrepareMessage(JObject.Parse(res.Response!));
        }
    }

    public async ValueTask<IReadOnlyList<DiscordChannel>> GetGuildChannelsAsync(ulong guildId)
    {
        string route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Channels}";
        string url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Channels}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        List<DiscordChannel> channels = JsonConvert.DeserializeObject<IEnumerable<DiscordChannel>>(res.Response!)!
            .Select
            (
                xc =>
                {
                    xc.Discord = this.discord!;
                    return xc;
                }
            ).ToList();

        foreach (DiscordChannel? ret in channels)
        {
            foreach (DiscordOverwrite xo in ret.permissionOverwrites)
            {
                xo.Discord = this.discord!;
                xo.channelId = ret.Id;
            }
        }

        return channels;
    }

    public async ValueTask<IReadOnlyList<DiscordMessage>> GetChannelMessagesAsync
    (
        ulong channelId,
        int limit,
        ulong? before = null,
        ulong? after = null,
        ulong? around = null
    )
    {
        QueryUriBuilder url = new($"{Endpoints.Channels}/{channelId}/{Endpoints.Messages}");
        if (around is not null)
        {
            url.AddParameter("around", around?.ToString(CultureInfo.InvariantCulture));
        }

        if (before is not null)
        {
            url.AddParameter("before", before?.ToString(CultureInfo.InvariantCulture));
        }

        if (after is not null)
        {
            url.AddParameter("after", after?.ToString(CultureInfo.InvariantCulture));
        }

        if (limit > 0)
        {
            url.AddParameter("limit", limit.ToString(CultureInfo.InvariantCulture));
        }

        string route = $"{Endpoints.Channels}/{channelId}/{Endpoints.Messages}";

        RestRequest request = new()
        {
            Route = route,
            Url = url.Build(),
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        JArray msgsRaw = JArray.Parse(res.Response!);
        List<DiscordMessage> msgs = [];
        msgs.AddRange(msgsRaw.Select(PrepareMessage));

        return msgs;
    }

    public async ValueTask<DiscordMessage> GetChannelMessageAsync
    (
        ulong channelId,
        ulong messageId
    )
    {
        string route = $"{Endpoints.Channels}/{channelId}/{Endpoints.Messages}/:message_id";
        string url = $"{Endpoints.Channels}/{channelId}/{Endpoints.Messages}/{messageId}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        DiscordMessage ret = PrepareMessage(JObject.Parse(res.Response!));

        return ret;
    }

    public async ValueTask<DiscordMessage> EditMessageAsync
    (
        ulong channelId,
        ulong messageId,
        Optional<string> content = default,
        Optional<IEnumerable<DiscordEmbed>> embeds = default,
        Optional<IEnumerable<IMention>> mentions = default,
        IReadOnlyList<DiscordComponent>? components = null,
        IReadOnlyList<DiscordMessageFile>? files = null,
        DiscordMessageFlags? flags = null,
        IEnumerable<DiscordAttachment>? attachments = null
    )
    {
        if (embeds.HasValue && embeds.Value != null)
        {
            foreach (DiscordEmbed embed in embeds.Value)
            {
                if (embed.Timestamp != null)
                {
                    embed.Timestamp = embed.Timestamp.Value.ToUniversalTime();
                }
            }
        }

        RestChannelMessageEditPayload pld = new()
        {
            HasContent = content.HasValue,
            Content = content.HasValue ? (string)content : null,
            HasEmbed = embeds.HasValue && (embeds.Value?.Any() ?? false),
            Embeds = embeds.HasValue && (embeds.Value?.Any() ?? false) ? embeds.Value : null,
            Components = components,
            Flags = flags,
            Attachments = attachments,
            Mentions = mentions.HasValue
                ? new DiscordMentions
                (
                    mentions.Value ?? Mentions.None,
                    mentions.Value?.OfType<RepliedUserMention>().Any() ?? false
                )
                : null
        };

        string payload = DiscordJson.SerializeObject(pld);

        string route = $"{Endpoints.Channels}/{channelId}/{Endpoints.Messages}/:message_id";
        string url = $"{Endpoints.Channels}/{channelId}/{Endpoints.Messages}/{messageId}";

        RestResponse res;

        if (files is not null)
        {
            MultipartRestRequest request = new()
            {
                Route = route,
                Url = url,
                Method = HttpMethod.Patch,
                Values = new Dictionary<string, string>()
                {
                    ["payload_json"] = payload
                },
                Files = (IReadOnlyList<DiscordMessageFile>)files
            };

            res = await this.rest.ExecuteRequestAsync(request);
        }
        else
        {
            RestRequest request = new()
            {
                Route = route,
                Url = url,
                Method = HttpMethod.Patch,
                Payload = payload
            };

            res = await this.rest.ExecuteRequestAsync(request);
        }

        DiscordMessage ret = PrepareMessage(JObject.Parse(res.Response!));

        if (files is not null)
        {
            foreach (DiscordMessageFile file in files.Where(x => x.ResetPositionTo.HasValue))
            {
                file.Stream.Position = file.ResetPositionTo!.Value;
            }
        }

        return ret;
    }

    public async ValueTask DeleteMessageAsync
    (
        ulong channelId,
        ulong messageId,
        string? reason
    )
    {
        Dictionary<string, string> headers = [];
        if (!string.IsNullOrWhiteSpace(reason))
        {
            headers[ReasonHeaderName] = reason;
        }

        string route = $"{Endpoints.Channels}/{channelId}/{Endpoints.Messages}/:message_id";
        string url = $"{Endpoints.Channels}/{channelId}/{Endpoints.Messages}/{messageId}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Delete,
            Headers = headers
        };

        await this.rest.ExecuteRequestAsync(request);
    }

    public async ValueTask DeleteMessagesAsync
    (
        ulong channelId,
        IEnumerable<ulong> messageIds,
        string reason
    )
    {
        RestChannelMessageBulkDeletePayload pld = new()
        {
            Messages = messageIds
        };

        Dictionary<string, string> headers = [];
        if (!string.IsNullOrWhiteSpace(reason))
        {
            headers[ReasonHeaderName] = reason;
        }

        string route = $"{Endpoints.Channels}/{channelId}/{Endpoints.Messages}/{Endpoints.BulkDelete}";
        string url = $"{Endpoints.Channels}/{channelId}/{Endpoints.Messages}/{Endpoints.BulkDelete}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Post,
            Payload = DiscordJson.SerializeObject(pld),
            Headers = headers
        };

        await this.rest.ExecuteRequestAsync(request);
    }

    public async ValueTask<IReadOnlyList<DiscordInvite>> GetChannelInvitesAsync
    (
        ulong channelId
    )
    {
        string route = $"{Endpoints.Channels}/{channelId}/{Endpoints.Invites}";
        string url = $"{Endpoints.Channels}/{channelId}/{Endpoints.Invites}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        List<DiscordInvite> invites = JsonConvert.DeserializeObject<IEnumerable<DiscordInvite>>(res.Response!)!
            .Select
            (
                xi =>
                {
                    xi.Discord = this.discord!;
                    return xi;
                }
            )
            .ToList();

        return invites;
    }

    public async ValueTask<DiscordInvite> CreateChannelInviteAsync
    (
        ulong channelId,
        int maxAge,
        int maxUses,
        bool temporary,
        bool unique,
        string reason,
        DiscordInviteTargetType? targetType = null,
        ulong? targetUserId = null,
        ulong? targetApplicationId = null
    )
    {
        RestChannelInviteCreatePayload pld = new()
        {
            MaxAge = maxAge,
            MaxUses = maxUses,
            Temporary = temporary,
            Unique = unique,
            TargetType = targetType,
            TargetUserId = targetUserId,
            TargetApplicationId = targetApplicationId
        };

        Dictionary<string, string> headers = [];
        if (!string.IsNullOrWhiteSpace(reason))
        {
            headers[ReasonHeaderName] = reason;
        }

        string route = $"{Endpoints.Channels}/{channelId}/{Endpoints.Invites}";
        string url = $"{Endpoints.Channels}/{channelId}/{Endpoints.Invites}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Post,
            Payload = DiscordJson.SerializeObject(pld),
            Headers = headers
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        DiscordInvite ret = JsonConvert.DeserializeObject<DiscordInvite>(res.Response!)!;
        ret.Discord = this.discord!;

        return ret;
    }

    public async ValueTask DeleteChannelPermissionAsync
    (
        ulong channelId,
        ulong overwriteId,
        string reason
    )
    {
        Dictionary<string, string> headers = [];
        if (!string.IsNullOrWhiteSpace(reason))
        {
            headers[ReasonHeaderName] = reason;
        }

        string route = $"{Endpoints.Channels}/{channelId}/{Endpoints.Permissions}/:overwrite_id";
        string url = $"{Endpoints.Channels}/{channelId}/{Endpoints.Permissions}/{overwriteId}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Delete,
            Headers = headers
        };

        await this.rest.ExecuteRequestAsync(request);
    }

    public async ValueTask EditChannelPermissionsAsync
    (
        ulong channelId,
        ulong overwriteId,
        DiscordPermissions allow,
        DiscordPermissions deny,
        string type,
        string? reason = null
    )
    {
        RestChannelPermissionEditPayload pld = new()
        {
            Type = type switch
            {
                "role" => 0,
                "member" => 1,
                _ => throw new InvalidOperationException("Unrecognized permission overwrite target type.")
            },
            Allow = allow & DiscordPermissions.All,
            Deny = deny & DiscordPermissions.All
        };

        Dictionary<string, string> headers = [];
        if (!string.IsNullOrWhiteSpace(reason))
        {
            headers[ReasonHeaderName] = reason;
        }

        string route = $"{Endpoints.Channels}/{channelId}/{Endpoints.Permissions}/:overwrite_id";
        string url = $"{Endpoints.Channels}/{channelId}/{Endpoints.Permissions}/{overwriteId}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Put,
            Payload = DiscordJson.SerializeObject(pld),
            Headers = headers
        };

        await this.rest.ExecuteRequestAsync(request);
    }

    public async ValueTask TriggerTypingAsync
    (
        ulong channelId
    )
    {
        string route = $"{Endpoints.Channels}/{channelId}/{Endpoints.Typing}";
        string url = $"{Endpoints.Channels}/{channelId}/{Endpoints.Typing}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Post
        };
        await this.rest.ExecuteRequestAsync(request);
    }

    public async ValueTask<IReadOnlyList<DiscordMessage>> GetPinnedMessagesAsync
    (
        ulong channelId
    )
    {
        string route = $"{Endpoints.Channels}/{channelId}/{Endpoints.Pins}";
        string url = $"{Endpoints.Channels}/{channelId}/{Endpoints.Pins}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        JArray msgsRaw = JArray.Parse(res.Response!);
        List<DiscordMessage> msgs = [];
        foreach (JToken xj in msgsRaw)
        {
            msgs.Add(PrepareMessage(xj));
        }

        return msgs;
    }

    public async ValueTask PinMessageAsync
    (
        ulong channelId,
        ulong messageId
    )
    {
        string route = $"{Endpoints.Channels}/{channelId}/{Endpoints.Pins}/:message_id";
        string url = $"{Endpoints.Channels}/{channelId}/{Endpoints.Pins}/{messageId}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Put
        };

        await this.rest.ExecuteRequestAsync(request);
    }

    public async ValueTask UnpinMessageAsync
    (
        ulong channelId,
        ulong messageId
    )
    {
        string route = $"{Endpoints.Channels}/{channelId}/{Endpoints.Pins}/:message_id";
        string url = $"{Endpoints.Channels}/{channelId}/{Endpoints.Pins}/{messageId}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Delete
        };
        await this.rest.ExecuteRequestAsync(request);
    }

    public async ValueTask AddGroupDmRecipientAsync
    (
        ulong channelId,
        ulong userId,
        string accessToken,
        string nickname
    )
    {
        RestChannelGroupDmRecipientAddPayload pld = new()
        {
            AccessToken = accessToken,
            Nickname = nickname
        };

        string route = $"{Endpoints.Users}/{Endpoints.Me}/{Endpoints.Channels}/{channelId}/{Endpoints.Recipients}/:user_id";
        string url = $"{Endpoints.Users}/{Endpoints.Me}/{Endpoints.Channels}/{channelId}/{Endpoints.Recipients}/{userId}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Put,
            Payload = DiscordJson.SerializeObject(pld)
        };

        await this.rest.ExecuteRequestAsync(request);
    }

    public async ValueTask RemoveGroupDmRecipientAsync
    (
        ulong channelId,
        ulong userId
    )
    {
        string route = $"{Endpoints.Users}/{Endpoints.Me}/{Endpoints.Channels}/{channelId}/{Endpoints.Recipients}/:user_id";
        string url = $"{Endpoints.Users}/{Endpoints.Me}/{Endpoints.Channels}/{channelId}/{Endpoints.Recipients}/{userId}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Delete
        };
        await this.rest.ExecuteRequestAsync(request);
    }

    public async ValueTask<DiscordDmChannel> CreateGroupDmAsync
    (
        IEnumerable<string> accessTokens,
        IDictionary<ulong, string> nicks
    )
    {
        RestUserGroupDmCreatePayload pld = new()
        {
            AccessTokens = accessTokens,
            Nicknames = nicks
        };

        string route = $"{Endpoints.Users}/{Endpoints.Me}/{Endpoints.Channels}";
        string url = $"{Endpoints.Users}/{Endpoints.Me}/{Endpoints.Channels}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Post,
            Payload = DiscordJson.SerializeObject(pld)
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        DiscordDmChannel ret = JsonConvert.DeserializeObject<DiscordDmChannel>(res.Response!)!;
        ret.Discord = this.discord!;

        return ret;
    }

    public async ValueTask<DiscordDmChannel> CreateDmAsync
    (
        ulong recipientId
    )
    {
        RestUserDmCreatePayload pld = new()
        {
            Recipient = recipientId
        };

        string route = $"{Endpoints.Users}{Endpoints.Me}{Endpoints.Channels}";
        string url = $"{Endpoints.Users}/{Endpoints.Me}/{Endpoints.Channels}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Post,
            Payload = DiscordJson.SerializeObject(pld)
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);
        DiscordDmChannel ret = JsonConvert.DeserializeObject<DiscordDmChannel>(res.Response!)!;
        ret.Discord = this.discord!;

        if (this.discord is DiscordClient dc)
        {
            _ = dc.privateChannels.TryAdd(ret.Id, ret);
        }

        return ret;
    }

    public async ValueTask<DiscordFollowedChannel> FollowChannelAsync
    (
        ulong channelId,
        ulong webhookChannelId
    )
    {
        FollowedChannelAddPayload pld = new()
        {
            WebhookChannelId = webhookChannelId
        };

        string route = $"{Endpoints.Channels}/{channelId}/{Endpoints.Followers}";
        string url = $"{Endpoints.Channels}/{channelId}/{Endpoints.Followers}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Post,
            Payload = DiscordJson.SerializeObject(pld)
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        return JsonConvert.DeserializeObject<DiscordFollowedChannel>(res.Response!)!;
    }

    public async ValueTask<DiscordMessage> CrosspostMessageAsync
    (
        ulong channelId,
        ulong messageId
    )
    {
        string route = $"{Endpoints.Channels}/{channelId}/{Endpoints.Messages}/:message_id/{Endpoints.Crosspost}";
        string url = $"{Endpoints.Channels}/{channelId}/{Endpoints.Messages}/{messageId}/{Endpoints.Crosspost}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Post
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        return JsonConvert.DeserializeObject<DiscordMessage>(res.Response!)!;
    }

    public async ValueTask<DiscordStageInstance> CreateStageInstanceAsync
    (
        ulong channelId,
        string topic,
        DiscordStagePrivacyLevel? privacyLevel = null,
        string? reason = null
    )
    {
        Dictionary<string, string> headers = [];

        if (!string.IsNullOrWhiteSpace(reason))
        {
            headers[ReasonHeaderName] = reason;
        }

        RestCreateStageInstancePayload pld = new()
        {
            ChannelId = channelId,
            Topic = topic,
            PrivacyLevel = privacyLevel
        };

        string route = $"{Endpoints.StageInstances}";
        string url = $"{Endpoints.StageInstances}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Post,
            Payload = DiscordJson.SerializeObject(pld),
            Headers = headers
        };

        RestResponse response = await this.rest.ExecuteRequestAsync(request);

        DiscordStageInstance stage = JsonConvert.DeserializeObject<DiscordStageInstance>(response.Response!)!;
        stage.Discord = this.discord!;

        return stage;
    }

    public async ValueTask<DiscordStageInstance> GetStageInstanceAsync
    (
        ulong channelId
    )
    {
        string route = $"{Endpoints.StageInstances}/{channelId}";
        string url = $"{Endpoints.StageInstances}/{channelId}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Get
        };

        RestResponse response = await this.rest.ExecuteRequestAsync(request);

        DiscordStageInstance stage = JsonConvert.DeserializeObject<DiscordStageInstance>(response.Response!)!;
        stage.Discord = this.discord!;

        return stage;
    }

    public async ValueTask<DiscordStageInstance> ModifyStageInstanceAsync
    (
        ulong channelId,
        Optional<string> topic = default,
        Optional<DiscordStagePrivacyLevel> privacyLevel = default,
        string? reason = null
    )
    {
        Dictionary<string, string> headers = [];

        if (!string.IsNullOrWhiteSpace(reason))
        {
            headers[ReasonHeaderName] = reason;
        }

        RestModifyStageInstancePayload pld = new()
        {
            Topic = topic,
            PrivacyLevel = privacyLevel
        };

        string route = $"{Endpoints.StageInstances}/{channelId}";
        string url = $"{Endpoints.StageInstances}/{channelId}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Patch,
            Payload = DiscordJson.SerializeObject(pld),
            Headers = headers
        };

        RestResponse response = await this.rest.ExecuteRequestAsync(request);
        DiscordStageInstance stage = JsonConvert.DeserializeObject<DiscordStageInstance>(response.Response!)!;
        stage.Discord = this.discord!;

        return stage;
    }

    public async ValueTask BecomeStageInstanceSpeakerAsync
    (
        ulong guildId,
        ulong id,
        ulong? userId = null,
        DateTime? timestamp = null,
        bool? suppress = null
    )
    {
        Dictionary<string, string> headers = [];

        RestBecomeStageSpeakerInstancePayload pld = new()
        {
            Suppress = suppress,
            ChannelId = id,
            RequestToSpeakTimestamp = timestamp
        };

        string user = userId?.ToString() ?? "@me";
        string route = $"/guilds/{guildId}/{Endpoints.VoiceStates}/{(userId is null ? "@me" : ":user_id")}";
        string url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.VoiceStates}/{user}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Patch,
            Payload = DiscordJson.SerializeObject(pld),
            Headers = headers
        };

        await this.rest.ExecuteRequestAsync(request);
    }

    public async ValueTask DeleteStageInstanceAsync
    (
        ulong channelId,
        string? reason = null
    )
    {
        Dictionary<string, string> headers = [];
        if (!string.IsNullOrWhiteSpace(reason))
        {
            headers[ReasonHeaderName] = reason;
        }

        string route = $"{Endpoints.StageInstances}/{channelId}";
        string url = $"{Endpoints.StageInstances}/{channelId}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Delete,
            Headers = headers
        };

        await this.rest.ExecuteRequestAsync(request);
    }

    #endregion

    #region Threads

    public async ValueTask<DiscordThreadChannel> CreateThreadFromMessageAsync
    (
        ulong channelId,
        ulong messageId,
        string name,
        DiscordAutoArchiveDuration archiveAfter,
        string? reason = null
    )
    {
        Dictionary<string, string> headers = [];
        if (!string.IsNullOrWhiteSpace(reason))
        {
            headers[ReasonHeaderName] = reason;
        }

        RestThreadCreatePayload payload = new()
        {
            Name = name,
            ArchiveAfter = archiveAfter
        };

        string route = $"{Endpoints.Channels}/{channelId}/{Endpoints.Messages}/:message_id/{Endpoints.Threads}";
        string url = $"{Endpoints.Channels}/{channelId}/{Endpoints.Messages}/{messageId}/{Endpoints.Threads}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Post,
            Payload = DiscordJson.SerializeObject(payload),
            Headers = headers
        };

        RestResponse response = await this.rest.ExecuteRequestAsync(request);

        DiscordThreadChannel thread = JsonConvert.DeserializeObject<DiscordThreadChannel>(response.Response!)!;
        thread.Discord = this.discord!;

        return thread;
    }

    public async ValueTask<DiscordThreadChannel> CreateThreadAsync
    (
        ulong channelId,
        string name,
        DiscordAutoArchiveDuration archiveAfter,
        DiscordChannelType type,
        string? reason = null
    )
    {
        Dictionary<string, string> headers = [];
        if (!string.IsNullOrWhiteSpace(reason))
        {
            headers[ReasonHeaderName] = reason;
        }

        RestThreadCreatePayload payload = new()
        {
            Name = name,
            ArchiveAfter = archiveAfter,
            Type = type
        };

        string route = $"{Endpoints.Channels}/{channelId}/{Endpoints.Threads}";
        string url = $"{Endpoints.Channels}/{channelId}/{Endpoints.Threads}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Post,
            Payload = DiscordJson.SerializeObject(payload),
            Headers = headers
        };

        RestResponse response = await this.rest.ExecuteRequestAsync(request);

        DiscordThreadChannel thread = JsonConvert.DeserializeObject<DiscordThreadChannel>(response.Response!)!;
        thread.Discord = this.discord!;

        return thread;
    }

    public async ValueTask JoinThreadAsync
    (
        ulong channelId
    )
    {
        string route = $"{Endpoints.Channels}/{channelId}/{Endpoints.ThreadMembers}/{Endpoints.Me}";
        string url = $"{Endpoints.Channels}/{channelId}/{Endpoints.ThreadMembers}/{Endpoints.Me}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Put
        };

        await this.rest.ExecuteRequestAsync(request);
    }

    public async ValueTask LeaveThreadAsync
    (
        ulong channelId
    )
    {
        string route = $"{Endpoints.Channels}/{channelId}/{Endpoints.ThreadMembers}/{Endpoints.Me}";
        string url = $"{Endpoints.Channels}/{channelId}/{Endpoints.ThreadMembers}/{Endpoints.Me}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Delete
        };

        await this.rest.ExecuteRequestAsync(request);
    }

    public async ValueTask<DiscordThreadChannelMember> GetThreadMemberAsync
    (
        ulong channelId,
        ulong userId
    )
    {
        string route = $"{Endpoints.Channels}/{channelId}/{Endpoints.ThreadMembers}/:user_id";
        string url = $"{Endpoints.Channels}/{channelId}/{Endpoints.ThreadMembers}/{userId}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Get
        };

        RestResponse response = await this.rest.ExecuteRequestAsync(request);

        DiscordThreadChannelMember ret = JsonConvert.DeserializeObject<DiscordThreadChannelMember>(response.Response!)!;

        ret.Discord = this.discord!;

        return ret;
    }

    public async ValueTask AddThreadMemberAsync
    (
        ulong channelId,
        ulong userId
    )
    {
        string route = $"{Endpoints.Channels}/{channelId}/{Endpoints.ThreadMembers}/:user_id";
        string url = $"{Endpoints.Channels}/{channelId}/{Endpoints.ThreadMembers}/{userId}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Put
        };

        await this.rest.ExecuteRequestAsync(request);
    }

    public async ValueTask RemoveThreadMemberAsync
    (
        ulong channelId,
        ulong userId
    )
    {
        string route = $"{Endpoints.Channels}/{channelId}/{Endpoints.ThreadMembers}/:user_id";
        string url = $"{Endpoints.Channels}/{channelId}/{Endpoints.ThreadMembers}/{userId}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Delete
        };

        await this.rest.ExecuteRequestAsync(request);
    }

    public async ValueTask<IReadOnlyList<DiscordThreadChannelMember>> ListThreadMembersAsync
    (
        ulong channelId
    )
    {
        string route = $"{Endpoints.Channels}/{channelId}/{Endpoints.ThreadMembers}";
        string url = $"{Endpoints.Channels}/{channelId}/{Endpoints.ThreadMembers}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Get
        };

        RestResponse response = await this.rest.ExecuteRequestAsync(request);

        List<DiscordThreadChannelMember> threadMembers = JsonConvert.DeserializeObject<List<DiscordThreadChannelMember>>(response.Response!)!;

        foreach (DiscordThreadChannelMember member in threadMembers)
        {
            member.Discord = this.discord!;
        }

        return threadMembers;
    }

    public async ValueTask<ThreadQueryResult> ListActiveThreadsAsync
    (
        ulong guildId
    )
    {
        string route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Threads}/{Endpoints.Active}";
        string url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Threads}/{Endpoints.Active}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Get
        };

        RestResponse response = await this.rest.ExecuteRequestAsync(request);

        ThreadQueryResult result = JsonConvert.DeserializeObject<ThreadQueryResult>(response.Response!)!;
        result.HasMore = false;

        foreach (DiscordThreadChannel thread in result.Threads)
        {
            thread.Discord = this.discord!;
        }

        foreach (DiscordThreadChannelMember member in result.Members)
        {
            member.Discord = this.discord!;
            member.guildId = guildId;
            DiscordThreadChannel? thread = result.Threads.SingleOrDefault(x => x.Id == member.ThreadId);
            if (thread is not null)
            {
                thread.CurrentMember = member;
            }
        }

        return result;
    }

    public async ValueTask<ThreadQueryResult> ListPublicArchivedThreadsAsync
    (
        ulong guildId,
        ulong channelId,
        string before,
        int limit
    )
    {
        QueryUriBuilder queryParams = new($"{Endpoints.Channels}/{channelId}/{Endpoints.Threads}/{Endpoints.Archived}/{Endpoints.Public}");
        if (before != null)
        {
            queryParams.AddParameter("before", before?.ToString(CultureInfo.InvariantCulture));
        }

        if (limit > 0)
        {
            queryParams.AddParameter("limit", limit.ToString(CultureInfo.InvariantCulture));
        }

        string route = $"{Endpoints.Channels}/{channelId}/{Endpoints.Threads}/{Endpoints.Archived}/{Endpoints.Public}";

        RestRequest request = new()
        {
            Route = route,
            Url = queryParams.Build(),
            Method = HttpMethod.Get,

        };

        RestResponse response = await this.rest.ExecuteRequestAsync(request);

        ThreadQueryResult result = JsonConvert.DeserializeObject<ThreadQueryResult>(response.Response!)!;

        foreach (DiscordThreadChannel thread in result.Threads)
        {
            thread.Discord = this.discord!;
        }

        foreach (DiscordThreadChannelMember member in result.Members)
        {
            member.Discord = this.discord!;
            member.guildId = guildId;
            DiscordThreadChannel? thread = result.Threads.SingleOrDefault(x => x.Id == member.ThreadId);
            if (thread is not null)
            {
                thread.CurrentMember = member;
            }
        }

        return result;
    }

    public async ValueTask<ThreadQueryResult> ListPrivateArchivedThreadsAsync
    (
        ulong guildId,
        ulong channelId,
        int limit,
        string? before = null
    )
    {
        QueryUriBuilder queryParams = new($"{Endpoints.Channels}/{channelId}/{Endpoints.Threads}/{Endpoints.Archived}/{Endpoints.Private}");
        if (before is not null)
        {
            queryParams.AddParameter("before", before?.ToString(CultureInfo.InvariantCulture));
        }

        if (limit > 0)
        {
            queryParams.AddParameter("limit", limit.ToString(CultureInfo.InvariantCulture));
        }

        string route = $"{Endpoints.Channels}/{channelId}/{Endpoints.Threads}/{Endpoints.Archived}/{Endpoints.Private}";

        RestRequest request = new()
        {
            Route = route,
            Url = queryParams.Build(),
            Method = HttpMethod.Get
        };

        RestResponse response = await this.rest.ExecuteRequestAsync(request);

        ThreadQueryResult result = JsonConvert.DeserializeObject<ThreadQueryResult>(response.Response!)!;

        foreach (DiscordThreadChannel thread in result.Threads)
        {
            thread.Discord = this.discord!;
        }

        foreach (DiscordThreadChannelMember member in result.Members)
        {
            member.Discord = this.discord!;
            member.guildId = guildId;
            DiscordThreadChannel? thread = result.Threads.SingleOrDefault(x => x.Id == member.ThreadId);
            if (thread is not null)
            {
                thread.CurrentMember = member;
            }
        }

        return result;
    }

    public async ValueTask<ThreadQueryResult> ListJoinedPrivateArchivedThreadsAsync
    (
        ulong guildId,
        ulong channelId,
        int limit,
        ulong? before = null
    )
    {
        QueryUriBuilder queryParams = new($"{Endpoints.Channels}/{channelId}/{Endpoints.Threads}/{Endpoints.Archived}/{Endpoints.Private}/{Endpoints.Me}");
        if (before is not null)
        {
            queryParams.AddParameter("before", before?.ToString(CultureInfo.InvariantCulture));
        }

        if (limit > 0)
        {
            queryParams.AddParameter("limit", limit.ToString(CultureInfo.InvariantCulture));
        }

        string route = $"{Endpoints.Channels}/{channelId}/{Endpoints.Users}/{Endpoints.Me}/{Endpoints.Threads}/{Endpoints.Archived}/{Endpoints.Public}";

        RestRequest request = new()
        {
            Route = route,
            Url = queryParams.Build(),
            Method = HttpMethod.Get
        };

        RestResponse response = await this.rest.ExecuteRequestAsync(request);

        ThreadQueryResult result = JsonConvert.DeserializeObject<ThreadQueryResult>(response.Response!)!;

        foreach (DiscordThreadChannel thread in result.Threads)
        {
            thread.Discord = this.discord!;
        }

        foreach (DiscordThreadChannelMember member in result.Members)
        {
            member.Discord = this.discord!;
            member.guildId = guildId;
            DiscordThreadChannel? thread = result.Threads.SingleOrDefault(x => x.Id == member.ThreadId);
            if (thread is not null)
            {
                thread.CurrentMember = member;
            }
        }

        return result;
    }

    #endregion

    #region Member
    internal ValueTask<DiscordUser> GetCurrentUserAsync()
        => GetUserAsync("@me");

    internal ValueTask<DiscordUser> GetUserAsync(ulong userId)
        => GetUserAsync(userId.ToString(CultureInfo.InvariantCulture));

    public async ValueTask<DiscordUser> GetUserAsync(string userId)
    {
        string route = $"{Endpoints.Users}/:user_id";
        string url = $"{Endpoints.Users}/{userId}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        TransportUser userRaw = JsonConvert.DeserializeObject<TransportUser>(res.Response!)!;
        DiscordUser user = new(userRaw)
        {
            Discord = this.discord!
        };

        return user;
    }

    public async ValueTask<DiscordMember> GetGuildMemberAsync
    (
        ulong guildId,
        ulong userId
    )
    {
        string route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Members}/:user_id";
        string url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Members}/{userId}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        TransportMember tm = JsonConvert.DeserializeObject<TransportMember>(res.Response!)!;

        DiscordUser usr = new(tm.User)
        {
            Discord = this.discord!
        };
        _ = this.discord!.UpdateUserCache(usr);

        return new DiscordMember(tm)
        {
            Discord = this.discord,
            guildId = guildId
        };
    }

    public async ValueTask RemoveGuildMemberAsync
    (
        ulong guildId,
        ulong userId,
        string? reason = null
    )
    {
        string url = new($"{Endpoints.Guilds}/{guildId}/{Endpoints.Members}/{userId}");
        string route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Members}/:user_id";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Delete,
            Headers = string.IsNullOrWhiteSpace(reason)
                ? null
                : new Dictionary<string, string>
                {
                    [ReasonHeaderName] = reason
                }
        };

        await this.rest.ExecuteRequestAsync(request);
    }

    public async ValueTask<DiscordUser> ModifyCurrentUserAsync
    (
        string username,
        Optional<string> base64Avatar = default,
        Optional<string> base64Banner = default
    )
    {
        RestUserUpdateCurrentPayload pld = new()
        {
            Username = username,
            AvatarBase64 = base64Avatar.HasValue ? base64Avatar.Value : null,
            AvatarSet = base64Avatar.HasValue,
            BannerBase64 = base64Banner.HasValue ? base64Banner.Value : null,
            BannerSet = base64Banner.HasValue
        };

        string route = $"{Endpoints.Users}/{Endpoints.Me}";
        string url = $"{Endpoints.Users}/{Endpoints.Me}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Patch,
            Payload = DiscordJson.SerializeObject(pld)
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        TransportUser userRaw = JsonConvert.DeserializeObject<TransportUser>(res.Response!)!;

        return new DiscordUser(userRaw)
        {
            Discord = this.discord
        };
    }

    public async ValueTask<IReadOnlyList<DiscordGuild>> GetCurrentUserGuildsAsync
    (
        int limit = 100,
        ulong? before = null,
        ulong? after = null
    )
    {
        string route = $"{Endpoints.Users}/{Endpoints.Me}/{Endpoints.Guilds}";
        QueryUriBuilder url = new($"{Endpoints.Users}/{Endpoints.Me}/{Endpoints.Guilds}");
        url.AddParameter($"limit", limit.ToString(CultureInfo.InvariantCulture));

        if (before != null)
        {
            url.AddParameter("before", before.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (after != null)
        {
            url.AddParameter("after", after.Value.ToString(CultureInfo.InvariantCulture));
        }

        RestRequest request = new()
        {
            Route = route,
            Url = url.Build(),
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        if (this.discord is DiscordClient)
        {
            IEnumerable<RestUserGuild> guildsRaw = JsonConvert.DeserializeObject<IEnumerable<RestUserGuild>>(res.Response!)!;
            IEnumerable<DiscordGuild> guilds = guildsRaw.Select
            (
                xug => (this.discord as DiscordClient)?.guilds[xug.Id]
            )
            .Where(static guild => guild is not null)!;
            return guilds.ToList();
        }
        else
        {
            List<DiscordGuild> guilds = [.. JsonConvert.DeserializeObject<List<DiscordGuild>>(res.Response!)!];
            foreach (DiscordGuild guild in guilds)
            {
                guild.Discord = this.discord!;

            }
            return guilds;
        }
    }

    public async ValueTask<DiscordMember> GetCurrentUserGuildMemberAsync
    (
        ulong guildId
    )
    {
        string route = $"{Endpoints.Users}/{Endpoints.Me}/{Endpoints.Guilds}/{guildId}/member";

        RestRequest request = new()
        {
            Route = route,
            Url = route,
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        TransportMember tm = JsonConvert.DeserializeObject<TransportMember>(res.Response!)!;

        DiscordUser usr = new(tm.User)
        {
            Discord = this.discord!
        };
        _ = this.discord!.UpdateUserCache(usr);

        return new DiscordMember(tm)
        {
            Discord = this.discord,
            guildId = guildId
        };
    }

    public async ValueTask ModifyGuildMemberAsync
    (
        ulong guildId,
        ulong userId,
        Optional<string> nick = default,
        Optional<IEnumerable<ulong>> roleIds = default,
        Optional<bool> mute = default,
        Optional<bool> deaf = default,
        Optional<ulong?> voiceChannelId = default,
        Optional<DateTimeOffset?> communicationDisabledUntil = default,
        Optional<DiscordMemberFlags> memberFlags = default,
        string? reason = null
    )
    {
        Dictionary<string, string> headers = [];
        if (!string.IsNullOrWhiteSpace(reason))
        {
            headers[ReasonHeaderName] = reason;
        }

        RestGuildMemberModifyPayload pld = new()
        {
            Nickname = nick,
            RoleIds = roleIds,
            Deafen = deaf,
            Mute = mute,
            VoiceChannelId = voiceChannelId,
            CommunicationDisabledUntil = communicationDisabledUntil,
            MemberFlags = memberFlags
        };

        string route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Members}/:user_id";
        string url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Members}/{userId}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Patch,
            Payload = DiscordJson.SerializeObject(pld),
            Headers = headers
        };

        await this.rest.ExecuteRequestAsync(request);
    }

    public async ValueTask ModifyCurrentMemberAsync
    (
        ulong guildId,
        string nick,
        string? reason = null
    )
    {
        Dictionary<string, string> headers = [];
        if (!string.IsNullOrWhiteSpace(reason))
        {
            headers[ReasonHeaderName] = reason;
        }

        RestGuildMemberModifyPayload pld = new()
        {
            Nickname = nick
        };

        string route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Members}/{Endpoints.Me}";
        string url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Members}/{Endpoints.Me}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Patch,
            Payload = DiscordJson.SerializeObject(pld),
            Headers = headers
        };

        await this.rest.ExecuteRequestAsync(request);
    }
    #endregion

    #region Roles
    public async ValueTask<DiscordRole> GetGuildRoleAsync
    (
        ulong guildId,
        ulong roleId
    )
    {
        string route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Roles}/:role_id";
        string url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Roles}/{roleId}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        DiscordRole role = JsonConvert.DeserializeObject<DiscordRole>(res.Response!)!;
        role.Discord = this.discord!;
        role.guildId = guildId;

        return role;
    }

    public async ValueTask<IReadOnlyList<DiscordRole>> GetGuildRolesAsync
    (
        ulong guildId
    )
    {
        string route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Roles}";
        string url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Roles}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        List<DiscordRole> roles = JsonConvert.DeserializeObject<IEnumerable<DiscordRole>>(res.Response!)!
            .Select
            (
                xr =>
                {
                    xr.Discord = this.discord!;
                    xr.guildId = guildId;
                    return xr;
                }
            )
            .ToList();

        return roles;
    }

    public async ValueTask<DiscordGuild> GetGuildAsync
    (
        ulong guildId,
        bool? withCounts
    )
    {
        QueryUriBuilder urlparams = new($"{Endpoints.Guilds}/{guildId}");
        if (withCounts.HasValue)
        {
            urlparams.AddParameter("with_counts", withCounts?.ToString());
        }

        string route = $"{Endpoints.Guilds}/{guildId}";

        RestRequest request = new()
        {
            Route = route,
            Url = urlparams.Build(),
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        JObject json = JObject.Parse(res.Response!);
        JArray rawMembers = (JArray)json["members"]!;
        DiscordGuild guildRest = json.ToDiscordObject<DiscordGuild>();
        foreach (DiscordRole role in guildRest.roles.Values)
        {
            role.guildId = guildRest.Id;
        }

        if (this.discord is DiscordClient discordClient)
        {
            await discordClient.OnGuildUpdateEventAsync(guildRest, rawMembers);
            return discordClient.guilds[guildRest.Id];
        }
        else
        {
            guildRest.Discord = this.discord!;
            return guildRest;
        }
    }

    public async ValueTask<DiscordRole> ModifyGuildRoleAsync
    (
        ulong guildId,
        ulong roleId,
        string? name = null,
        DiscordPermissions? permissions = null,
        int? color = null,
        bool? hoist = null,
        bool? mentionable = null,
        Stream? icon = null,
        string? emoji = null,
        string? reason = null
    )
    {
        string? image = null;

        if (icon != null)
        {
            using InlineMediaTool it = new(icon);
            image = it.GetBase64();
        }

        RestGuildRolePayload pld = new()
        {
            Name = name,
            Permissions = permissions & DiscordPermissions.All,
            Color = color,
            Hoist = hoist,
            Mentionable = mentionable,
            Emoji = emoji,
            Icon = image
        };

        Dictionary<string, string> headers = [];
        if (!string.IsNullOrWhiteSpace(reason))
        {
            headers[ReasonHeaderName] = reason;
        }

        string route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Roles}/:role_id";
        string url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Roles}/{roleId}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Patch,
            Payload = DiscordJson.SerializeObject(pld),
            Headers = headers
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        DiscordRole ret = JsonConvert.DeserializeObject<DiscordRole>(res.Response!)!;
        ret.Discord = this.discord!;
        ret.guildId = guildId;

        return ret;
    }

    public async ValueTask DeleteRoleAsync
    (
        ulong guildId,
        ulong roleId,
        string? reason = null
    )
    {
        Dictionary<string, string> headers = [];
        if (!string.IsNullOrWhiteSpace(reason))
        {
            headers[ReasonHeaderName] = reason;
        }

        string route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Roles}/:role_id";
        string url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Roles}/{roleId}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Delete,
            Headers = headers
        };

        await this.rest.ExecuteRequestAsync(request);
    }

    public async ValueTask<DiscordRole> CreateGuildRoleAsync
    (
        ulong guildId,
        string name,
        DiscordPermissions? permissions = null,
        int? color = null,
        bool? hoist = null,
        bool? mentionable = null,
        Stream? icon = null,
        string? emoji = null,
        string? reason = null
    )
    {
        string? image = null;

        if (icon != null)
        {
            using InlineMediaTool it = new(icon);
            image = it.GetBase64();
        }

        RestGuildRolePayload pld = new()
        {
            Name = name,
            Permissions = permissions & DiscordPermissions.All,
            Color = color,
            Hoist = hoist,
            Mentionable = mentionable,
            Emoji = emoji,
            Icon = image
        };

        Dictionary<string, string> headers = [];
        if (!string.IsNullOrWhiteSpace(reason))
        {
            headers[ReasonHeaderName] = reason;
        }

        string route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Roles}";
        string url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Roles}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Post,
            Payload = DiscordJson.SerializeObject(pld),
            Headers = headers
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        DiscordRole ret = JsonConvert.DeserializeObject<DiscordRole>(res.Response!)!;
        ret.Discord = this.discord!;
        ret.guildId = guildId;

        return ret;
    }
    #endregion

    #region Prune
    public async ValueTask<int> GetGuildPruneCountAsync
    (
        ulong guildId,
        int days,
        IEnumerable<ulong>? includeRoles = null
    )
    {
        if (days is < 0 or > 30)
        {
            throw new ArgumentException("Prune inactivity days must be a number between 0 and 30.", nameof(days));
        }

        QueryUriBuilder urlparams = new($"{Endpoints.Guilds}/{guildId}/{Endpoints.Prune}");
        urlparams.AddParameter("days", days.ToString(CultureInfo.InvariantCulture));

        StringBuilder sb = new();

        if (includeRoles is not null)
        {
            ulong[] roleArray = includeRoles.ToArray();
            int roleArrayCount = roleArray.Length;

            for (int i = 0; i < roleArrayCount; i++)
            {
                sb.Append($"&include_roles={roleArray[i]}");
            }
        }

        string route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Prune}";

        RestRequest request = new()
        {
            Route = route,
            Url = urlparams.Build(),
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        RestGuildPruneResultPayload pruned = JsonConvert.DeserializeObject<RestGuildPruneResultPayload>(res.Response!)!;

        return pruned.Pruned!.Value;
    }

    public async ValueTask<int?> BeginGuildPruneAsync
    (
        ulong guildId,
        int days,
        bool computePruneCount,
        IEnumerable<ulong>? includeRoles = null,
        string? reason = null
    )
    {
        if (days is < 0 or > 30)
        {
            throw new ArgumentException("Prune inactivity days must be a number between 0 and 30.", nameof(days));
        }

        QueryUriBuilder urlparams = new($"{Endpoints.Guilds}/{guildId}/{Endpoints.Prune}");
        urlparams.AddParameter("days", days.ToString(CultureInfo.InvariantCulture));
        urlparams.AddParameter("compute_prune_count", computePruneCount.ToString());

        StringBuilder sb = new();

        if (includeRoles is not null)
        {
            foreach (ulong id in includeRoles)
            {
                sb.Append($"&include_roles={id}");
            }
        }

        Dictionary<string, string> headers = [];
        if (string.IsNullOrWhiteSpace(reason))
        {
            headers[ReasonHeaderName] = reason!;
        }

        string route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Prune}";

        RestRequest request = new()
        {
            Route = route,
            Url = urlparams.Build() + sb.ToString(),
            Method = HttpMethod.Post,
            Headers = headers
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        RestGuildPruneResultPayload pruned = JsonConvert.DeserializeObject<RestGuildPruneResultPayload>(res.Response!)!;

        return pruned.Pruned;
    }
    #endregion

    #region GuildVarious
    public async ValueTask<DiscordGuildTemplate> GetTemplateAsync
    (
        string code
    )
    {
        string route = $"{Endpoints.Guilds}/{Endpoints.Templates}/:code";
        string url = $"{Endpoints.Guilds}/{Endpoints.Templates}/{code}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        DiscordGuildTemplate templatesRaw = JsonConvert.DeserializeObject<DiscordGuildTemplate>(res.Response!)!;

        return templatesRaw;
    }

    public async ValueTask<IReadOnlyList<DiscordIntegration>> GetGuildIntegrationsAsync
    (
        ulong guildId
    )
    {
        string route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Integrations}";
        string url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Integrations}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        List<DiscordIntegration> integrations =
            JsonConvert.DeserializeObject<IEnumerable<DiscordIntegration>>(res.Response!)!
            .Select
            (
                xi =>
                {
                    xi.Discord = this.discord!;
                    return xi;
                }
            )
            .ToList();

        return integrations;
    }

    public async ValueTask<DiscordGuildPreview> GetGuildPreviewAsync
    (
        ulong guildId
    )
    {
        string route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Preview}";
        string url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Preview}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        DiscordGuildPreview ret = JsonConvert.DeserializeObject<DiscordGuildPreview>(res.Response!)!;
        ret.Discord = this.discord!;

        return ret;
    }

    public async ValueTask<DiscordIntegration> CreateGuildIntegrationAsync
    (
        ulong guildId,
        string type,
        ulong id
    )
    {
        RestGuildIntegrationAttachPayload pld = new()
        {
            Type = type,
            Id = id
        };

        string route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Integrations}";
        string url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Integrations}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Post,
            Payload = DiscordJson.SerializeObject(pld)
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        DiscordIntegration ret = JsonConvert.DeserializeObject<DiscordIntegration>(res.Response!)!;
        ret.Discord = this.discord!;

        return ret;
    }

    public async ValueTask<DiscordIntegration> ModifyGuildIntegrationAsync
    (
        ulong guildId,
        ulong integrationId,
        int expireBehaviour,
        int expireGracePeriod,
        bool enableEmoticons
    )
    {
        RestGuildIntegrationModifyPayload pld = new()
        {
            ExpireBehavior = expireBehaviour,
            ExpireGracePeriod = expireGracePeriod,
            EnableEmoticons = enableEmoticons
        };

        string route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Integrations}/:integration_id";
        string url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Integrations}/{integrationId}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Patch,
            Payload = DiscordJson.SerializeObject(pld)
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);
        DiscordIntegration ret = JsonConvert.DeserializeObject<DiscordIntegration>(res.Response!)!;
        ret.Discord = this.discord!;

        return ret;
    }

    public async ValueTask DeleteGuildIntegrationAsync
    (
        ulong guildId,
        ulong integrationId,
        string? reason = null
    )
    {
        Dictionary<string, string> headers = [];
        if (string.IsNullOrWhiteSpace(reason))
        {
            headers[ReasonHeaderName] = reason!;
        }

        string route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Integrations}/:integration_id";
        string url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Integrations}/{integrationId}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Delete,
            Headers = headers
        };

        await this.rest.ExecuteRequestAsync(request);
    }

    public async ValueTask SyncGuildIntegrationAsync
    (
        ulong guildId,
        ulong integrationId
    )
    {
        string route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Integrations}/:integration_id/{Endpoints.Sync}";
        string url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Integrations}/{integrationId}/{Endpoints.Sync}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Post
        };

        await this.rest.ExecuteRequestAsync(request);
    }

    public async ValueTask<IReadOnlyList<DiscordVoiceRegion>> GetGuildVoiceRegionsAsync
    (
        ulong guildId
    )
    {
        string route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Regions}";
        string url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Regions}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        List<DiscordVoiceRegion> regions 
            = JsonConvert.DeserializeObject<IEnumerable<DiscordVoiceRegion>>(res.Response!)!.ToList();

        return regions;
    }

    public async ValueTask<IReadOnlyList<DiscordInvite>> GetGuildInvitesAsync
    (
        ulong guildId
    )
    {
        string route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Invites}";
        string url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Invites}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        List<DiscordInvite> invites =
            JsonConvert.DeserializeObject<IEnumerable<DiscordInvite>>(res.Response!)!
            .Select
            (
                xi =>
                {
                    xi.Discord = this.discord!;
                    return xi;
                }
            )
            .ToList();

        return invites;
    }
    #endregion

    #region Invite
    public async ValueTask<DiscordInvite> GetInviteAsync
    (
        string inviteCode,
        bool? withCounts = null
    )
    {
        QueryUriBuilder uriBuilder = new($"{Endpoints.Invites}/{inviteCode}");

        if (withCounts is true)
        {
            uriBuilder.AddParameter("with_counts", "true");
        }

        const string route = $"{Endpoints.Invites}/:invite_code";
        string url = uriBuilder.Build();

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        DiscordInvite ret = JsonConvert.DeserializeObject<DiscordInvite>(res.Response!)!;
        ret.Discord = this.discord!;

        return ret;
    }

    public async ValueTask<DiscordInvite> DeleteInviteAsync
    (
        string inviteCode,
        string? reason = null
    )
    {
        Dictionary<string, string> headers = [];
        if (!string.IsNullOrWhiteSpace(reason))
        {
            headers[ReasonHeaderName] = reason;
        }

        string route = $"{Endpoints.Invites}/:invite_code";
        string url = $"{Endpoints.Invites}/{inviteCode}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Delete,
            Headers = headers
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        DiscordInvite ret = JsonConvert.DeserializeObject<DiscordInvite>(res.Response!)!;
        ret.Discord = this.discord!;

        return ret;
    }
    #endregion

    #region Connections
    public async ValueTask<IReadOnlyList<DiscordConnection>> GetUsersConnectionsAsync()
    {
        string route = $"{Endpoints.Users}/{Endpoints.Me}/{Endpoints.Connections}";
        string url = $"{Endpoints.Users}/{Endpoints.Me}/{Endpoints.Connections}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        List<DiscordConnection> connections =
            JsonConvert.DeserializeObject<IEnumerable<DiscordConnection>>(res.Response!)!
            .Select
            (
                xc =>
                {
                    xc.Discord = this.discord!;
                    return xc;
                }
            )
            .ToList();

        return connections;
    }
    #endregion

    #region Voice
    public async ValueTask<IReadOnlyList<DiscordVoiceRegion>> ListVoiceRegionsAsync()
    {
        string route = $"{Endpoints.Voice}/{Endpoints.Regions}";
        string url = $"{Endpoints.Voice}/{Endpoints.Regions}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        List<DiscordVoiceRegion> regions =
            JsonConvert.DeserializeObject<IEnumerable<DiscordVoiceRegion>>(res.Response!)!
                .ToList();

        return regions;
    }
    #endregion

    #region Webhooks
    public async ValueTask<DiscordWebhook> CreateWebhookAsync
    (
        ulong channelId,
        string name,
        Optional<string> base64Avatar = default,
        string? reason = null
    )
    {
        RestWebhookPayload pld = new()
        {
            Name = name,
            AvatarBase64 = base64Avatar.HasValue ? base64Avatar.Value : null,
            AvatarSet = base64Avatar.HasValue
        };

        Dictionary<string, string> headers = [];
        if (!string.IsNullOrWhiteSpace(reason))
        {
            headers[ReasonHeaderName] = reason;
        }

        string route = $"{Endpoints.Channels}/{channelId}/{Endpoints.Webhooks}";
        string url = $"{Endpoints.Channels}/{channelId}/{Endpoints.Webhooks}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Post,
            Payload = DiscordJson.SerializeObject(pld),
            Headers = headers
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        DiscordWebhook ret = JsonConvert.DeserializeObject<DiscordWebhook>(res.Response!)!;
        ret.Discord = this.discord!;
        ret.ApiClient = this;

        return ret;
    }

    public async ValueTask<IReadOnlyList<DiscordWebhook>> GetChannelWebhooksAsync
    (
        ulong channelId
    )
    {
        string route = $"{Endpoints.Channels}/{channelId}/{Endpoints.Webhooks}";
        string url = $"{Endpoints.Channels}/{channelId}/{Endpoints.Webhooks}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        List<DiscordWebhook> webhooks =
            JsonConvert
                .DeserializeObject<IEnumerable<DiscordWebhook>>(res.Response!)!
                .Select
                (
                    xw =>
                    {
                        xw.Discord = this.discord!;
                        xw.ApiClient = this;
                        return xw;
                    }
                )
                .ToList();

        return webhooks;
    }

    public async ValueTask<IReadOnlyList<DiscordWebhook>> GetGuildWebhooksAsync
    (
        ulong guildId
    )
    {
        string route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Webhooks}";
        string url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Webhooks}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        List<DiscordWebhook> webhooks =
            JsonConvert
                .DeserializeObject<IEnumerable<DiscordWebhook>>(res.Response!)!
                .Select
                (
                    xw =>
                    {
                        xw.Discord = this.discord!;
                        xw.ApiClient = this;
                        return xw;
                    }
                )
                .ToList();

        return webhooks;
    }

    public async ValueTask<DiscordWebhook> GetWebhookAsync
    (
        ulong webhookId
    )
    {
        string route = $"{Endpoints.Webhooks}/{webhookId}";
        string url = $"{Endpoints.Webhooks}/{webhookId}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        DiscordWebhook ret = JsonConvert.DeserializeObject<DiscordWebhook>(res.Response!)!;
        ret.Discord = this.discord!;
        ret.ApiClient = this;

        return ret;
    }

    // Auth header not required
    public async ValueTask<DiscordWebhook> GetWebhookWithTokenAsync
    (
        ulong webhookId,
        string webhookToken
    )
    {
        string route = $"{Endpoints.Webhooks}/{webhookId}/:webhook_token";
        string url = $"{Endpoints.Webhooks}/{webhookId}/{webhookToken}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            IsExemptFromGlobalLimit = true,
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        DiscordWebhook ret = JsonConvert.DeserializeObject<DiscordWebhook>(res.Response!)!;
        ret.Token = webhookToken;
        ret.Id = webhookId;
        ret.Discord = this.discord!;
        ret.ApiClient = this;

        return ret;
    }

    public async ValueTask<DiscordMessage> GetWebhookMessageAsync
    (
        ulong webhookId,
        string webhookToken,
        ulong messageId
    )
    {
        string route = $"{Endpoints.Webhooks}/{webhookId}/:webhook_token/{Endpoints.Messages}/:message_id";
        string url = $"{Endpoints.Webhooks}/{webhookId}/{webhookToken}/{Endpoints.Messages}/{messageId}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            IsExemptFromGlobalLimit = true,
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        DiscordMessage ret = JsonConvert.DeserializeObject<DiscordMessage>(res.Response!)!;
        ret.Discord = this.discord!;
        return ret;
    }

    public async ValueTask<DiscordWebhook> ModifyWebhookAsync
    (
        ulong webhookId,
        ulong channelId,
        string? name = null,
        Optional<string> base64Avatar = default,
        string? reason = null
    )
    {
        RestWebhookPayload pld = new()
        {
            Name = name,
            AvatarBase64 = base64Avatar.HasValue ? base64Avatar.Value : null,
            AvatarSet = base64Avatar.HasValue,
            ChannelId = channelId
        };

        Dictionary<string, string> headers = [];
        if (!string.IsNullOrWhiteSpace(reason))
        {
            headers[ReasonHeaderName] = reason;
        }

        string route = $"{Endpoints.Webhooks}/{webhookId}";
        string url = $"{Endpoints.Webhooks}/{webhookId}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Patch,
            Payload = DiscordJson.SerializeObject(pld),
            Headers = headers
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        DiscordWebhook ret = JsonConvert.DeserializeObject<DiscordWebhook>(res.Response!)!;
        ret.Discord = this.discord!;
        ret.ApiClient = this;

        return ret;
    }

    public async ValueTask<DiscordWebhook> ModifyWebhookAsync
    (
        ulong webhookId,
        string webhookToken,
        string? name = null,
        string? base64Avatar = null,
        string? reason = null
    )
    {
        RestWebhookPayload pld = new()
        {
            Name = name,
            AvatarBase64 = base64Avatar
        };

        Dictionary<string, string> headers = [];
        if (!string.IsNullOrWhiteSpace(reason))
        {
            headers[ReasonHeaderName] = reason;
        }

        string route = $"{Endpoints.Webhooks}/{webhookId}/:webhook_token";
        string url = $"{Endpoints.Webhooks}/{webhookId}/{webhookToken}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Patch,
            Payload = DiscordJson.SerializeObject(pld),
            IsExemptFromGlobalLimit = true,
            Headers = headers
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        DiscordWebhook ret = JsonConvert.DeserializeObject<DiscordWebhook>(res.Response!)!;
        ret.Discord = this.discord!;
        ret.ApiClient = this;

        return ret;
    }

    public async ValueTask DeleteWebhookAsync
    (
        ulong webhookId,
        string? reason = null
    )
    {
        Dictionary<string, string> headers = [];
        if (!string.IsNullOrWhiteSpace(reason))
        {
            headers[ReasonHeaderName] = reason;
        }

        string route = $"{Endpoints.Webhooks}/{webhookId}";
        string url = $"{Endpoints.Webhooks}/{webhookId}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Delete,
            Headers = headers
        };

        await this.rest.ExecuteRequestAsync(request);
    }

    public async ValueTask DeleteWebhookAsync
    (
        ulong webhookId,
        string webhookToken,
        string? reason = null
    )
    {
        Dictionary<string, string> headers = [];
        if (!string.IsNullOrWhiteSpace(reason))
        {
            headers[ReasonHeaderName] = reason;
        }

        string route = $"{Endpoints.Webhooks}/{webhookId}/:webhook_token";
        string url = $"{Endpoints.Webhooks}/{webhookId}/{webhookToken}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Delete,
            IsExemptFromGlobalLimit = true,
            Headers = headers
        };

        await this.rest.ExecuteRequestAsync(request);
    }

    public async ValueTask<DiscordMessage> ExecuteWebhookAsync
    (
        ulong webhookId,
        string webhookToken,
        DiscordWebhookBuilder builder
    )
    {
        builder.Validate();

        if (builder.Embeds != null)
        {
            foreach (DiscordEmbed embed in builder.Embeds)
            {
                if (embed.Timestamp != null)
                {
                    embed.Timestamp = embed.Timestamp.Value.ToUniversalTime();
                }
            }
        }

        Dictionary<string, string> values = [];
        RestWebhookExecutePayload pld = new()
        {
            Content = builder.Content,
            Username = builder.Username.HasValue ? builder.Username.Value : null,
            AvatarUrl = builder.AvatarUrl.HasValue ? builder.AvatarUrl.Value : null,
            IsTts = builder.IsTts,
            Embeds = builder.Embeds,
            Flags = builder.Flags,
            Components = builder.Components,
            Poll = builder.Poll?.BuildInternal(),
        };

        if (builder.Mentions != null)
        {
            pld.Mentions = new DiscordMentions(builder.Mentions, builder.Mentions.Any());
        }

        if (!string.IsNullOrEmpty(builder.Content) || builder.Embeds?.Count > 0 || builder.IsTts == true || builder.Mentions != null)
        {
            values["payload_json"] = DiscordJson.SerializeObject(pld);
        }

        string route = $"{Endpoints.Webhooks}/{webhookId}/:webhook_token";
        QueryUriBuilder url = new($"{Endpoints.Webhooks}/{webhookId}/{webhookToken}");
        url.AddParameter("wait", "true");
        url.AddParameter("with_components", "true");

        if (builder.ThreadId.HasValue)
        {
            url.AddParameter("thread_id", builder.ThreadId.Value.ToString());
        }

        MultipartRestRequest request = new()
        {
            Route = route,
            Url = url.Build(),
            Method = HttpMethod.Post,
            Values = values,
            Files = builder.Files,
            IsExemptFromGlobalLimit = true
        };

        RestResponse res;
        try
        {
            res = await this.rest.ExecuteRequestAsync(request);
        }
        finally
        {
            builder.ResetFileStreamPositions();
        }
        DiscordMessage ret = JsonConvert.DeserializeObject<DiscordMessage>(res.Response!)!;

        ret.Discord = this.discord!;
        return ret;
    }

    public async ValueTask<DiscordMessage> ExecuteWebhookSlackAsync
    (
        ulong webhookId,
        string webhookToken,
        string jsonPayload
    )
    {
        string route = $"{Endpoints.Webhooks}/{webhookId}/:webhook_token/{Endpoints.Slack}";
        QueryUriBuilder url = new($"{Endpoints.Webhooks}/{webhookId}/{webhookToken}/{Endpoints.Slack}");

        RestRequest request = new()
        {
            Route = route,
            Url = url.Build(),
            Method = HttpMethod.Post,
            Payload = jsonPayload,
            IsExemptFromGlobalLimit = true
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        DiscordMessage ret = JsonConvert.DeserializeObject<DiscordMessage>(res.Response!)!;
        ret.Discord = this.discord!;
        return ret;
    }

    public async ValueTask<DiscordMessage> ExecuteWebhookGithubAsync
    (
        ulong webhookId,
        string webhookToken,
        string jsonPayload
    )
    {
        string route = $"{Endpoints.Webhooks}/{webhookId}/:webhook_token{Endpoints.Github}";
        QueryUriBuilder url = new($"{Endpoints.Webhooks}/{webhookId}/{webhookToken}{Endpoints.Github}");
        url.AddParameter("wait", "true");

        RestRequest request = new()
        {
            Route = route,
            Url = url.Build(),
            Method = HttpMethod.Post,
            Payload = jsonPayload,
            IsExemptFromGlobalLimit = true
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);
        DiscordMessage ret = JsonConvert.DeserializeObject<DiscordMessage>(res.Response!)!;
        ret.Discord = this.discord!;
        return ret;
    }

    public async ValueTask<DiscordMessage> EditWebhookMessageAsync
    (
        ulong webhookId,
        string webhookToken,
        ulong messageId,
        DiscordWebhookBuilder builder,
        IEnumerable<DiscordAttachment>? attachments = null
    )
    {
        builder.Validate(true);

        DiscordMentions? mentions = builder.Mentions != null ? new DiscordMentions(builder.Mentions, builder.Mentions.Any()) : null;

        RestWebhookMessageEditPayload pld = new()
        {
            Content = builder.Content,
            Embeds = builder.Embeds,
            Mentions = mentions,
            Flags = builder.Flags,
            Components = builder.Components,
            Attachments = attachments
        };

        string route = $"{Endpoints.Webhooks}/{webhookId}/:webhook_token/{Endpoints.Messages}/:message_id";
        QueryUriBuilder uriBuilder = new($"{Endpoints.Webhooks}/{webhookId}/{webhookToken}/{Endpoints.Messages}/{messageId}");

        uriBuilder.AddParameter("wait", "true");
        uriBuilder.AddParameter("with_components", "true");

        if (builder.ThreadId.HasValue)
        {
            uriBuilder.AddParameter("thread_id", builder.ThreadId.Value.ToString());
        }
        
        Dictionary<string, string> values = new()
        {
            ["payload_json"] = DiscordJson.SerializeObject(pld)
        };

        MultipartRestRequest request = new()
        {
            Route = route,
            Url = uriBuilder.Build(),
            Method = HttpMethod.Patch,
            Values = values,
            Files = builder.Files,
            IsExemptFromGlobalLimit = true
        };

        RestResponse res;
        try
        {
            res = await this.rest.ExecuteRequestAsync(request);
        }
        finally
        {
            builder.ResetFileStreamPositions();
        }

        return PrepareMessage(JObject.Parse(res.Response!));
    }

    public async ValueTask DeleteWebhookMessageAsync
    (
        ulong webhookId,
        string webhookToken,
        ulong messageId
    )
    {
        string route = $"{Endpoints.Webhooks}/{webhookId}/:webhook_token/{Endpoints.Messages}/:message_id";
        string url = $"{Endpoints.Webhooks}/{webhookId}/{webhookToken}/{Endpoints.Messages}/{messageId}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Delete,
            IsExemptFromGlobalLimit = true
        };

        await this.rest.ExecuteRequestAsync(request);
    }
    #endregion

    #region Reactions
    public async ValueTask CreateReactionAsync
    (
        ulong channelId,
        ulong messageId,
        string emoji
    )
    {
        string route = $"{Endpoints.Channels}/{channelId}/{Endpoints.Messages}/:message_id/{Endpoints.Reactions}/:emoji/{Endpoints.Me}";
        string url = $"{Endpoints.Channels}/{channelId}/{Endpoints.Messages}/{messageId}/{Endpoints.Reactions}/{emoji}/{Endpoints.Me}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Put
        };

        await this.rest.ExecuteRequestAsync(request);
    }

    public async ValueTask DeleteOwnReactionAsync
    (
        ulong channelId,
        ulong messageId,
        string emoji
    )
    {
        string route = $"{Endpoints.Channels}/{channelId}/{Endpoints.Messages}/:message_id/{Endpoints.Reactions}/:emoji/{Endpoints.Me}";
        string url = $"{Endpoints.Channels}/{channelId}/{Endpoints.Messages}/{messageId}/{Endpoints.Reactions}/{emoji}/{Endpoints.Me}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Delete
        };

        await this.rest.ExecuteRequestAsync(request);
    }

    public async ValueTask DeleteUserReactionAsync
    (
        ulong channelId,
        ulong messageId,
        ulong userId,
        string emoji,
        string? reason
    )
    {
        Dictionary<string, string> headers = [];
        if (!string.IsNullOrWhiteSpace(reason))
        {
            headers[ReasonHeaderName] = reason;
        }

        string route = $"{Endpoints.Channels}/{channelId}/{Endpoints.Messages}/:message_id/{Endpoints.Reactions}/:emoji/:user_id";
        string url = $"{Endpoints.Channels}/{channelId}/{Endpoints.Messages}/{messageId}/{Endpoints.Reactions}/{emoji}/{userId}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Delete,
            Headers = headers
        };

        await this.rest.ExecuteRequestAsync(request);
    }

    public async ValueTask<IReadOnlyList<DiscordUser>> GetReactionsAsync
    (
        ulong channelId,
        ulong messageId,
        string emoji,
        ulong? afterId = null,
        int limit = 25
    )
    {
        QueryUriBuilder urlparams = new($"{Endpoints.Channels}/{channelId}/{Endpoints.Messages}/{messageId}/{Endpoints.Reactions}/{emoji}");
        if (afterId.HasValue)
        {
            urlparams.AddParameter("after", afterId.Value.ToString(CultureInfo.InvariantCulture));
        }

        urlparams.AddParameter("limit", limit.ToString(CultureInfo.InvariantCulture));

        string route = $"{Endpoints.Channels}/{channelId}/{Endpoints.Messages}/:message_id/{Endpoints.Reactions}/:emoji";

        RestRequest request = new()
        {
            Route = route,
            Url = urlparams.Build(),
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        IEnumerable<TransportUser> usersRaw = JsonConvert.DeserializeObject<IEnumerable<TransportUser>>(res.Response!)!;
        List<DiscordUser> users = [];
        foreach (TransportUser xr in usersRaw)
        {
            DiscordUser usr = new(xr)
            {
                Discord = this.discord!
            };
            usr = this.discord!.UpdateUserCache(usr);

            users.Add(usr);
        }

        return users;
    }

    public async ValueTask DeleteAllReactionsAsync
    (
        ulong channelId,
        ulong messageId,
        string? reason = null
    )
    {
        Dictionary<string, string> headers = [];
        if (!string.IsNullOrWhiteSpace(reason))
        {
            headers[ReasonHeaderName] = reason;
        }

        string route = $"{Endpoints.Channels}/{channelId}/{Endpoints.Messages}/:message_id/{Endpoints.Reactions}";
        string url = $"{Endpoints.Channels}/{channelId}/{Endpoints.Messages}/{messageId}/{Endpoints.Reactions}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Delete,
            Headers = headers
        };

        await this.rest.ExecuteRequestAsync(request);
    }

    public async ValueTask DeleteReactionsEmojiAsync
    (
        ulong channelId,
        ulong messageId,
        string emoji
    )
    {
        string route = $"{Endpoints.Channels}/{channelId}/{Endpoints.Messages}/:message_id/{Endpoints.Reactions}/:emoji";
        string url = $"{Endpoints.Channels}/{channelId}/{Endpoints.Messages}/{messageId}/{Endpoints.Reactions}/{emoji}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Delete
        };

        await this.rest.ExecuteRequestAsync(request);
    }
    #endregion

    #region Polls

    public async ValueTask<IReadOnlyList<DiscordUser>> GetPollAnswerVotersAsync
    (
        ulong channelId,
        ulong messageId,
        int answerId,
        ulong? after,
        int? limit
    )
    {
        string route = $"{Endpoints.Channels}/{channelId}/{Endpoints.Polls}/:message_id/{Endpoints.Answers}/:answer_id";
        QueryUriBuilder url = new($"{Endpoints.Channels}/{channelId}/{Endpoints.Polls}/{messageId}/{Endpoints.Answers}/{answerId}");

        if (limit > 0)
        {
            url.AddParameter("limit", limit.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (after > 0)
        {
            url.AddParameter("after", after.Value.ToString(CultureInfo.InvariantCulture));
        }

        RestRequest request = new()
        {
            Route = route,
            Url = url.Build(),
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        JToken jto = JToken.Parse(res.Response!);

        return (jto as JArray ?? jto["users"] as JArray)!
            .Select(j => j.ToDiscordObject<DiscordUser>())
            .ToList();
    }

    public async ValueTask<DiscordMessage> EndPollAsync
    (
        ulong channelId,
        ulong messageId
    )
    {
        string route = $"{Endpoints.Channels}/{channelId}/{Endpoints.Polls}/:message_id/{Endpoints.Expire}";
        string url = $"{Endpoints.Channels}/{channelId}/{Endpoints.Polls}/{messageId}/{Endpoints.Expire}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Post
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        DiscordMessage ret = PrepareMessage(JObject.Parse(res.Response!));

        return ret;
    }

    #endregion

    #region Emoji
    public async ValueTask<IReadOnlyList<DiscordGuildEmoji>> GetGuildEmojisAsync
    (
        ulong guildId
    )
    {
        string route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Emojis}";
        string url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Emojis}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        IEnumerable<JObject> emojisRaw = JsonConvert.DeserializeObject<IEnumerable<JObject>>(res.Response!)!;

        this.discord!.Guilds.TryGetValue(guildId, out DiscordGuild? guild);
        Dictionary<ulong, DiscordUser> users = [];
        List<DiscordGuildEmoji> emojis = [];
        foreach (JObject rawEmoji in emojisRaw)
        {
            DiscordGuildEmoji discordGuildEmoji = rawEmoji.ToDiscordObject<DiscordGuildEmoji>();

            if (guild is not null)
            {
                discordGuildEmoji.Guild = guild;
            }

            TransportUser? rawUser = rawEmoji["user"]?.ToDiscordObject<TransportUser>();
            if (rawUser != null)
            {
                if (!users.ContainsKey(rawUser.Id))
                {
                    DiscordUser user = guild is not null && guild.Members.TryGetValue(rawUser.Id, out DiscordMember? member) ? member : new DiscordUser(rawUser);
                    users[user.Id] = user;
                }

                discordGuildEmoji.User = users[rawUser.Id];
            }

            emojis.Add(discordGuildEmoji);
        }

        return emojis;
    }

    public async ValueTask<DiscordGuildEmoji> GetGuildEmojiAsync
    (
        ulong guildId,
        ulong emojiId
    )
    {
        string route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Emojis}/:emoji_id";
        string url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Emojis}/{emojiId}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        this.discord!.Guilds.TryGetValue(guildId, out DiscordGuild? guild);

        JObject emojiRaw = JObject.Parse(res.Response!);
        DiscordGuildEmoji emoji = emojiRaw.ToDiscordObject<DiscordGuildEmoji>();

        if (guild is not null)
        {
            emoji.Guild = guild;
        }

        TransportUser? rawUser = emojiRaw["user"]?.ToDiscordObject<TransportUser>();
        if (rawUser != null)
        {
            emoji.User = guild is not null && guild.Members.TryGetValue(rawUser.Id, out DiscordMember? member) ? member : new DiscordUser(rawUser);
        }

        return emoji;
    }

    public async ValueTask<DiscordGuildEmoji> CreateGuildEmojiAsync
    (
        ulong guildId,
        string name,
        string imageb64,
        IEnumerable<ulong>? roles = null,
        string? reason = null
    )
    {
        RestGuildEmojiCreatePayload pld = new()
        {
            Name = name,
            ImageB64 = imageb64,
            Roles = roles?.ToArray()
        };

        Dictionary<string, string> headers = [];
        if (!string.IsNullOrWhiteSpace(reason))
        {
            headers[ReasonHeaderName] = reason;
        }

        string route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Emojis}";
        string url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Emojis}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Post,
            Payload = DiscordJson.SerializeObject(pld),
            Headers = headers
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        this.discord!.Guilds.TryGetValue(guildId, out DiscordGuild? guild);

        JObject emojiRaw = JObject.Parse(res.Response!);
        DiscordGuildEmoji emoji = emojiRaw.ToDiscordObject<DiscordGuildEmoji>();

        if (guild is not null)
        {
            emoji.Guild = guild;
        }

        TransportUser? rawUser = emojiRaw["user"]?.ToDiscordObject<TransportUser>();
        emoji.User = rawUser != null
            ? guild is not null && guild.Members.TryGetValue(rawUser.Id, out DiscordMember? member) ? member : new DiscordUser(rawUser)
            : this.discord.CurrentUser;

        return emoji;
    }

    public async ValueTask<DiscordGuildEmoji> ModifyGuildEmojiAsync
    (
        ulong guildId,
        ulong emojiId,
        string? name = null,
        IEnumerable<ulong>? roles = null,
        string? reason = null
    )
    {
        RestGuildEmojiModifyPayload pld = new()
        {
            Name = name,
            Roles = roles?.ToArray()
        };

        Dictionary<string, string> headers = [];
        if (!string.IsNullOrWhiteSpace(reason))
        {
            headers[ReasonHeaderName] = reason;
        }

        string route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Emojis}/:emoji_id";
        string url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Emojis}/{emojiId}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Patch,
            Payload = DiscordJson.SerializeObject(pld),
            Headers = headers
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        this.discord!.Guilds.TryGetValue(guildId, out DiscordGuild? guild);

        JObject emojiRaw = JObject.Parse(res.Response!);
        DiscordGuildEmoji emoji = emojiRaw.ToDiscordObject<DiscordGuildEmoji>();

        if (guild is not null)
        {
            emoji.Guild = guild;
        }

        TransportUser? rawUser = emojiRaw["user"]?.ToDiscordObject<TransportUser>();
        if (rawUser != null)
        {
            emoji.User = guild is not null && guild.Members.TryGetValue(rawUser.Id, out DiscordMember? member) ? member : new DiscordUser(rawUser);
        }

        return emoji;
    }

    public async ValueTask DeleteGuildEmojiAsync
    (
        ulong guildId,
        ulong emojiId,
        string? reason = null
    )
    {
        Dictionary<string, string> headers = [];
        if (!string.IsNullOrWhiteSpace(reason))
        {
            headers[ReasonHeaderName] = reason;
        }

        string route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Emojis}/:emoji_id";
        string url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.Emojis}/{emojiId}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Delete,
            Headers = headers
        };

        await this.rest.ExecuteRequestAsync(request);
    }
    #endregion

    #region Application Commands
    public async ValueTask<IReadOnlyList<DiscordApplicationCommand>> GetGlobalApplicationCommandsAsync
    (
        ulong applicationId,
        bool withLocalizations = false
    )
    {
        string route = $"{Endpoints.Applications}/:application_id/{Endpoints.Commands}";
        QueryUriBuilder builder = new($"{Endpoints.Applications}/{applicationId}/{Endpoints.Commands}");

        if (withLocalizations)
        {
            builder.AddParameter("with_localizations", "true");
        }

        RestRequest request = new()
        {
            Route = route,
            Url = builder.Build(),
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        IEnumerable<DiscordApplicationCommand> ret = JsonConvert.DeserializeObject<IEnumerable<DiscordApplicationCommand>>(res.Response!)!;
        foreach (DiscordApplicationCommand app in ret)
        {
            app.Discord = this.discord!;
        }

        return ret.ToList();
    }

    public async ValueTask<IReadOnlyList<DiscordApplicationCommand>> BulkOverwriteGlobalApplicationCommandsAsync
    (
        ulong applicationId,
        IEnumerable<DiscordApplicationCommand> commands
    )
    {
        List<RestApplicationCommandCreatePayload> pld = [];
        foreach (DiscordApplicationCommand command in commands)
        {
            pld.Add(new RestApplicationCommandCreatePayload
            {
                Type = command.Type,
                Name = command.Name,
                Description = command.Description,
                Options = command.Options,
                DefaultPermission = command.DefaultPermission,
                NameLocalizations = command.NameLocalizations,
                DescriptionLocalizations = command.DescriptionLocalizations,
                AllowDmUsage = command.AllowDmUsage,
                DefaultMemberPermissions = command.DefaultMemberPermissions,
                Nsfw = command.Nsfw,
                AllowedContexts = command.Contexts,
                InstallTypes = command.IntegrationTypes,
            });
        }

        string route = $"{Endpoints.Applications}/:application_id/{Endpoints.Commands}";
        string url = $"{Endpoints.Applications}/{applicationId}/{Endpoints.Commands}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Put,
            Payload = DiscordJson.SerializeObject(pld)
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        IEnumerable<DiscordApplicationCommand> ret = JsonConvert.DeserializeObject<IEnumerable<DiscordApplicationCommand>>(res.Response!)!;
        foreach (DiscordApplicationCommand app in ret)
        {
            app.Discord = this.discord!;
        }

        return ret.ToList();
    }

    public async ValueTask<DiscordApplicationCommand> CreateGlobalApplicationCommandAsync
    (
        ulong applicationId,
        DiscordApplicationCommand command
    )
    {
        RestApplicationCommandCreatePayload pld = new()
        {
            Type = command.Type,
            Name = command.Name,
            Description = command.Description,
            Options = command.Options,
            DefaultPermission = command.DefaultPermission,
            NameLocalizations = command.NameLocalizations,
            DescriptionLocalizations = command.DescriptionLocalizations,
            AllowDmUsage = command.AllowDmUsage,
            DefaultMemberPermissions = command.DefaultMemberPermissions,
            Nsfw = command.Nsfw,
            AllowedContexts = command.Contexts,
            InstallTypes = command.IntegrationTypes,
        };

        string route = $"{Endpoints.Applications}/:application_id/{Endpoints.Commands}";
        string url = $"{Endpoints.Applications}/{applicationId}/{Endpoints.Commands}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Post,
            Payload = DiscordJson.SerializeObject(pld)
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        DiscordApplicationCommand ret = JsonConvert.DeserializeObject<DiscordApplicationCommand>(res.Response!)!;
        ret.Discord = this.discord!;

        return ret;
    }

    public async ValueTask<DiscordApplicationCommand> GetGlobalApplicationCommandAsync
    (
        ulong applicationId,
        ulong commandId
    )
    {
        string route = $"{Endpoints.Applications}/:application_id/{Endpoints.Commands}/:command_id";
        string url = $"{Endpoints.Applications}/{applicationId}/{Endpoints.Commands}/{commandId}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        DiscordApplicationCommand ret = JsonConvert.DeserializeObject<DiscordApplicationCommand>(res.Response!)!;
        ret.Discord = this.discord!;

        return ret;
    }

    public async ValueTask<DiscordApplicationCommand> EditGlobalApplicationCommandAsync
    (
        ulong applicationId,
        ulong commandId,
        Optional<string> name = default,
        Optional<string> description = default,
        Optional<IReadOnlyList<DiscordApplicationCommandOption>> options = default,
        Optional<bool?> defaultPermission = default,
        Optional<bool?> nsfw = default,
        IReadOnlyDictionary<string, string>? nameLocalizations = null,
        IReadOnlyDictionary<string, string>? descriptionLocalizations = null,
        Optional<bool> allowDmUsage = default,
        Optional<DiscordPermissions?> defaultMemberPermissions = default,
        Optional<IEnumerable<DiscordInteractionContextType>> allowedContexts = default,
        Optional<IEnumerable<DiscordApplicationIntegrationType>> installTypes = default
    )
    {
        RestApplicationCommandEditPayload pld = new()
        {
            Name = name,
            Description = description,
            Options = options,
            DefaultPermission = defaultPermission,
            NameLocalizations = nameLocalizations,
            DescriptionLocalizations = descriptionLocalizations,
            AllowDmUsage = allowDmUsage,
            DefaultMemberPermissions = defaultMemberPermissions,
            Nsfw = nsfw,
            AllowedContexts = allowedContexts,
            InstallTypes = installTypes,
        };

        string route = $"{Endpoints.Applications}/:application_id/{Endpoints.Commands}/:command_id";
        string url = $"{Endpoints.Applications}/{applicationId}/{Endpoints.Commands}/{commandId}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Patch,
            Payload = DiscordJson.SerializeObject(pld)
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        DiscordApplicationCommand ret = JsonConvert.DeserializeObject<DiscordApplicationCommand>(res.Response!)!;
        ret.Discord = this.discord!;

        return ret;
    }

    public async ValueTask DeleteGlobalApplicationCommandAsync
    (
        ulong applicationId,
        ulong commandId
    )
    {
        string route = $"{Endpoints.Applications}/:application_id/{Endpoints.Commands}/:command_id";
        string url = $"{Endpoints.Applications}/{applicationId}/{Endpoints.Commands}/{commandId}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Delete
        };

        await this.rest.ExecuteRequestAsync(request);
    }

    public async ValueTask<IReadOnlyList<DiscordApplicationCommand>> GetGuildApplicationCommandsAsync
    (
        ulong applicationId,
        ulong guildId,
        bool withLocalizations = false
    )
    {
        string route = $"{Endpoints.Applications}/:application_id/{Endpoints.Guilds}/:guild_id/{Endpoints.Commands}";
        QueryUriBuilder builder = new($"{Endpoints.Applications}/{applicationId}/{Endpoints.Guilds}/{guildId}/{Endpoints.Commands}");

        if (withLocalizations)
        {
            builder.AddParameter("with_localizations", "true");
        }

        RestRequest request = new()
        {
            Route = route,
            Url = builder.Build(),
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        IEnumerable<DiscordApplicationCommand> ret = JsonConvert.DeserializeObject<IEnumerable<DiscordApplicationCommand>>(res.Response!)!;
        foreach (DiscordApplicationCommand app in ret)
        {
            app.Discord = this.discord!;
        }

        return ret.ToList();
    }

    public async ValueTask<IReadOnlyList<DiscordApplicationCommand>> BulkOverwriteGuildApplicationCommandsAsync
    (
        ulong applicationId,
        ulong guildId,
        IEnumerable<DiscordApplicationCommand> commands
    )
    {
        List<RestApplicationCommandCreatePayload> pld = [];
        foreach (DiscordApplicationCommand command in commands)
        {
            pld.Add(new RestApplicationCommandCreatePayload
            {
                Type = command.Type,
                Name = command.Name,
                Description = command.Description,
                Options = command.Options,
                DefaultPermission = command.DefaultPermission,
                NameLocalizations = command.NameLocalizations,
                DescriptionLocalizations = command.DescriptionLocalizations,
                AllowDmUsage = command.AllowDmUsage,
                DefaultMemberPermissions = command.DefaultMemberPermissions,
                Nsfw = command.Nsfw
            });
        }

        string route = $"{Endpoints.Applications}/:application_id/{Endpoints.Guilds}/:guild_id/{Endpoints.Commands}";
        string url = $"{Endpoints.Applications}/{applicationId}/{Endpoints.Guilds}/{guildId}/{Endpoints.Commands}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Put,
            Payload = DiscordJson.SerializeObject(pld)
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        IEnumerable<DiscordApplicationCommand> ret = JsonConvert.DeserializeObject<IEnumerable<DiscordApplicationCommand>>(res.Response!)!;
        foreach (DiscordApplicationCommand app in ret)
        {
            app.Discord = this.discord!;
        }

        return ret.ToList();
    }

    public async ValueTask<DiscordApplicationCommand> CreateGuildApplicationCommandAsync
    (
        ulong applicationId,
        ulong guildId,
        DiscordApplicationCommand command
    )
    {
        RestApplicationCommandCreatePayload pld = new()
        {
            Type = command.Type,
            Name = command.Name,
            Description = command.Description,
            Options = command.Options,
            DefaultPermission = command.DefaultPermission,
            NameLocalizations = command.NameLocalizations,
            DescriptionLocalizations = command.DescriptionLocalizations,
            AllowDmUsage = command.AllowDmUsage,
            DefaultMemberPermissions = command.DefaultMemberPermissions,
            Nsfw = command.Nsfw
        };

        string route = $"{Endpoints.Applications}/:application_id/{Endpoints.Guilds}/:guild_id/{Endpoints.Commands}";
        string url = $"{Endpoints.Applications}/{applicationId}/{Endpoints.Guilds}/{guildId}/{Endpoints.Commands}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Post,
            Payload = DiscordJson.SerializeObject(pld)
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        DiscordApplicationCommand ret = JsonConvert.DeserializeObject<DiscordApplicationCommand>(res.Response!)!;
        ret.Discord = this.discord!;

        return ret;
    }

    public async ValueTask<DiscordApplicationCommand> GetGuildApplicationCommandAsync
    (
        ulong applicationId,
        ulong guildId,
        ulong commandId
    )
    {
        string route = $"{Endpoints.Applications}/:application_id/{Endpoints.Guilds}/:guild_id/{Endpoints.Commands}/:command_id";
        string url = $"{Endpoints.Applications}/{applicationId}/{Endpoints.Guilds}/{guildId}/{Endpoints.Commands}/{commandId}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        DiscordApplicationCommand ret = JsonConvert.DeserializeObject<DiscordApplicationCommand>(res.Response!)!;
        ret.Discord = this.discord!;

        return ret;
    }

    public async ValueTask<DiscordApplicationCommand> EditGuildApplicationCommandAsync
    (
        ulong applicationId,
        ulong guildId,
        ulong commandId,
        Optional<string> name = default,
        Optional<string> description = default,
        Optional<IReadOnlyList<DiscordApplicationCommandOption>> options = default,
        Optional<bool?> defaultPermission = default,
        Optional<bool?> nsfw = default,
        IReadOnlyDictionary<string, string>? nameLocalizations = null,
        IReadOnlyDictionary<string, string>? descriptionLocalizations = null,
        Optional<bool> allowDmUsage = default,
        Optional<DiscordPermissions?> defaultMemberPermissions = default,
        Optional<IEnumerable<DiscordInteractionContextType>> allowedContexts = default,
        Optional<IEnumerable<DiscordApplicationIntegrationType>> installTypes = default
    )
    {
        RestApplicationCommandEditPayload pld = new()
        {
            Name = name,
            Description = description,
            Options = options,
            DefaultPermission = defaultPermission,
            NameLocalizations = nameLocalizations,
            DescriptionLocalizations = descriptionLocalizations,
            AllowDmUsage = allowDmUsage,
            DefaultMemberPermissions = defaultMemberPermissions,
            Nsfw = nsfw,
            AllowedContexts = allowedContexts,
            InstallTypes = installTypes
        };

        string route = $"{Endpoints.Applications}/:application_id/{Endpoints.Guilds}/:guild_id/{Endpoints.Commands}/:command_id";
        string url = $"{Endpoints.Applications}/{applicationId}/{Endpoints.Guilds}/{guildId}/{Endpoints.Commands}/{commandId}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Patch,
            Payload = DiscordJson.SerializeObject(pld)
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        DiscordApplicationCommand ret = JsonConvert.DeserializeObject<DiscordApplicationCommand>(res.Response!)!;
        ret.Discord = this.discord!;

        return ret;
    }

    public async ValueTask DeleteGuildApplicationCommandAsync
    (
        ulong applicationId,
        ulong guildId,
        ulong commandId
    )
    {
        string route = $"{Endpoints.Applications}/:application_id/{Endpoints.Guilds}/:guild_id/{Endpoints.Commands}/:command_id";
        string url = $"{Endpoints.Applications}/{applicationId}/{Endpoints.Guilds}/{guildId}/{Endpoints.Commands}/{commandId}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Delete
        };

        await this.rest.ExecuteRequestAsync(request);
    }

    internal async ValueTask CreateInteractionResponseAsync
    (
        ulong interactionId,
        string interactionToken,
        DiscordInteractionResponseType type,
        string? content = null,
        IReadOnlyList<DiscordEmbed>? embeds = null,
        bool isTts = false,
        string? customId = null,
        string? title = null,
        IReadOnlyList<IMention>? mentions = null,
        IReadOnlyList<DiscordComponent>? components = null,
        DiscordMessageFlags? flags = null,
        IReadOnlyList<DiscordAutoCompleteChoice>? choices = null,
        DiscordPollBuilder? pollBuilder = null,
        IReadOnlyList<DiscordMessageFile>? files = null
    )
    {
        bool hasContent = content is not null || embeds is not null || components is not null || choices is not null || pollBuilder is not null;

        if (embeds is not null)
        {
            foreach (DiscordEmbed embed in embeds)
            {
                embed.Timestamp = embed.Timestamp?.ToUniversalTime();
            }
        }

        DiscordInteractionResponsePayload payload = new()
        {
            Type = type,
            Data = !hasContent
                ? null
                : new DiscordInteractionApplicationCommandCallbackData
                {
                    Content = content,
                    IsTts = isTts,
                    Title = title,
                    CustomId = customId,
                    Embeds = embeds,
                    Mentions = new DiscordMentions(mentions ?? Mentions.All, mentions is not (null or [])),
                    Components = components,
                    Choices = choices,
                    Poll = pollBuilder?.BuildInternal(),
                    Flags = flags,
                }
        };
        
        Dictionary<string, string> values = [];

        if (hasContent)
        {
            if (!string.IsNullOrEmpty(content) || embeds?.Count > 0 || isTts || mentions != null)
            {
                values["payload_json"] = DiscordJson.SerializeObject(payload);
            }
        }

        string route = $"{Endpoints.Interactions}/{interactionId}/:interaction_token/{Endpoints.Callback}";
        string url = $"{Endpoints.Interactions}/{interactionId}/{interactionToken}/{Endpoints.Callback}";

        
        if (files is not (null or []))
        {
            MultipartRestRequest request = new()
            {
                Route = route,
                Url = url,
                Method = HttpMethod.Post,
                Values = values,
                Files = files,
                IsExemptFromAllLimits = true
            };

            try
            {
                await this.rest.ExecuteRequestAsync(request);
            }
            finally
            {
                foreach (DiscordMessageFile file in files)
                {
                    if (file.ResetPositionTo is long pos)
                    {
                        file.Stream.Seek(pos, SeekOrigin.Begin);
                    }
                }
            }
        }
        else
        {
            RestRequest request = new()
            {
                Route = route,
                Url = url,
                Method = HttpMethod.Post,
                Payload = DiscordJson.SerializeObject(payload),
                IsExemptFromGlobalLimit = true
            };

            await this.rest.ExecuteRequestAsync(request);
        }
    }

    public ValueTask CreateInteractionResponseAsync
    (
        ulong interactionId,
        string interactionToken,
        DiscordInteractionResponseType type,
        DiscordInteractionResponseBuilder? builder
    )
    {
        return CreateInteractionResponseAsync
        (
         interactionId,
         interactionToken,
         type,
         builder?.Content,
         builder?.Embeds,
         builder?.IsTts ?? false,
         null,
         null,
         builder?.Mentions,
         builder?.Components,
         builder?.Flags,
         builder?.Choices,
         builder?.Poll,
         builder?.Files
        );
    }

    public async ValueTask<DiscordMessage> GetOriginalInteractionResponseAsync
    (
        ulong applicationId,
        string interactionToken
    )
    {
        string route = $"{Endpoints.Webhooks}/:application_id/{interactionToken}/{Endpoints.Messages}/{Endpoints.Original}";
        string url = $"{Endpoints.Webhooks}/{applicationId}/{interactionToken}/{Endpoints.Messages}/{Endpoints.Original}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Get,
            IsExemptFromGlobalLimit = true
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);
        DiscordMessage ret = JsonConvert.DeserializeObject<DiscordMessage>(res.Response!)!;

        ret.Channel = (this.discord as DiscordClient).InternalGetCachedChannel(ret.ChannelId);
        ret.Discord = this.discord!;

        return ret;
    }

    public async ValueTask<DiscordMessage> EditOriginalInteractionResponseAsync
    (
        ulong applicationId,
        string interactionToken,
        DiscordWebhookBuilder builder,
        IEnumerable<DiscordAttachment> attachments
    )
    {
        {
            builder.Validate(true);

            DiscordMentions? mentions = builder.Mentions != null ? new DiscordMentions(builder.Mentions, builder.Mentions.Any()) : null;

            if (builder.Files.Any())
            {
                attachments ??= [];
            }

            RestWebhookMessageEditPayload pld = new()
            {
                Content = builder.Content,
                Embeds = builder.Embeds,
                Mentions = mentions,
                Flags = builder.Flags,
                Components = builder.Components,
                Attachments = attachments
            };

            string route = $"{Endpoints.Webhooks}/:application_id/{interactionToken}/{Endpoints.Messages}/@original";
            string url = $"{Endpoints.Webhooks}/{applicationId}/{interactionToken}/{Endpoints.Messages}/@original";

            Dictionary<string, string> values = new()
            {
                ["payload_json"] = DiscordJson.SerializeObject(pld)
            };

            MultipartRestRequest request = new()
            {
                Route = route,
                Url = url,
                Method = HttpMethod.Patch,
                Values = values,
                Files = builder.Files,
                IsExemptFromAllLimits = true
            };

            RestResponse res = await this.rest.ExecuteRequestAsync(request);

            DiscordMessage ret = JsonConvert.DeserializeObject<DiscordMessage>(res.Response!)!;
            ret.Discord = this.discord!;

            foreach (DiscordMessageFile file in builder.Files.Where(x => x.ResetPositionTo.HasValue))
            {
                file.Stream.Position = file.ResetPositionTo!.Value;
            }

            return ret;
        }
    }

    public async ValueTask DeleteOriginalInteractionResponseAsync
    (
        ulong applicationId,
        string interactionToken
    )
    {
        string route = $"{Endpoints.Webhooks}/:application_id/{interactionToken}/{Endpoints.Messages}/@original";
        string url = $"{Endpoints.Webhooks}/{applicationId}/{interactionToken}/{Endpoints.Messages}/@original";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Delete,
            IsExemptFromAllLimits = true
        };

        await this.rest.ExecuteRequestAsync(request);
    }

    public async ValueTask<DiscordMessage> CreateFollowupMessageAsync
    (
        ulong applicationId,
        string interactionToken,
        DiscordFollowupMessageBuilder builder
    )
    {
        builder.Validate();

        if (builder.Embeds != null)
        {
            foreach (DiscordEmbed embed in builder.Embeds)
            {
                if (embed.Timestamp != null)
                {
                    embed.Timestamp = embed.Timestamp.Value.ToUniversalTime();
                }
            }
        }

        Dictionary<string, string> values = [];
        RestFollowupMessageCreatePayload pld = new()
        {
            Content = builder.Content,
            IsTts = builder.IsTts,
            Embeds = builder.Embeds,
            Flags = builder.Flags,
            Components = builder.Components
        };

        if (builder.Mentions != null)
        {
            pld.Mentions = new DiscordMentions(builder.Mentions, builder.Mentions.Any());
        }

        if (!string.IsNullOrEmpty(builder.Content) || builder.Embeds?.Count > 0 || builder.IsTts == true || builder.Mentions != null)
        {
            values["payload_json"] = DiscordJson.SerializeObject(pld);
        }

        string route = $"{Endpoints.Webhooks}/:application_id/{interactionToken}";
        string url = $"{Endpoints.Webhooks}/{applicationId}/{interactionToken}";

        MultipartRestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Post,
            Values = values,
            Files = builder.Files,
            IsExemptFromAllLimits = true
        };

        RestResponse res;
        try
        {
            res = await this.rest.ExecuteRequestAsync(request);
        }
        finally
        {
            builder.ResetFileStreamPositions();
        }
        DiscordMessage ret = JsonConvert.DeserializeObject<DiscordMessage>(res.Response!)!;

        ret.Discord = this.discord!;
        return ret;
    }

    internal ValueTask<DiscordMessage> GetFollowupMessageAsync
    (
        ulong applicationId,
        string interactionToken,
        ulong messageId
    )
        => GetWebhookMessageAsync(applicationId, interactionToken, messageId);

    internal ValueTask<DiscordMessage> EditFollowupMessageAsync
    (
        ulong applicationId,
        string interactionToken,
        ulong messageId,
        DiscordWebhookBuilder builder,
        IEnumerable<DiscordAttachment>? attachments
    )
        => EditWebhookMessageAsync(applicationId, interactionToken, messageId, builder, attachments ?? []);

    internal ValueTask DeleteFollowupMessageAsync(ulong applicationId, string interactionToken, ulong messageId)
        => DeleteWebhookMessageAsync(applicationId, interactionToken, messageId);

    public async ValueTask<IReadOnlyList<DiscordGuildApplicationCommandPermissions>> GetGuildApplicationCommandPermissionsAsync
    (
        ulong applicationId,
        ulong guildId
    )
    {
        string route = $"{Endpoints.Applications}/:application_id/{Endpoints.Guilds}/:guild_id/{Endpoints.Commands}/{Endpoints.Permissions}";
        string url = $"{Endpoints.Applications}/{applicationId}/{Endpoints.Guilds}/{guildId}/{Endpoints.Commands}/{Endpoints.Permissions}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);
        IEnumerable<DiscordGuildApplicationCommandPermissions> ret = JsonConvert.DeserializeObject<IEnumerable<DiscordGuildApplicationCommandPermissions>>(res.Response!)!;

        foreach (DiscordGuildApplicationCommandPermissions perm in ret)
        {
            perm.Discord = this.discord!;
        }

        return ret.ToList();
    }

    public async ValueTask<DiscordGuildApplicationCommandPermissions> GetApplicationCommandPermissionsAsync
    (
        ulong applicationId,
        ulong guildId,
        ulong commandId
    )
    {
        string route = $"{Endpoints.Applications}/:application_id/{Endpoints.Guilds}/:guild_id/{Endpoints.Commands}/:command_id/{Endpoints.Permissions}";
        string url = $"{Endpoints.Applications}/{applicationId}/{Endpoints.Guilds}/{guildId}/{Endpoints.Commands}/{commandId}/{Endpoints.Permissions}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);
        DiscordGuildApplicationCommandPermissions ret = JsonConvert.DeserializeObject<DiscordGuildApplicationCommandPermissions>(res.Response!)!;

        ret.Discord = this.discord!;
        return ret;
    }

    public async ValueTask<DiscordGuildApplicationCommandPermissions> EditApplicationCommandPermissionsAsync
    (
        ulong applicationId,
        ulong guildId,
        ulong commandId,
        IEnumerable<DiscordApplicationCommandPermission> permissions
    )
    {

        RestEditApplicationCommandPermissionsPayload pld = new()
        {
            Permissions = permissions
        };

        string route = $"{Endpoints.Applications}/:application_id/{Endpoints.Guilds}/:guild_id/{Endpoints.Commands}/:command_id/{Endpoints.Permissions}";
        string url = $"{Endpoints.Applications}/{applicationId}/{Endpoints.Guilds}/{guildId}/{Endpoints.Commands}/{commandId}/{Endpoints.Permissions}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Put,
            Payload = DiscordJson.SerializeObject(pld)
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);
        DiscordGuildApplicationCommandPermissions ret =
            JsonConvert.DeserializeObject<DiscordGuildApplicationCommandPermissions>(res.Response!)!;

        ret.Discord = this.discord!;
        return ret;
    }

    public async ValueTask<IReadOnlyList<DiscordGuildApplicationCommandPermissions>> BatchEditApplicationCommandPermissionsAsync
    (
        ulong applicationId,
        ulong guildId,
        IEnumerable<DiscordGuildApplicationCommandPermissions> permissions
    )
    {
        string route = $"{Endpoints.Applications}/:application_id/{Endpoints.Guilds}/:guild_id/{Endpoints.Commands}/{Endpoints.Permissions}";
        string url = $"{Endpoints.Applications}/{applicationId}/{Endpoints.Guilds}/{guildId}/{Endpoints.Commands}/{Endpoints.Permissions}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Put,
            Payload = DiscordJson.SerializeObject(permissions)
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);
        IEnumerable<DiscordGuildApplicationCommandPermissions> ret =
            JsonConvert.DeserializeObject<IEnumerable<DiscordGuildApplicationCommandPermissions>>(res.Response!)!;

        foreach (DiscordGuildApplicationCommandPermissions perm in ret)
        {
            perm.Discord = this.discord!;
        }

        return ret.ToList();
    }
    #endregion

    #region Misc
    internal ValueTask<TransportApplication> GetCurrentApplicationInfoAsync()
        => GetApplicationInfoAsync("@me");

    internal ValueTask<TransportApplication> GetApplicationInfoAsync
    (
        ulong applicationId
    )
        => GetApplicationInfoAsync(applicationId.ToString(CultureInfo.InvariantCulture));

    private async ValueTask<TransportApplication> GetApplicationInfoAsync
    (
        string applicationId
    )
    {
        string route = $"{Endpoints.Oauth2}/{Endpoints.Applications}/:application_id";
        string url = $"{Endpoints.Oauth2}/{Endpoints.Applications}/{applicationId}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        return JsonConvert.DeserializeObject<TransportApplication>(res.Response!)!;
    }

    public async ValueTask<IReadOnlyList<DiscordApplicationAsset>> GetApplicationAssetsAsync
    (
        DiscordApplication application
     )
    {
        string route = $"{Endpoints.Oauth2}/{Endpoints.Applications}/:application_id/{Endpoints.Assets}";
        string url = $"{Endpoints.Oauth2}/{Endpoints.Applications}/{application.Id}/{Endpoints.Assets}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        List<DiscordApplicationAsset> assets 
            = JsonConvert.DeserializeObject<IEnumerable<DiscordApplicationAsset>>(res.Response!)!.ToList();

        foreach (DiscordApplicationAsset asset in assets)
        {
            asset.Discord = application.Discord;
            asset.Application = application;
        }

        return assets;
    }

    public async ValueTask<GatewayInfo> GetGatewayInfoAsync()
    {
        Dictionary<string, string> headers = [];
        string route = $"{Endpoints.Gateway}/{Endpoints.Bot}";
        string url = route;

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Get,
            Headers = headers
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);

        GatewayInfo info = JObject.Parse(res.Response!).ToDiscordObject<GatewayInfo>();
        info.SessionBucket.ResetAfter = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(info.SessionBucket.ResetAfterInternal);
        return info;
    }
    #endregion

    public async ValueTask<DiscordEmoji> CreateApplicationEmojiAsync(ulong applicationId, string name, string image)
    {
        string route = $"{Endpoints.Applications}/{applicationId}/{Endpoints.Emojis}";
        string url = $"{Endpoints.Applications}/{applicationId}/{Endpoints.Emojis}";

        RestApplicationEmojiCreatePayload pld = new()
        {
            Name = name,
            ImageB64 = image
        };

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Post,
            Payload = DiscordJson.SerializeObject(pld)
        };


        RestResponse res = await this.rest.ExecuteRequestAsync(request);
        DiscordEmoji emoji = JsonConvert.DeserializeObject<DiscordEmoji>(res.Response!)!;
        emoji.Discord = this.discord!;

        return emoji;
    }

    public async ValueTask<DiscordEmoji> ModifyApplicationEmojiAsync(ulong applicationId, ulong emojiId, string name)
    {
        string route = $"{Endpoints.Applications}/{applicationId}/{Endpoints.Emojis}/{emojiId}";
        string url = $"{Endpoints.Applications}/{applicationId}/{Endpoints.Emojis}/{emojiId}";

        RestApplicationEmojiModifyPayload pld = new()
        {
            Name = name,
        };

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Patch,
            Payload = DiscordJson.SerializeObject(pld)
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);
        DiscordEmoji emoji = JsonConvert.DeserializeObject<DiscordEmoji>(res.Response!)!;

        emoji.Discord = this.discord!;

        return emoji;
    }

    public async ValueTask DeleteApplicationEmojiAsync(ulong applicationId, ulong emojiId)
    {
        string route = $"{Endpoints.Applications}/{applicationId}/{Endpoints.Emojis}/{emojiId}";
        string url = $"{Endpoints.Applications}/{applicationId}/{Endpoints.Emojis}/{emojiId}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Delete
        };

        await this.rest.ExecuteRequestAsync(request);
    }

    public async ValueTask<DiscordEmoji> GetApplicationEmojiAsync(ulong applicationId, ulong emojiId)
    {
        string route = $"{Endpoints.Applications}/{applicationId}/{Endpoints.Emojis}/{emojiId}";
        string url = $"{Endpoints.Applications}/{applicationId}/{Endpoints.Emojis}/{emojiId}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);
        DiscordEmoji emoji = JsonConvert.DeserializeObject<DiscordEmoji>(res.Response!)!;
        emoji.Discord = this.discord!;

        return emoji;
    }

    public async ValueTask<IReadOnlyList<DiscordEmoji>> GetApplicationEmojisAsync(ulong applicationId)
    {
        string route = $"{Endpoints.Applications}/{applicationId}/{Endpoints.Emojis}";
        string url = $"{Endpoints.Applications}/{applicationId}/{Endpoints.Emojis}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);
        IEnumerable<DiscordEmoji> emojis = JObject.Parse(res.Response!)["items"]!.ToDiscordObject<DiscordEmoji[]>();

        foreach (DiscordEmoji emoji in emojis)
        {
            emoji.Discord = this.discord!;
            emoji.User!.Discord = this.discord!;
        }

        return emojis.ToList();
    }

    public async ValueTask<DiscordForumPostStarter> CreateForumPostAsync
    (
        ulong channelId,
        string name,
        DiscordMessageBuilder message,
        DiscordAutoArchiveDuration? autoArchiveDuration = null,
        int? rateLimitPerUser = null,
        IEnumerable<ulong>? appliedTags = null
    )
    {
        string route = $"{Endpoints.Channels}/{channelId}/{Endpoints.Threads}";
        string url = $"{Endpoints.Channels}/{channelId}/{Endpoints.Threads}";

        RestForumPostCreatePayload pld = new()
        {
            Name = name,
            ArchiveAfter = autoArchiveDuration,
            RateLimitPerUser = rateLimitPerUser,
            Message = new RestChannelMessageCreatePayload
            {
                Content = message.Content,
                HasContent = !string.IsNullOrWhiteSpace(message.Content),
                Embeds = message.Embeds,
                HasEmbed = message.Embeds.Count > 0,
                Mentions = new DiscordMentions(message.Mentions, message.Mentions.Any()),
                Components = message.Components,
                StickersIds = message.Stickers?.Select(s => s.Id) ?? Array.Empty<ulong>(),
            },
            AppliedTags = appliedTags
        };

        JObject ret;
        RestResponse res;
        if (message.Files.Count is 0)
        {
            RestRequest req = new()
            {
                Route = route,
                Url = url,
                Method = HttpMethod.Post,
                Payload = DiscordJson.SerializeObject(pld)
            };

            res = await this.rest.ExecuteRequestAsync(req);
            ret = JObject.Parse(res.Response!);
        }
        else
        {
            Dictionary<string, string> values = new()
            {
                ["payload_json"] = DiscordJson.SerializeObject(pld)
            };

            MultipartRestRequest req = new()
            {
                Route = route,
                Url = url,
                Method = HttpMethod.Post,
                Values = values,
                Files = message.Files
            };

            res = await this.rest.ExecuteRequestAsync(req);
            ret = JObject.Parse(res.Response!);
        }

        JToken? msgToken = ret["message"];
        ret.Remove("message");

        DiscordMessage msg = PrepareMessage(msgToken!);
        // We know the return type; deserialize directly.
        DiscordThreadChannel chn = ret.ToDiscordObject<DiscordThreadChannel>();
        chn.Discord = this.discord!;

        return new DiscordForumPostStarter(chn, msg);
    }

    /// <summary>
    /// Internal method to create an auto-moderation rule in a guild.
    /// </summary>
    /// <param name="guildId">The id of the guild where the rule will be created.</param>
    /// <param name="name">The rule name.</param>
    /// <param name="eventType">The Discord event that will trigger the rule.</param>
    /// <param name="triggerType">The rule trigger.</param>
    /// <param name="triggerMetadata">The trigger metadata.</param>
    /// <param name="actions">The actions that will run when a rule is triggered.</param>
    /// <param name="enabled">Whenever the rule is enabled or not.</param>
    /// <param name="exemptRoles">The exempted roles that will not trigger the rule.</param>
    /// <param name="exemptChannels">The exempted channels that will not trigger the rule.</param>
    /// <param name="reason">The reason for audits logs.</param>
    /// <returns>The created rule.</returns>
    public async ValueTask<DiscordAutoModerationRule> CreateGuildAutoModerationRuleAsync
    (
        ulong guildId,
        string name,
        DiscordRuleEventType eventType,
        DiscordRuleTriggerType triggerType,
        DiscordRuleTriggerMetadata triggerMetadata,
        IReadOnlyList<DiscordAutoModerationAction> actions,
        Optional<bool> enabled = default,
        Optional<IReadOnlyList<DiscordRole>> exemptRoles = default,
        Optional<IReadOnlyList<DiscordChannel>> exemptChannels = default,
        string? reason = null
    )
    {
        string route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.AutoModeration}/{Endpoints.Rules}";
        string url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.AutoModeration}/{Endpoints.Rules}";

        Dictionary<string, string> headers = [];
        if (!string.IsNullOrWhiteSpace(reason))
        {
            headers[ReasonHeaderName] = reason;
        }

        string payload = DiscordJson.SerializeObject(new
        {
            guild_id = guildId,
            name,
            event_type = eventType,
            trigger_type = triggerType,
            trigger_metadata = triggerMetadata,
            actions,
            enabled,
            exempt_roles = exemptRoles.Value.Select(x => x.Id).ToArray(),
            exempt_channels = exemptChannels.Value.Select(x => x.Id).ToArray()
        });

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Post,
            Headers = headers,
            Payload = payload
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);
        DiscordAutoModerationRule rule = JsonConvert.DeserializeObject<DiscordAutoModerationRule>(res.Response!)!;

        return rule;
    }

    /// <summary>
    /// Internal method to get an auto-moderation rule in a guild.
    /// </summary>
    /// <param name="guildId">The guild id where the rule is in.</param>
    /// <param name="ruleId">The rule id.</param>
    /// <returns>The rule found.</returns>
    public async ValueTask<DiscordAutoModerationRule> GetGuildAutoModerationRuleAsync
    (
        ulong guildId,
        ulong ruleId
    )
    {
        string route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.AutoModeration}/{Endpoints.Rules}/:rule_id";
        string url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.AutoModeration}/{Endpoints.Rules}/{ruleId}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);
        DiscordAutoModerationRule rule = JsonConvert.DeserializeObject<DiscordAutoModerationRule>(res.Response!)!;

        return rule;
    }

    /// <summary>
    /// Internal method to get all auto-moderation rules in a guild.
    /// </summary>
    /// <param name="guildId">The guild id where rules are in.</param>
    /// <returns>The rules found.</returns>
    public async ValueTask<IReadOnlyList<DiscordAutoModerationRule>> GetGuildAutoModerationRulesAsync
    (
        ulong guildId
    )
    {
        string route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.AutoModeration}/{Endpoints.Rules}";
        string url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.AutoModeration}/{Endpoints.Rules}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);
        IReadOnlyList<DiscordAutoModerationRule> rules = JsonConvert.DeserializeObject<IReadOnlyList<DiscordAutoModerationRule>>(res.Response!)!;

        return rules;
    }

    /// <summary>
    /// Internal method to modify an auto-moderation rule in a guild.
    /// </summary>
    /// <param name="guildId">The id of the guild where the rule will be modified.</param>
    /// <param name="ruleId">The id of the rule that will be modified.</param>
    /// <param name="name">The rule name.</param>
    /// <param name="eventType">The Discord event that will trigger the rule.</param>
    /// <param name="triggerMetadata">The trigger metadata.</param>
    /// <param name="actions">The actions that will run when a rule is triggered.</param>
    /// <param name="enabled">Whenever the rule is enabled or not.</param>
    /// <param name="exemptRoles">The exempted roles that will not trigger the rule.</param>
    /// <param name="exemptChannels">The exempted channels that will not trigger the rule.</param>
    /// <param name="reason">The reason for audits logs.</param>
    /// <returns>The modified rule.</returns>
    public async ValueTask<DiscordAutoModerationRule> ModifyGuildAutoModerationRuleAsync
    (
        ulong guildId,
        ulong ruleId,
        Optional<string> name,
        Optional<DiscordRuleEventType> eventType,
        Optional<DiscordRuleTriggerMetadata> triggerMetadata,
        Optional<IReadOnlyList<DiscordAutoModerationAction>> actions,
        Optional<bool> enabled,
        Optional<IReadOnlyList<DiscordRole>> exemptRoles,
        Optional<IReadOnlyList<DiscordChannel>> exemptChannels,
        string? reason = null
    )
    {
        string route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.AutoModeration}/{Endpoints.Rules}/:rule_id";
        string url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.AutoModeration}/{Endpoints.Rules}/{ruleId}";

        Dictionary<string, string> headers = [];
        if (!string.IsNullOrWhiteSpace(reason))
        {
            headers[ReasonHeaderName] = reason;
        }

        string payload = DiscordJson.SerializeObject(new
        {
            name,
            event_type = eventType,
            trigger_metadata = triggerMetadata,
            actions,
            enabled,
            exempt_roles = exemptRoles.Value.Select(x => x.Id).ToArray(),
            exempt_channels = exemptChannels.Value.Select(x => x.Id).ToArray()
        });

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Patch,
            Headers = headers,
            Payload = payload
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);
        DiscordAutoModerationRule rule = JsonConvert.DeserializeObject<DiscordAutoModerationRule>(res.Response!)!;

        return rule;
    }

    /// <summary>
    /// Internal method to delete an auto-moderation rule in a guild.
    /// </summary>
    /// <param name="guildId">The id of the guild where the rule is in.</param>
    /// <param name="ruleId">The rule id that will be deleted.</param>
    /// <param name="reason">The reason for audits logs.</param>
    public async ValueTask DeleteGuildAutoModerationRuleAsync
    (
        ulong guildId,
        ulong ruleId,
        string? reason = null
    )
    {
        string route = $"{Endpoints.Guilds}/{guildId}/{Endpoints.AutoModeration}/{Endpoints.Rules}/:rule_id";
        string url = $"{Endpoints.Guilds}/{guildId}/{Endpoints.AutoModeration}/{Endpoints.Rules}/{ruleId}";

        Dictionary<string, string> headers = [];
        if (!string.IsNullOrWhiteSpace(reason))
        {
            headers[ReasonHeaderName] = reason;
        }

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Delete,
            Headers = headers
        };

        await this.rest.ExecuteRequestAsync(request);
    }

    /// <summary>
    /// Internal method to get all SKUs belonging to a specific application
    /// </summary>
    /// <param name="applicationId">Id of the application of which SKUs should be returned</param>
    /// <returns>Returns a list of SKUs</returns>
    public async ValueTask<IReadOnlyList<DiscordStockKeepingUnit>> ListStockKeepingUnitsAsync(ulong applicationId)
    {
        string route = $"{Endpoints.Applications}/{applicationId}/{Endpoints.Skus}";
        string url = $"{Endpoints.Applications}/{applicationId}/{Endpoints.Skus}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);
        IReadOnlyList<DiscordStockKeepingUnit> stockKeepingUnits = JsonConvert.DeserializeObject<IReadOnlyList<DiscordStockKeepingUnit>>(res.Response!)!;

        return stockKeepingUnits;
    }
    
    /// <summary>
    /// Returns all entitlements for a given app.
    /// </summary>
    /// <param name="applicationId">Application ID to look up entitlements for</param>
    /// <param name="userId">User ID to look up entitlements for</param>
    /// <param name="skuIds">Optional list of SKU IDs to check entitlements for</param>
    /// <param name="before">Retrieve entitlements before this entitlement ID</param>
    /// <param name="after">Retrieve entitlements after this entitlement ID</param>
    /// <param name="guildId">Guild ID to look up entitlements for</param>
    /// <param name="excludeEnded">Whether or not ended entitlements should be omitted</param>
    /// <param name="limit">Number of entitlements to return, 1-100, default 100</param>
    /// <returns>Returns the list of entitlments. Sorted by id descending (depending on discord)</returns>
    public async ValueTask<IReadOnlyList<DiscordEntitlement>> ListEntitlementsAsync
    (
        ulong applicationId,
        ulong? userId = null,
        IEnumerable<ulong>? skuIds = null,
        ulong? before = null,
        ulong? after = null,
        ulong? guildId = null,
        bool? excludeEnded = null,
        int? limit = 100
    )
    {
        string route = $"{Endpoints.Applications}/{applicationId}/{Endpoints.Entitlements}";
        string url = $"{Endpoints.Applications}/{applicationId}/{Endpoints.Entitlements}";
        
        QueryUriBuilder builder = new(url);

        if (userId is not null)
        {
            builder.AddParameter("user_id", userId.ToString());
        }
        
        if (skuIds is not null)
        {
            builder.AddParameter("sku_ids", string.Join(",", skuIds.Select(x => x.ToString())));
        }

        if (before is not null)
        {
            builder.AddParameter("before", before.ToString());
        }

        if (after is not null)
        {
            builder.AddParameter("after", after.ToString());
        }

        if (limit is not null)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(limit.Value, 100);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit.Value);
            
            builder.AddParameter("limit", limit.ToString());
        }

        if (guildId is not null)
        {
            builder.AddParameter("guild_id", guildId.ToString());
        }

        if (excludeEnded is not null)
        {
            builder.AddParameter("exclude_ended", excludeEnded.ToString());
        }

        RestRequest request = new()
        {
            Route = route,
            Url = builder.ToString(),
            Method = HttpMethod.Get
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);
        IReadOnlyList<DiscordEntitlement> entitlements = JsonConvert.DeserializeObject<IReadOnlyList<DiscordEntitlement>>(res.Response!)!;

        return entitlements;
    }
    
    /// <summary>
    /// For One-Time Purchase consumable SKUs, marks a given entitlement for the user as consumed. 
    /// </summary>
    /// <param name="applicationId">The id of the application the entitlement belongs to</param>
    /// <param name="entitlementId">The id of the entitlement which will be marked as consumed</param>
    public async ValueTask ConsumeEntitlementAsync(ulong applicationId, ulong entitlementId)
    {
        string route = $"{Endpoints.Applications}/{applicationId}/{Endpoints.Entitlements}/:entitlementId/{Endpoints.Consume}";
        string url = $"{Endpoints.Applications}/{applicationId}/{Endpoints.Entitlements}/{entitlementId}/{Endpoints.Consume}";

        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Post
        };

        await this.rest.ExecuteRequestAsync(request);
    }
    
    /// <summary>
    /// Create a test entitlement which can be granted to a user or a guild
    /// </summary>
    /// <param name="applicationId">The id of the application the SKU belongs to</param>
    /// <param name="skuId">The id of the SKU the entitlement belongs to</param>
    /// <param name="ownerId">The id of the entity which should recieve the entitlement</param>
    /// <param name="ownerType">The type of the entity which should recieve the entitlement</param>
    /// <returns>Returns a partial entitlment</returns>
    public async ValueTask<DiscordEntitlement> CreateTestEntitlementAsync
    (
        ulong applicationId,
        ulong skuId,
        ulong ownerId,
        DiscordTestEntitlementOwnerType ownerType
    )
    {
        string route = $"{Endpoints.Applications}/{applicationId}/{Endpoints.Entitlements}";
        string url = $"{Endpoints.Applications}/{applicationId}/{Endpoints.Entitlements}";

        string payload = DiscordJson.SerializeObject(
            new RestCreateTestEntitlementPayload() { SkuId = skuId, OwnerId = ownerId, OwnerType = ownerType });
        
        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Post,
            Payload = payload
        };

        RestResponse res = await this.rest.ExecuteRequestAsync(request);
        DiscordEntitlement entitlement = JsonConvert.DeserializeObject<DiscordEntitlement>(res.Response!)!;

        return entitlement;
    }
    
    /// <summary>
    /// Deletes a test entitlement
    /// </summary>
    /// <param name="applicationId">The id of the application the entitlement belongs to</param>
    /// <param name="entitlementId">The id of the test entitlement which should be removed</param>
    public async ValueTask DeleteTestEntitlementAsync
    (
        ulong applicationId,
        ulong entitlementId
    )
    {
        string route = $"{Endpoints.Applications}/{applicationId}/{Endpoints.Entitlements}/:entitlementId";
        string url = $"{Endpoints.Applications}/{applicationId}/{Endpoints.Entitlements}/{entitlementId}";
        
        RestRequest request = new()
        {
            Route = route,
            Url = url,
            Method = HttpMethod.Delete
        };

        await this.rest.ExecuteRequestAsync(request);
    }
}
