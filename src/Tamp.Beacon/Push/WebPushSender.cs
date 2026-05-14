using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WebPush;

namespace Tamp.Beacon.Push;

/// <summary>
/// Abstraction over the Web Push transport so the alert worker can be
/// tested without standing up a real push service. Production wiring
/// resolves to <see cref="WebPushSender"/>.
/// </summary>
public interface IWebPushSender
{
    Task<bool> SendAsync(Models.PushSubscription sub, object payload, CancellationToken ct = default);
}

/// <summary>
/// Wraps <c>WebPush.WebPushClient</c> with logging and per-subscription
/// failure isolation. A single subscription that's gone stale (HTTP 404
/// from the push service) does not abort the broadcast.
/// </summary>
public sealed class WebPushSender(VapidKeyStore vapid, ILogger<WebPushSender> logger) : IWebPushSender
{
    private readonly WebPushClient _client = new();

    public async Task<bool> SendAsync(Models.PushSubscription sub, object payload, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sub);
        ArgumentNullException.ThrowIfNull(payload);

        var json = JsonSerializer.Serialize(payload);
        var rfc = new WebPush.PushSubscription(sub.Endpoint, sub.P256dh, sub.Auth);

        try
        {
            await _client.SendNotificationAsync(rfc, json, vapid.Details).ConfigureAwait(false);
            return true;
        }
        catch (WebPushException ex) when ((int)ex.StatusCode is 404 or 410)
        {
            logger.LogWarning("Push subscription {Endpoint} returned {Status}; consider unsubscribing.",
                sub.Endpoint, (int)ex.StatusCode);
            return false;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Push transport error for {Endpoint}", sub.Endpoint);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error sending push to {Endpoint}", sub.Endpoint);
            return false;
        }
    }
}
