namespace AiReceptionist.Core.Abstractions;

/// <summary>A normalised direct message received from any channel.</summary>
/// <param name="Channel">Channel id (see <see cref="Domain.Channels"/>).</param>
/// <param name="SenderId">Platform id of the customer; replies are sent back to it.</param>
/// <param name="SenderName">Display name if the platform supplied one.</param>
/// <param name="Text">Message text; null for a voice note that still needs transcribing.</param>
/// <param name="ExternalMessageId">Platform message id, used for de-duplication.</param>
/// <param name="TimestampUtc">When the platform received the message.</param>
/// <param name="IsVoice">True for voice notes / audio attachments.</param>
/// <param name="AudioReference">Channel-specific handle (media id or URL) used to download the audio.</param>
public sealed record InboundMessage(
    string Channel,
    string SenderId,
    string? SenderName,
    string? Text,
    string ExternalMessageId,
    DateTime TimestampUtc,
    bool IsVoice = false,
    string? AudioReference = null);

/// <summary>A reply to deliver. Either or both of <see cref="Text"/> and <see cref="AudioUrl"/> are set.</summary>
/// <param name="Hint">Set for automated messages (follow-ups, reminders) so the channel can respect platform rules.</param>
public sealed record OutboundMessage(string Channel, string RecipientId, string? Text, string? AudioUrl = null, DeliveryHint? Hint = null);

/// <summary>
/// Context for messages the business starts itself. Meta platforms only allow free-form messages within 24 hours of the
/// customer's last message; outside it Messenger accepts the CONFIRMED_EVENT_UPDATE tag for appointment reminders and
/// WhatsApp needs a pre-approved template. Instagram has no equivalent, so the channel reports a failure.
/// </summary>
/// <param name="OutsideReplyWindow">The customer's last message is older than the channel's <see cref="ChannelCapabilities.ReplyWindow"/>.</param>
/// <param name="IsAppointmentReminder">The message is a reminder about a booked appointment.</param>
/// <param name="TemplateName">WhatsApp template to use outside the window (with <paramref name="TemplateParameters"/> as body variables).</param>
public sealed record DeliveryHint(
    bool OutsideReplyWindow,
    bool IsAppointmentReminder = false,
    string? TemplateName = null,
    string TemplateLanguage = "en",
    IReadOnlyList<string>? TemplateParameters = null);

public sealed record SendResult(bool Success, string? ExternalId = null, string? Error = null)
{
    public static SendResult Ok(string? id = null) => new(true, id);
    public static SendResult Fail(string error) => new(false, null, error);
}

public enum AudioFormat
{
    Mp3,
    OggOpus,
}

/// <param name="SupportsAudio">Channel can deliver an audio attachment. If false, voice replies degrade to text plus a link.</param>
/// <param name="PreferredAudioFormat">Format synthesized for this channel (WhatsApp shows OGG/Opus as a native voice note).</param>
/// <param name="MaxTextLength">Platform limit; longer replies are truncated.</param>
/// <param name="ReplyWindow">How long after the customer's last message free-form messages are allowed (null = no limit).</param>
public sealed record ChannelCapabilities(bool SupportsAudio, AudioFormat PreferredAudioFormat, int MaxTextLength, TimeSpan? ReplyWindow = null);

/// <summary>
/// Outbound side of a social channel. Every channel implements this; to receive messages it also
/// implements <see cref="IWebhookChannel"/> (push) and/or <see cref="IPollingChannel"/> (pull).
/// Register the implementation as a singleton <see cref="IChannelConnector"/> and it is picked up
/// automatically by the webhook endpoint, the polling worker and the dashboard.
/// </summary>
public interface IChannelConnector
{
    /// <summary>Stable id stored in the database and used in the webhook URL (/webhooks/{ChannelId}).</summary>
    string ChannelId { get; }

    string DisplayName { get; }

    /// <summary>False when required credentials are missing; the channel is then skipped.</summary>
    bool IsConfigured { get; }

    ChannelCapabilities Capabilities { get; }

    Task<SendResult> SendAsync(OutboundMessage message, CancellationToken ct);
}

/// <summary>A channel whose platform pushes messages to an HTTPS webhook.</summary>
public interface IWebhookChannel : IChannelConnector
{
    /// <summary>Handles the platform's subscription handshake (GET). Returns the body to echo back, or null to reject.</summary>
    string? VerifySubscription(IReadOnlyDictionary<string, string?> query);

    /// <summary>Checks the request signature against the raw body. Must use a constant-time comparison.</summary>
    bool ValidateSignature(Func<string, string?> getHeader, byte[] body);

    /// <summary>Extracts customer messages from a webhook payload, ignoring echoes, receipts and status updates.</summary>
    IReadOnlyList<InboundMessage> ParsePayload(string json);
}

public sealed record PollResult(IReadOnlyList<InboundMessage> Messages, string? NextCursor);

/// <summary>A channel whose inbox must be polled.</summary>
public interface IPollingChannel : IChannelConnector
{
    TimeSpan PollInterval { get; }

    /// <summary>Fetch messages newer than <paramref name="cursor"/>. A null cursor means first run.</summary>
    Task<PollResult> PollAsync(string? cursor, CancellationToken ct);
}

/// <summary>Implemented by channels able to download inbound voice notes so they can be transcribed.</summary>
public interface IAudioDownloadChannel
{
    Task<(byte[] Data, string ContentType)?> DownloadAudioAsync(string audioReference, CancellationToken ct);
}
