using AiReceptionist.Core.Abstractions;

namespace AiReceptionist.Web.Endpoints;

/// <summary>
/// Public webhook endpoints, one per push-based channel: <c>/webhooks/{channelId}</c>
/// (facebook, instagram, whatsapp, or any custom <see cref="IWebhookChannel"/>).
/// </summary>
public static class WebhookEndpoints
{
    public static void MapWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/webhooks").AllowAnonymous();

        // Subscription handshake (Meta: hub.mode / hub.verify_token / hub.challenge).
        group.MapGet("/{channel}", (string channel, HttpRequest request, IEnumerable<IChannelConnector> channels) =>
        {
            if (Find(channels, channel) is not { } webhook) return Results.NotFound();
            var query = request.Query.ToDictionary(q => q.Key, q => (string?)q.Value.ToString());
            var challenge = webhook.VerifySubscription(query);
            return challenge is null ? Results.StatusCode(StatusCodes.Status403Forbidden) : Results.Text(challenge, "text/plain");
        });

        // Event delivery. Verify the signature on the raw bytes, queue the messages, and return 200 immediately:
        // platforms retry (and eventually disable the webhook) if the response is slow.
        group.MapPost("/{channel}", async (string channel, HttpRequest request, IEnumerable<IChannelConnector> channels,
            IInboundQueue queue, ILoggerFactory loggers, CancellationToken ct) =>
        {
            var log = loggers.CreateLogger("Webhooks");
            if (Find(channels, channel) is not { } webhook) return Results.NotFound();

            using var buffer = new MemoryStream();
            await request.Body.CopyToAsync(buffer, ct);
            var body = buffer.ToArray();

            if (!webhook.ValidateSignature(name => request.Headers.TryGetValue(name, out var v) ? v.ToString() : null, body))
            {
                log.LogWarning("Rejected {Channel} webhook with an invalid signature.", channel);
                return Results.Unauthorized();
            }

            try
            {
                var messages = webhook.ParsePayload(System.Text.Encoding.UTF8.GetString(body));
                foreach (var m in messages) await queue.EnqueueAsync(m, ct);
                log.LogInformation("{Channel} webhook: {Count} message(s) queued.", channel, messages.Count);
                if (messages.Count == 0)
                {
                    // Delivery/read receipts and other events carry no customer message; log a short excerpt so
                    // unexpected payload shapes can be diagnosed.
                    var text = System.Text.Encoding.UTF8.GetString(body);
                    log.LogInformation("{Channel} webhook payload without messages: {Payload}", channel,
                        text.Length > 1500 ? text[..1500] + "…" : text);
                }
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
            {
                // Acknowledge anyway so the platform does not retry a payload we can never parse.
                log.LogError(ex, "Could not parse {Channel} webhook payload.", channel);
            }
            return Results.Ok();
        });
    }

    private static IWebhookChannel? Find(IEnumerable<IChannelConnector> channels, string id) =>
        channels.OfType<IWebhookChannel>().FirstOrDefault(c => c.IsConfigured && string.Equals(c.ChannelId, id, StringComparison.OrdinalIgnoreCase));
}
