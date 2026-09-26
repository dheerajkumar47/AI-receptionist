using System.Net.Mail;
using System.Text;
using AiReceptionist.Core.Abstractions;
using AiReceptionist.Core.Data;
using AiReceptionist.Core.Domain;
using AiReceptionist.Core.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AiReceptionist.Core.Conversations;

/// <summary>
/// The receptionist's brain. For every inbound message it:
/// <list type="number">
/// <item>de-duplicates and stores it (transcribing voice notes when possible);</item>
/// <item>stops if a human has taken over or auto-reply is off;</item>
/// <item>applies admin rules, then asks the <see cref="IIntentEngine"/> for intent + reply;</item>
/// <item>runs the intent's action (offer slot, book, cancel, hand off) with deterministic validation;</item>
/// <item>delivers the reply as text and/or synthesized voice, or saves it as a draft in approval mode.</item>
/// </list>
/// It is also the API the dashboard uses for manual overrides.
/// </summary>
public sealed class ConversationService
{
    private readonly IDbContextFactory<ReceptionistDbContext> _dbFactory;
    private readonly IIntentEngine _engine;
    private readonly Dictionary<string, IChannelConnector> _channels;
    private readonly IVoiceSynthesizer _voice;
    private readonly IAudioTranscriber _transcriber;
    private readonly IMediaStore _media;
    private readonly AvailabilityService _availability;
    private readonly AppointmentService _appointments;
    private readonly IActivityNotifier _notifier;
    private readonly TimeProvider _time;
    private readonly ILogger<ConversationService> _log;

    public ConversationService(
        IDbContextFactory<ReceptionistDbContext> dbFactory,
        IIntentEngine engine,
        IEnumerable<IChannelConnector> channels,
        IVoiceSynthesizer voice,
        IAudioTranscriber transcriber,
        IMediaStore media,
        AvailabilityService availability,
        AppointmentService appointments,
        IActivityNotifier notifier,
        TimeProvider time,
        ILogger<ConversationService> log)
    {
        _dbFactory = dbFactory;
        _engine = engine;
        _channels = channels.ToDictionary(c => c.ChannelId, StringComparer.OrdinalIgnoreCase);
        _voice = voice;
        _transcriber = transcriber;
        _media = media;
        _availability = availability;
        _appointments = appointments;
        _notifier = notifier;
        _time = time;
        _log = log;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    // ------------------------------------------------------------------ inbound pipeline

    public async Task ProcessInboundAsync(InboundMessage inbound, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        if (await db.Messages.AnyAsync(m => m.Channel == inbound.Channel && m.ExternalId == inbound.ExternalMessageId, ct))
        {
            _log.LogDebug("Duplicate message {Channel}/{Id} ignored.", inbound.Channel, inbound.ExternalMessageId);
            return;
        }

        var settings = await LoadSettingsAsync(db, ct);
        var conversation = await GetOrCreateConversationAsync(db, inbound, ct);

        var text = inbound.Text;
        if (inbound.IsVoice && string.IsNullOrWhiteSpace(text))
            text = await TryTranscribeAsync(inbound, ct);

        var message = new Message
        {
            Conversation = conversation,
            Channel = inbound.Channel,
            Direction = MessageDirection.Inbound,
            Text = text,
            IsVoice = inbound.IsVoice,
            AudioUrl = inbound.IsVoice && Uri.IsWellFormedUriString(inbound.AudioReference, UriKind.Absolute) ? inbound.AudioReference : null,
            ExternalId = inbound.ExternalMessageId,
            Status = MessageStatus.Received,
            CreatedUtc = Now,
        };
        db.Messages.Add(message);
        conversation.LastActivityUtc = Now;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            await using var check = await _dbFactory.CreateDbContextAsync(ct);
            if (await check.Messages.AnyAsync(m => m.Channel == inbound.Channel && m.ExternalId == inbound.ExternalMessageId, ct))
                return; // lost a race with a concurrent duplicate delivery
            throw;
        }
        _notifier.Publish(new ActivityEvent(ActivityKind.MessageReceived, conversation.Id));

        if (conversation.Mode == ConversationMode.HumanTakeover || !settings.AutoReplyEnabled)
        {
            conversation.NeedsAttention = true;
            await db.SaveChangesAsync(ct);
            _notifier.Publish(new ActivityEvent(ActivityKind.ConversationUpdated, conversation.Id));
            return;
        }

        ReplyDecision? decision;
        if (string.IsNullOrWhiteSpace(text))
        {
            // Unsupported attachment or a voice note we could not transcribe.
            decision = new ReplyDecision(settings.HandoffMessage, settings.DefaultReplyMode, "Unreadable", null, NeedsAttention: true);
        }
        else
        {
            decision = await DecideAsync(db, settings, conversation, message, text, inbound.IsVoice, ct);
        }

        if (decision is null)
        {
            message.Status = MessageStatus.Ignored;
            await db.SaveChangesAsync(ct);
            _notifier.Publish(new ActivityEvent(ActivityKind.ConversationUpdated, conversation.Id));
            return;
        }

        message.Intent = decision.Intent;
        message.Confidence = decision.Confidence;
        conversation.LastIntent = decision.Intent;
        if (decision.NeedsAttention) conversation.NeedsAttention = true;
        if (decision.Handoff) conversation.Mode = ConversationMode.HumanTakeover;

        if (settings.RequireApproval)
        {
            db.Messages.Add(new Message
            {
                Conversation = conversation,
                Channel = conversation.Channel,
                Direction = MessageDirection.Outbound,
                Text = decision.Text,
                ReplyMode = decision.Mode,
                Intent = decision.Intent,
                Confidence = decision.Confidence,
                Status = MessageStatus.Draft,
                CreatedUtc = Now,
            });
            conversation.NeedsAttention = true;
            await db.SaveChangesAsync(ct);
            _notifier.Publish(new ActivityEvent(ActivityKind.ConversationUpdated, conversation.Id));
            return;
        }

        await db.SaveChangesAsync(ct);
        await DeliverAsync(db, settings, conversation, decision.Text, decision.Mode, decision.Intent, decision.Confidence,
            isManual: false, existing: null, ct);
    }

