using System;
using System.Threading.Tasks;

namespace DSharpPlusPlus.Net.InboundWebhooks.Transport;

public interface IWebhookTransportService
{
    public Task HandleWebhookEventAsync(ArraySegment<byte> payload);
}
