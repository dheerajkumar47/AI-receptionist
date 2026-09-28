using AiReceptionist.Core.Domain;
using AiReceptionist.Core.Scheduling;

namespace AiReceptionist.Core.Abstractions;

/// <summary>One previous line of the conversation passed to the intent engine.</summary>
public sealed record ChatTurn(bool FromCustomer, string Text);

/// <summary>Everything the intent engine needs to understand a message and draft a reply.</summary>
public sealed record IntentContext(
    BotSettings Settings,
    IReadOnlyList<IntentDefinition> Intents,
    IReadOnlyList<ChatTurn> History,
    string UserMessage,
    string Channel,
    string? CustomerName,
    string? CustomerEmail,
    IReadOnlyList<TimeSlot> OpenSlots,
    DateTime? PendingSlotUtc,
    DateTime NowUtc,
    TimeZoneInfo TimeZone);

/// <summary>The engine's interpretation of a message.</summary>
/// <param name="Intent">Name of one of the configured intents.</param>
/// <param name="Confidence">0..1.</param>
/// <param name="Reply">Natural-language reply suitable for chat and speech.</param>
/// <param name="ProposedSlotUtc">A slot the reply offers to the customer.</param>
/// <param name="ConfirmsPendingSlot">Customer agreed to the pending (or proposed) slot.</param>
/// <param name="CustomerName">Name the customer gave, if any.</param>
/// <param name="CustomerEmail">Email the customer gave, if any.</param>
public sealed record IntentResult(
    string Intent,
    double Confidence,
    string Reply,
    DateTime? ProposedSlotUtc = null,
    bool ConfirmsPendingSlot = false,
    string? CustomerName = null,
    string? CustomerEmail = null);

/// <summary>Natural-language understanding + reply generation (Azure OpenAI, OpenAI, or offline keywords).</summary>
public interface IIntentEngine
{
    string Name { get; }
    Task<IntentResult> AnalyzeAsync(IntentContext context, CancellationToken ct);
}

public sealed record AudioClip(byte[] Data, string ContentType, string Extension);

/// <summary>Text-to-speech.</summary>
public interface IVoiceSynthesizer
{
    bool IsConfigured { get; }
    Task<AudioClip> SynthesizeAsync(string text, string voiceName, AudioFormat format, CancellationToken ct);
}

/// <summary>Speech-to-text for inbound voice notes.</summary>
public interface IAudioTranscriber
{
    bool IsConfigured { get; }
    Task<string?> TranscribeAsync(byte[] audio, string contentType, CancellationToken ct);
}

/// <summary>Stores audio so platforms can fetch it by public URL.</summary>
public interface IMediaStore
{
    Task<string> SaveAsync(AudioClip clip, CancellationToken ct);
}

public sealed record BookingRequest(
    DateTime StartUtc,
    DateTime EndUtc,
    string Subject,
    string Body,
    string? CustomerName,
    string? CustomerEmail,
    string TimeZoneId,
    string IdempotencyKey,
    string? BusinessName = null);

public sealed record CalendarEventResult(string EventId, string? WebLink);

/// <summary>The business calendar (Microsoft 365 via Graph).</summary>
public interface ICalendarProvider
{
    bool IsConfigured { get; }
    Task<IReadOnlyList<TimeSlot>> GetBusyAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct);
    Task<CalendarEventResult> CreateEventAsync(BookingRequest request, CancellationToken ct);
    Task CancelEventAsync(string eventId, CancellationToken ct);
}

public sealed record EmailAttachment(string FileName, string ContentType, byte[] Data);

public sealed record EmailMessage(IReadOnlyList<string> To, string Subject, string Body, IReadOnlyList<EmailAttachment>? Attachments = null);

/// <summary>Email used as the booking fallback when the calendar is unavailable.</summary>
public interface IEmailSender
{
    bool IsConfigured { get; }

    /// <summary>Address of the business owner that receives fallback confirmations.</summary>
    string? OwnerAddress { get; }

    Task SendAsync(EmailMessage message, CancellationToken ct);
}

/// <summary>Kinds of change pushed to the live dashboard.</summary>
public enum ActivityKind
{
    MessageReceived,
    MessageSent,
    ConversationUpdated,
    AppointmentChanged,
}

public sealed record ActivityEvent(ActivityKind Kind, int? ConversationId);

/// <summary>In-process pub/sub so dashboard pages refresh live.</summary>
public interface IActivityNotifier
{
    event Action<ActivityEvent>? Changed;
    void Publish(ActivityEvent evt);
}

/// <summary>Queue between webhook/polling receivers and the processing worker, so webhooks return 200 immediately.</summary>
public interface IInboundQueue
{
    ValueTask EnqueueAsync(InboundMessage message, CancellationToken ct = default);
    IAsyncEnumerable<InboundMessage> ReadAllAsync(CancellationToken ct);
}
