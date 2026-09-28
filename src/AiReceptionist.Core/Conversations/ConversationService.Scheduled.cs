using AiReceptionist.Core.Abstractions;
using AiReceptionist.Core.Data;
using AiReceptionist.Core.Domain;
using AiReceptionist.Core.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AiReceptionist.Core.Conversations;

/// <summary>Messages the receptionist starts itself: follow-ups on unconfirmed offers and appointment reminders.</summary>
public sealed partial class ConversationService
{
    public const string FollowUpIntent = "FollowUp";
    public const string ReminderIntent = "Reminder";

    // Stay safely inside Meta's 24-hour customer-service window.
    private static readonly TimeSpan WindowMargin = TimeSpan.FromMinutes(5);

    /// <summary>Sends every follow-up and reminder that is due. Called about once a minute by a background worker.</summary>
    public async Task RunScheduledMessagesAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var settings = await LoadSettingsAsync(db, ct);

        if (settings.RemindersEnabled) await SendDueRemindersAsync(db, settings, ct);
        if (settings.FollowUpsEnabled && settings.AutoReplyEnabled && !settings.RequireApproval)
            await SendDueFollowUpsAsync(db, settings, ct);
    }

    /// <summary>
    /// A slot was offered, the customer went quiet (or said they'd confirm later) and nothing is booked yet:
    /// nudge them after <see cref="BotSettings.FollowUp1Minutes"/> and again after <see cref="BotSettings.FollowUp2Minutes"/>.
    /// </summary>
    private async Task SendDueFollowUpsAsync(ReceptionistDbContext db, BotSettings settings, CancellationToken ct)
    {
        var delays = new[] { settings.FollowUp1Minutes, settings.FollowUp2Minutes };
        var messages = new[] { settings.FollowUp1Message, settings.FollowUp2Message };

        var candidates = await db.Conversations.Include(c => c.Contact)
            .Where(c => c.PendingSlotStartUtc != null && c.Mode == ConversationMode.Bot && c.FollowUpsSent < delays.Length)
            .ToListAsync(ct);
        if (candidates.Count == 0) return;

        Availability? availability = null;
        foreach (var conversation in candidates)
        {
            var step = conversation.FollowUpsSent;
            if (delays[step] <= 0 || string.IsNullOrWhiteSpace(messages[step])) continue;

            var last = await db.Messages.AsNoTracking()
                .Where(m => m.ConversationId == conversation.Id && m.Status != MessageStatus.Draft)
                .OrderByDescending(m => m.CreatedUtc).ThenByDescending(m => m.Id)
                .FirstOrDefaultAsync(ct);
            var lastInbound = await LastInboundUtcAsync(db, conversation.Id, ct);
            // Only when the business had the last word (the customer hasn't answered the offer).
            if (last is null || last.Direction != MessageDirection.Outbound || lastInbound is null) continue;

            var quiet = Now - lastInbound.Value;
            if (quiet < TimeSpan.FromMinutes(delays[step])) continue;
            if (_channels.TryGetValue(conversation.Channel, out var channel) &&
                channel.Capabilities.ReplyWindow is { } window && quiet > window - WindowMargin)
            {
                conversation.FollowUpsSent = delays.Length; // too late to message; stop trying
                await db.SaveChangesAsync(ct);
                continue;
            }

            availability ??= await _availability.GetAsync(settings, ct);
            var slot = conversation.PendingSlotStartUtc!.Value;
            if (!availability.IsBookable(slot))
            {
                // The offered time passed or was taken: offer the nearest free time instead.
                var next = availability.Nearest(slot, 1);
                if (next.Count == 0)
                {
                    conversation.FollowUpsSent = delays.Length;
                    await db.SaveChangesAsync(ct);
                    continue;
                }
                conversation.PendingSlotStartUtc = slot = next[0].StartUtc;
            }

            var values = TemplateValues(settings, conversation.Contact, conversation.Channel, slot, availability);
            values["name"] ??= "there";
            conversation.FollowUpsSent = step + 1;
            _log.LogInformation("Sending follow-up {Step} to conversation {Conversation}.", step + 1, conversation.Id);
            await DeliverAsync(db, settings, conversation, TemplateRenderer.Render(messages[step], values), ReplyMode.Text,
                FollowUpIntent, null, isManual: false, existing: null, ct, new DeliveryHint(OutsideReplyWindow: false));
        }
    }

    /// <summary>Reminds the customer <see cref="BotSettings.ReminderMinutesBefore"/> minutes before a booked appointment.
    /// Falls back to email when the chat platform does not allow the message.</summary>
    private async Task SendDueRemindersAsync(ReceptionistDbContext db, BotSettings settings, CancellationToken ct)
    {
        var now = Now;
        var until = now.AddMinutes(Math.Max(1, settings.ReminderMinutesBefore));
        var due = await db.Appointments.Include(a => a.Conversation).ThenInclude(c => c.Contact)
            .Where(a => a.ReminderSentUtc == null && a.StartUtc > now && a.StartUtc <= until &&
                        (a.Status == AppointmentStatus.Booked || a.Status == AppointmentStatus.EmailFallback))
            .ToListAsync(ct);

        var tz = TimeZoneResolver.Resolve(settings.TimeZoneId);
        foreach (var appointment in due)
        {
            var conversation = appointment.Conversation;
            var minutes = Math.Max(1, (int)Math.Round((appointment.StartUtc - now).TotalMinutes));
            var slot = SlotFormatter.Friendly(appointment.StartUtc, tz);
            var name = FirstName(appointment.CustomerName ?? conversation.Contact.DisplayName);
            var text = TemplateRenderer.Render(settings.ReminderMessage, new Dictionary<string, string?>
            {
                ["name"] = name ?? "there",
                ["fullname"] = appointment.CustomerName ?? conversation.Contact.DisplayName ?? "customer",
                ["business"] = settings.BusinessName,
                ["slot"] = slot,
                ["minutes"] = minutes.ToString(),
                ["channel"] = conversation.Channel,
                ["hours"] = settings.BusinessHours,
            });

            var lastInbound = await LastInboundUtcAsync(db, conversation.Id, ct);
            var outside = _channels.TryGetValue(conversation.Channel, out var channel) &&
                          channel.Capabilities.ReplyWindow is { } window &&
                          (lastInbound is null || now - lastInbound.Value > window - WindowMargin);
            var hint = new DeliveryHint(outside, IsAppointmentReminder: true,
                string.IsNullOrWhiteSpace(settings.WhatsAppReminderTemplate) ? null : settings.WhatsAppReminderTemplate.Trim(),
                string.IsNullOrWhiteSpace(settings.WhatsAppTemplateLanguage) ? "en" : settings.WhatsAppTemplateLanguage.Trim(),
                new[] { name ?? "there", settings.BusinessName, slot });

            appointment.ReminderSentUtc = now; // once only, even if delivery fails
            await db.SaveChangesAsync(ct);
            _log.LogInformation("Sending reminder for appointment {Appointment}.", appointment.Id);

            var sent = await DeliverAsync(db, settings, conversation, text, ReplyMode.Text, ReminderIntent, null,
                isManual: false, existing: null, ct, hint);
            if (sent.Status == MessageStatus.Sent) continue;

            var emailError = await _appointments.SendReminderEmailAsync(appointment, text, settings.BusinessName, ct);
            if (emailError is null)
            {
                sent.Error += " | Reminder sent by email instead.";
                // Delivered by email: no need for the admin to act on the failed chat message.
                conversation.NeedsAttention = await db.Messages.AnyAsync(m => m.ConversationId == conversation.Id && m.Status == MessageStatus.Draft, ct);
            }
            else
            {
                sent.Error += " | " + emailError;
            }
            await db.SaveChangesAsync(ct);
        }
    }

    private static Task<DateTime?> LastInboundUtcAsync(ReceptionistDbContext db, int conversationId, CancellationToken ct) =>
        db.Messages.Where(m => m.ConversationId == conversationId && m.Direction == MessageDirection.Inbound)
            .MaxAsync(m => (DateTime?)m.CreatedUtc, ct);
}
