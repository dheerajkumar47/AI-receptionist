using AiReceptionist.Core.Scheduling;

namespace AiReceptionist.Core.Abstractions;

// "Not configured" implementations used when the corresponding Azure / Microsoft 365 credentials are absent.
// The orchestrator checks IsConfigured and degrades gracefully (text instead of voice, email instead of calendar...).

public sealed class NullCalendarProvider : ICalendarProvider
{
    public bool IsConfigured => false;
    public Task<IReadOnlyList<TimeSlot>> GetBusyAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct) => Task.FromResult<IReadOnlyList<TimeSlot>>(Array.Empty<TimeSlot>());
    public Task<CalendarEventResult> CreateEventAsync(BookingRequest request, CancellationToken ct) => throw new InvalidOperationException("Calendar not configured.");
    public Task CancelEventAsync(string eventId, CancellationToken ct) => throw new InvalidOperationException("Calendar not configured.");
}

public sealed class NullEmailSender : IEmailSender
{
    public bool IsConfigured => false;
    public string? OwnerAddress => null;
    public Task SendAsync(EmailMessage message, CancellationToken ct) => throw new InvalidOperationException("Email not configured.");
}

public sealed class NullVoiceSynthesizer : IVoiceSynthesizer
{
    public bool IsConfigured => false;
    public Task<AudioClip> SynthesizeAsync(string text, string voiceName, AudioFormat format, CancellationToken ct) => throw new InvalidOperationException("Speech not configured.");
}

public sealed class NullAudioTranscriber : IAudioTranscriber
{
    public bool IsConfigured => false;
    public Task<string?> TranscribeAsync(byte[] audio, string contentType, CancellationToken ct) => Task.FromResult<string?>(null);
}