    private sealed record ReplyDecision(
        string Text,
        ReplyMode Mode,
        string? Intent,
        double? Confidence,
        bool NeedsAttention = false,
        bool Handoff = false);

    private async Task<ReplyDecision?> DecideAsync(ReceptionistDbContext db, BotSettings settings, Conversation conversation,
        Message current, string text, bool inboundWasVoice, CancellationToken ct)
    {
        var rules = await db.Rules.AsNoTracking().Where(r => r.Enabled).ToListAsync(ct);
        var intents = await db.Intents.AsNoTracking().Where(i => i.Enabled).OrderBy(i => i.SortOrder).ToListAsync(ct);
        var contact = conversation.Contact;

        string? forcedIntent = null;
        var rule = RuleEvaluator.Match(rules, text, conversation.Channel);
        if (rule is not null)
        {
            _log.LogInformation("Rule '{Rule}' matched conversation {Conversation}.", rule.Name, conversation.Id);
            switch (rule.Action)
            {
                case RuleAction.Ignore:
                    return null;
                case RuleAction.FixedReply:
                    return new ReplyDecision(rule.Value ?? "", ModeFor(null, settings, inboundWasVoice), $"Rule: {rule.Name}", 1.0);
                case RuleAction.HumanHandoff:
                    return new ReplyDecision(settings.HandoffMessage, ModeFor(null, settings, inboundWasVoice), $"Rule: {rule.Name}", 1.0,
                        NeedsAttention: true, Handoff: true);
                case RuleAction.ForceIntent:
                    forcedIntent = rule.Value;
                    break;
            }
        }

        var availability = await _availability.GetAsync(settings, ct);
        var tz = availability.TimeZone;

        var history = await db.Messages.AsNoTracking()
            .Where(m => m.ConversationId == conversation.Id && m.Id != current.Id && m.Text != null &&
                        (m.Status == MessageStatus.Received || m.Status == MessageStatus.Sent))
            .OrderByDescending(m => m.CreatedUtc).ThenByDescending(m => m.Id)
            .Take(Math.Max(0, settings.HistoryMessages))
            .ToListAsync(ct);
        history.Reverse();

        var context = new IntentContext(
            settings,
            intents,
            history.Select(m => new ChatTurn(m.Direction == MessageDirection.Inbound, m.Text!)).ToList(),
            text,
            conversation.Channel,
            contact.DisplayName,
            contact.Email,
            availability.OpenSlots,
            conversation.PendingSlotStartUtc,
            availability.NowUtc,
            tz);

        IntentResult result;
        try
        {
            result = await _engine.AnalyzeAsync(context, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Intent engine {Engine} failed for conversation {Conversation}.", _engine.Name, conversation.Id);
            // Shown under the customer's message in the dashboard so the admin can see why the AI failed.
            current.Error = $"{_engine.Name}: {ex.Message}";
            return new ReplyDecision(settings.HandoffMessage, settings.DefaultReplyMode, "EngineError", null, NeedsAttention: true);
        }

        UpdateContact(contact, result);

        var intentName = forcedIntent ?? result.Intent;
        var intent = intents.FirstOrDefault(i => string.Equals(i.Name, intentName, StringComparison.OrdinalIgnoreCase));
        var mode = ModeFor(intent, settings, inboundWasVoice);

        if (forcedIntent is null && result.Confidence < settings.MinConfidence)
        {
            _log.LogInformation("Low confidence {Confidence:0.00} for '{Intent}'; sending handoff message.", result.Confidence, result.Intent);
            return new ReplyDecision(settings.HandoffMessage, mode, intentName, result.Confidence, NeedsAttention: true);
        }

        var values = TemplateValues(settings, contact, conversation.Channel, conversation.PendingSlotStartUtc, availability);
        var reply = intent is { AlwaysUseTemplate: true, TemplateReply: { Length: > 0 } tpl }
            ? TemplateRenderer.Render(tpl, values)
            : result.Reply;
        if (string.IsNullOrWhiteSpace(reply))
            reply = intent?.TemplateReply is { Length: > 0 } fallback ? TemplateRenderer.Render(fallback, values) : settings.HandoffMessage;

        switch (intent?.Action ?? IntentAction.Reply)
        {
            case IntentAction.ProposeAppointment:
                if (result.ProposedSlotUtc is { } proposed)
                {
                    if (availability.IsBookable(proposed))
                    {
                        conversation.PendingSlotStartUtc = proposed;
                    }
                    else
                    {
                        conversation.PendingSlotStartUtc = null;
                        reply = UnavailableReply(settings, values, availability, proposed);
                    }
                }
                return new ReplyDecision(reply, mode, intentName, result.Confidence);

            case IntentAction.ConfirmAppointment:
                return await ConfirmAsync(db, settings, conversation, result, availability, values, reply, mode, intentName, ct);

            case IntentAction.CancelAppointment:
                return await CancelUpcomingAsync(db, settings, conversation, values, mode, intentName, result.Confidence, ct);

            case IntentAction.HumanHandoff:
                return new ReplyDecision(intent?.TemplateReply is { Length: > 0 } h ? TemplateRenderer.Render(h, values) : reply,
                    mode, intentName, result.Confidence, NeedsAttention: true, Handoff: true);

            default:
                if (result.ProposedSlotUtc is { } p && availability.IsBookable(p)) conversation.PendingSlotStartUtc = p;
                return new ReplyDecision(reply, mode, intentName, result.Confidence);
        }
    }

    private async Task<ReplyDecision> ConfirmAsync(ReceptionistDbContext db, BotSettings settings, Conversation conversation,
        IntentResult result, Availability availability, Dictionary<string, string?> values, string llmReply,
        ReplyMode mode, string intentName, CancellationToken ct)
    {
        // Prefer the slot we offered; accept a different explicit slot only if it is bookable.
        DateTime? slot = conversation.PendingSlotStartUtc;
        if (result.ProposedSlotUtc is { } p && p != slot && availability.IsBookable(p)) slot = p;

        if (slot is null)
        {
            // "Yes" without anything to confirm: let the model's reply ask which time they want.
            return new ReplyDecision(llmReply, mode, intentName, result.Confidence);
        }

        if (!availability.IsBookable(slot.Value))
        {
            conversation.PendingSlotStartUtc = null;
            return new ReplyDecision(UnavailableReply(settings, values, availability, slot.Value), mode, intentName, result.Confidence);
        }

        var contact = conversation.Contact;
        var start = slot.Value;
        var end = start.AddMinutes(settings.AppointmentMinutes);
        values["slot"] = SlotFormatter.Friendly(start, availability.TimeZone);

        var subject = TemplateRenderer.Render(settings.AppointmentSubject, values);
        var request = new BookingRequest(start, end, subject, await BuildBookingBodyAsync(db, conversation, ct),
            contact.DisplayName, contact.Email, settings.TimeZoneId, $"conv{conversation.Id}-{start:yyyyMMddHHmm}");

        var outcome = await _appointments.BookAsync(request, ct);

        db.Appointments.Add(new Appointment
        {
            Conversation = conversation,
            StartUtc = start,
            EndUtc = end,
            Subject = subject,
            CustomerName = contact.DisplayName,
            CustomerEmail = contact.Email,
            Status = outcome.Status,
            Method = outcome.Method,
            ExternalEventId = outcome.EventId,
            WebLink = outcome.WebLink,
            Error = outcome.Error,
            CreatedUtc = Now,
        });
        conversation.PendingSlotStartUtc = null;
        _notifier.Publish(new ActivityEvent(ActivityKind.AppointmentChanged, conversation.Id));

        return outcome.Status switch
        {
            AppointmentStatus.Booked => new ReplyDecision(TemplateRenderer.Render(settings.BookedReply, values), mode, intentName, result.Confidence),
            AppointmentStatus.EmailFallback => new ReplyDecision(TemplateRenderer.Render(settings.EmailFallbackReply, values), mode, intentName, result.Confidence, NeedsAttention: true),
            _ => new ReplyDecision(TemplateRenderer.Render(settings.BookingFailedReply, values), mode, intentName, result.Confidence, NeedsAttention: true),
        };
    }

    private async Task<ReplyDecision> CancelUpcomingAsync(ReceptionistDbContext db, BotSettings settings, Conversation conversation,
        Dictionary<string, string?> values, ReplyMode mode, string intentName, double confidence, CancellationToken ct)
    {
        var now = Now;
        var appointment = await db.Appointments
            .Where(a => a.ConversationId == conversation.Id && a.StartUtc > now &&
                        (a.Status == AppointmentStatus.Booked || a.Status == AppointmentStatus.EmailFallback))
            .OrderBy(a => a.StartUtc)
            .FirstOrDefaultAsync(ct);

        conversation.PendingSlotStartUtc = null;
        if (appointment is null)
            return new ReplyDecision("I couldn't find an upcoming appointment for you. Would you like to book one?", mode, intentName, confidence);

        var tz = TimeZoneResolver.Resolve(settings.TimeZoneId);
        values["slot"] = SlotFormatter.Friendly(appointment.StartUtc, tz);
        var error = await _appointments.CancelAsync(appointment, settings.TimeZoneId, ct);
        if (error is not null)
        {
            appointment.Error = "Cancel failed: " + error;
            return new ReplyDecision(TemplateRenderer.Render("I've asked our team to cancel your appointment on {slot}. They'll confirm shortly.", values),
                mode, intentName, confidence, NeedsAttention: true);
        }

        appointment.Status = AppointmentStatus.Cancelled;
        _notifier.Publish(new ActivityEvent(ActivityKind.AppointmentChanged, conversation.Id));
        return new ReplyDecision(TemplateRenderer.Render("Done, your appointment on {slot} has been cancelled. Let me know if you'd like to rebook.", values),
            mode, intentName, confidence);
    }

    // ------------------------------------------------------------------ delivery

    /// <summary>Sends <paramref name="text"/> to the customer as text and/or voice and records the outbound message.</summary>
    private async Task<Message> DeliverAsync(ReceptionistDbContext db, BotSettings settings, Conversation conversation, string text,
        ReplyMode mode, string? intent, double? confidence, bool isManual, Message? existing, CancellationToken ct)
    {
        var message = existing ?? new Message
        {
            Conversation = conversation,
            Channel = conversation.Channel,
            Direction = MessageDirection.Outbound,
            CreatedUtc = Now,
        };
        if (existing is null) db.Messages.Add(message);

        message.Intent ??= intent;
        message.Confidence ??= confidence;
        message.IsManual |= isManual;

        if (!_channels.TryGetValue(conversation.Channel, out var channel))
        {
            message.Text = text;
            message.Status = MessageStatus.Failed;
            message.Error = $"No connector registered for channel '{conversation.Channel}'.";
            conversation.NeedsAttention = true;
            await db.SaveChangesAsync(ct);
            _notifier.Publish(new ActivityEvent(ActivityKind.MessageSent, conversation.Id));
            return message;
        }

        var caps = channel.Capabilities;
        if (text.Length > caps.MaxTextLength) text = text[..(caps.MaxTextLength - 1)] + "…";

        var errors = new List<string>();
        string? audioUrl = null;
        if (mode != ReplyMode.Text)
        {
            if (_voice.IsConfigured)
            {
                try
                {
                    var clip = await _voice.SynthesizeAsync(text, settings.VoiceName, caps.PreferredAudioFormat, ct);
                    audioUrl = await _media.SaveAsync(clip, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogError(ex, "Speech synthesis failed; sending text instead.");
                    errors.Add("Voice: " + ex.Message);
                }
            }
            else
            {
                errors.Add("Voice requested but speech service is not configured; sent as text.");
            }
        }

        var effectiveMode = audioUrl is null ? ReplyMode.Text : mode;
        var sendAudioNatively = audioUrl is not null && caps.SupportsAudio;
        var sendText = effectiveMode != ReplyMode.Voice || !caps.SupportsAudio;
        var textToSend = audioUrl is not null && !caps.SupportsAudio ? $"{text}\n\n🔊 {audioUrl}" : text;

        string? externalId = null;
        if (sendText)
        {
            var r = await SafeSendAsync(channel, new OutboundMessage(channel.ChannelId, conversation.Contact.ExternalUserId, textToSend, null), ct);
            externalId ??= r.ExternalId;
            if (!r.Success) errors.Add("Text: " + r.Error);
        }
        if (sendAudioNatively)
        {
            var r = await SafeSendAsync(channel, new OutboundMessage(channel.ChannelId, conversation.Contact.ExternalUserId, null, audioUrl), ct);
            externalId ??= r.ExternalId;
            if (!r.Success) errors.Add("Audio: " + r.Error);
        }

        var deliveryFailed = errors.Any(e => e.StartsWith("Text:") || e.StartsWith("Audio:"));
        message.Text = text;
        message.AudioUrl = audioUrl;
        message.ReplyMode = effectiveMode;
        message.ExternalId = externalId;
        message.Status = deliveryFailed ? MessageStatus.Failed : MessageStatus.Sent;
        message.Error = errors.Count > 0 ? string.Join(" | ", errors) : null;
        if (deliveryFailed) conversation.NeedsAttention = true;
        conversation.LastActivityUtc = Now;

        await db.SaveChangesAsync(ct);
        _notifier.Publish(new ActivityEvent(ActivityKind.MessageSent, conversation.Id));
        return message;
    }

    private async Task<SendResult> SafeSendAsync(IChannelConnector channel, OutboundMessage message, CancellationToken ct)
    {
        try
        {
            return await channel.SendAsync(message, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Sending via {Channel} failed.", channel.ChannelId);
            return SendResult.Fail(ex.Message);
        }
    }

    // ------------------------------------------------------------------ dashboard API

    /// <summary>Admin override: send a hand-written reply. Does not change the conversation mode.</summary>
    public async Task<Message> SendManualReplyAsync(int conversationId, string text, ReplyMode mode, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var conversation = await db.Conversations.Include(c => c.Contact).SingleAsync(c => c.Id == conversationId, ct);
        var settings = await LoadSettingsAsync(db, ct);
        conversation.NeedsAttention = false;
        return await DeliverAsync(db, settings, conversation, text, mode, "Manual", null, isManual: true, existing: null, ct);
    }

    /// <summary>Approves (optionally edits) a draft created in approval mode and sends it.</summary>
    public async Task<Message> ApproveDraftAsync(int messageId, string? editedText, ReplyMode? mode, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var draft = await db.Messages.Include(m => m.Conversation).ThenInclude(c => c.Contact)
            .SingleAsync(m => m.Id == messageId && m.Status == MessageStatus.Draft, ct);
        var settings = await LoadSettingsAsync(db, ct);
        var edited = editedText is not null && editedText != draft.Text;
        var hasOtherDrafts = await db.Messages.AnyAsync(m => m.ConversationId == draft.ConversationId && m.Id != messageId && m.Status == MessageStatus.Draft, ct);
        if (!hasOtherDrafts) draft.Conversation.NeedsAttention = false;
        return await DeliverAsync(db, settings, draft.Conversation, editedText ?? draft.Text ?? "", mode ?? draft.ReplyMode ?? ReplyMode.Text,
            draft.Intent, draft.Confidence, isManual: edited, existing: draft, ct);
    }

    public async Task DiscardDraftAsync(int messageId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var draft = await db.Messages.SingleAsync(m => m.Id == messageId && m.Status == MessageStatus.Draft, ct);
        db.Messages.Remove(draft);
        await db.SaveChangesAsync(ct);
        _notifier.Publish(new ActivityEvent(ActivityKind.ConversationUpdated, draft.ConversationId));
    }

    /// <summary>Pause (HumanTakeover) or resume (Bot) automatic replies for a conversation.</summary>
    public async Task SetModeAsync(int conversationId, ConversationMode mode, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var conversation = await db.Conversations.SingleAsync(c => c.Id == conversationId, ct);
        conversation.Mode = mode;
        if (mode == ConversationMode.Bot) conversation.NeedsAttention = false;
        await db.SaveChangesAsync(ct);
        _notifier.Publish(new ActivityEvent(ActivityKind.ConversationUpdated, conversationId));
    }

    public async Task<string?> CancelAppointmentAsync(int appointmentId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var appointment = await db.Appointments.SingleAsync(a => a.Id == appointmentId, ct);
        var settings = await LoadSettingsAsync(db, ct);
        var error = await _appointments.CancelAsync(appointment, settings.TimeZoneId, ct);
        if (error is null) appointment.Status = AppointmentStatus.Cancelled;
        else appointment.Error = "Cancel failed: " + error;
        await db.SaveChangesAsync(ct);
        _notifier.Publish(new ActivityEvent(ActivityKind.AppointmentChanged, appointment.ConversationId));
        return error;
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<BotSettings> LoadSettingsAsync(ReceptionistDbContext db, CancellationToken ct) =>
        await db.Settings.OrderBy(s => s.Id).FirstOrDefaultAsync(ct) ?? new BotSettings();

    private async Task<Conversation> GetOrCreateConversationAsync(ReceptionistDbContext db, InboundMessage inbound, CancellationToken ct)
    {
        var contact = await db.Contacts.SingleOrDefaultAsync(c => c.Channel == inbound.Channel && c.ExternalUserId == inbound.SenderId, ct);
        if (contact is null)
        {
            contact = new Contact
            {
                Channel = inbound.Channel,
                ExternalUserId = inbound.SenderId,
                DisplayName = inbound.SenderName,
                Phone = inbound.Channel == Channels.WhatsApp ? "+" + inbound.SenderId.TrimStart('+') : null,
                CreatedUtc = Now,
            };
            db.Contacts.Add(contact);
        }
        else if (string.IsNullOrWhiteSpace(contact.DisplayName) && !string.IsNullOrWhiteSpace(inbound.SenderName))
        {
            contact.DisplayName = inbound.SenderName;
        }

        var conversation = contact.Id == 0
            ? null
            : await db.Conversations.Include(c => c.Contact)
                .Where(c => c.ContactId == contact.Id)
                .OrderByDescending(c => c.LastActivityUtc)
                .FirstOrDefaultAsync(ct);

        if (conversation is null)
        {
            conversation = new Conversation { Contact = contact, Channel = inbound.Channel, CreatedUtc = Now, LastActivityUtc = Now };
            db.Conversations.Add(conversation);
        }
        return conversation;
    }

    private async Task<string?> TryTranscribeAsync(InboundMessage inbound, CancellationToken ct)
    {
        if (!_transcriber.IsConfigured || inbound.AudioReference is null) return null;
        if (!_channels.TryGetValue(inbound.Channel, out var channel) || channel is not IAudioDownloadChannel downloader) return null;
        try
        {
            var audio = await downloader.DownloadAudioAsync(inbound.AudioReference, ct);
            return audio is null ? null : await _transcriber.TranscribeAsync(audio.Value.Data, audio.Value.ContentType, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Could not transcribe voice note {Id}.", inbound.ExternalMessageId);
            return null;
        }
    }

    private static void UpdateContact(Contact contact, IntentResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.CustomerName) && result.CustomerName.Length <= 100)
            contact.DisplayName = result.CustomerName.Trim();
        if (!string.IsNullOrWhiteSpace(result.CustomerEmail) && MailAddress.TryCreate(result.CustomerEmail.Trim(), out var email))
            contact.Email = email.Address;
    }

    private static ReplyMode ModeFor(IntentDefinition? intent, BotSettings settings, bool inboundWasVoice)
    {
        var mode = intent?.ReplyMode ?? settings.DefaultReplyMode;
        return inboundWasVoice && settings.VoiceReplyToVoice && mode == ReplyMode.Text ? ReplyMode.Voice : mode;
    }

    private static Dictionary<string, string?> TemplateValues(BotSettings settings, Contact contact, string channel,
        DateTime? pendingSlot, Availability availability) => new()
    {
        ["business"] = settings.BusinessName,
        ["name"] = FirstName(contact.DisplayName),
        ["fullname"] = string.IsNullOrWhiteSpace(contact.DisplayName) ? "customer" : contact.DisplayName.Trim(),
        ["channel"] = channel,
        ["hours"] = settings.BusinessHours,
        ["slot"] = pendingSlot is { } p
            ? SlotFormatter.Friendly(p, availability.TimeZone)
            : availability.OpenSlots.Count > 0 ? SlotFormatter.Friendly(availability.OpenSlots[0].StartUtc, availability.TimeZone) : "",
        ["slots"] = string.Join(", ", availability.Nearest(null, 3).Select(s => SlotFormatter.Friendly(s.StartUtc, availability.TimeZone))),
    };

    private static string UnavailableReply(BotSettings settings, Dictionary<string, string?> values, Availability availability, DateTime requested)
    {
        var nearest = availability.Nearest(requested, 3);
        if (nearest.Count == 0) return "Sorry, we have no openings in the next few days. A team member will contact you to find a time.";
        values["slots"] = string.Join(", ", nearest.Select(s => SlotFormatter.Friendly(s.StartUtc, availability.TimeZone)));
        return TemplateRenderer.Render(settings.SlotUnavailableReply, values);
    }

    private static string? FirstName(string? displayName) =>
        string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim().Split(' ')[0];

    private static async Task<string> BuildBookingBodyAsync(ReceptionistDbContext db, Conversation conversation, CancellationToken ct)
    {
        var contact = conversation.Contact;
        var recent = await db.Messages.AsNoTracking()
            .Where(m => m.ConversationId == conversation.Id && m.Text != null && m.Status != MessageStatus.Draft)
            .OrderByDescending(m => m.Id).Take(8).ToListAsync(ct);
        recent.Reverse();

        var sb = new StringBuilder()
            .AppendLine("Booked automatically by the AI receptionist.")
            .AppendLine($"Customer: {contact.DisplayName ?? "(unknown)"}")
            .AppendLine($"Channel: {conversation.Channel} (user id {contact.ExternalUserId})");
        if (contact.Email is not null) sb.AppendLine($"Email: {contact.Email}");
        if (contact.Phone is not null) sb.AppendLine($"Phone: {contact.Phone}");
        sb.AppendLine().AppendLine("Recent conversation:");
        foreach (var m in recent)
            sb.AppendLine($"{(m.Direction == MessageDirection.Inbound ? "Customer" : "Receptionist")}: {m.Text}");
        return sb.ToString();
    }
}
