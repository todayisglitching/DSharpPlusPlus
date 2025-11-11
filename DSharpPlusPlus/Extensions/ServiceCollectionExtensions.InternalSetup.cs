using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;

using DSharpPlusPlus.Clients;
using DSharpPlusPlus.Net;
using DSharpPlusPlus.Net.Abstractions;
using DSharpPlusPlus.Net.Gateway;
using DSharpPlusPlus.Net.InboundWebhooks;
using DSharpPlusPlus.Net.InboundWebhooks.Transport;
using DSharpPlusPlus.Net.Gateway.Compression;
using DSharpPlusPlus.Net.Gateway.Compression.Zlib;
using DSharpPlusPlus.Net.Gateway.Compression.Zstd;

using Microsoft.Extensions.DependencyInjection;

namespace DSharpPlusPlus.Extensions;

public static partial class ServiceCollectionExtensions
{
    internal static IServiceCollection AddDSharpPlusPlusDefaultsSingleShard
    (
        this IServiceCollection serviceCollection,
        DiscordIntents intents
    )
    {
        serviceCollection.AddDSharpPlusPlusServices(intents)
            .AddSingleton<IShardOrchestrator, SingleShardOrchestrator>();

        return serviceCollection;
    }

    internal static IServiceCollection AddDSharpPlusPlusDefaultsMultiShard
    (
        this IServiceCollection serviceCollection,
        DiscordIntents intents
    )
    {
        serviceCollection.AddDSharpPlusPlusServices(intents)
            .AddSingleton<IShardOrchestrator, MultiShardOrchestrator>();

        return serviceCollection;
    }

    internal static IServiceCollection AddDSharpPlusPlusServices
    (
        this IServiceCollection serviceCollection,
        DiscordIntents intents
    )
    {
        // peripheral setup
        serviceCollection.AddMemoryCache()
            .AddSingleton<IMessageCacheProvider, MessageCache>()
            .AddSingleton<IClientErrorHandler, DefaultClientErrorHandler>()
            .AddSingleton<IGatewayController, DefaultGatewayController>();

        // rest setup
        serviceCollection.AddHttpClient("DSharpPlusPlus.Rest.HttpClient")
            .UseSocketsHttpHandler((handler, _) => handler.PooledConnectionLifetime = TimeSpan.FromMinutes(30))
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan)
            .ConfigureHttpClient(client =>
            {
                client.BaseAddress = new Uri(Utilities.GetApiBaseUri());
                client.Timeout = Timeout.InfiniteTimeSpan;
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", Utilities.GetUserAgent());
                client.BaseAddress = new(Endpoints.BaseUri);
            });

        serviceCollection.AddTransient<DiscordRestApiClient>()
            .AddSingleton<DiscordRestApiClientFactory>()
            .AddTransient<RestClient>();

        // gateway setup
        serviceCollection.Configure<GatewayClientOptions>(c => c.Intents = intents)
            .AddKeyedSingleton("DSharpPlusPlus.Gateway.EventChannel", Channel.CreateUnbounded<GatewayPayload>(new UnboundedChannelOptions { SingleReader = true }))
            .AddTransient<ITransportService, TransportService>()
            .AddTransient<IGatewayClient, GatewayClient>()
            .RegisterBestDecompressor()
            .AddSingleton<IEventDispatcher, DefaultEventDispatcher>()
            .AddSingleton<DiscordClient>();

        // http events/interactions, if we're using those - doesn't actually cause any overhead if we aren't
        serviceCollection.AddKeyedSingleton("DSharpPlusPlus.Webhooks.EventChannel", Channel.CreateUnbounded<DiscordWebhookEvent>
            (
                new UnboundedChannelOptions
                {
                    SingleReader = true
                }
            ))
            .AddKeyedSingleton("DSharpPlusPlus.Interactions.EventChannel", Channel.CreateUnbounded<DiscordHttpInteractionPayload>
            (
                new UnboundedChannelOptions
                {
                    SingleReader = true
                }
            ))
            .AddSingleton<IInteractionTransportService, InteractionTransportService>()
            .AddSingleton<IWebhookTransportService, WebhookEventTransportService>();

        return serviceCollection;
    }

    private static IServiceCollection RegisterBestDecompressor(this IServiceCollection services)
    {
        if (NativeLibrary.TryLoad("libzstd", Assembly.GetEntryAssembly(), default, out _))
        {
            services.AddTransient<IPayloadDecompressor, ZstdDecompressor>();
        }
        else
        {
            services.AddTransient<IPayloadDecompressor, ZlibStreamDecompressor>();
        }

        return services;
    }
}
