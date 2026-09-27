using AiReceptionist.Core.Abstractions;
using AiReceptionist.Core.Domain;
using AiReceptionist.Core.Scheduling;
using Microsoft.EntityFrameworkCore;

namespace AiReceptionist.Tests;

public class ConversationServiceTests
{
    private static readonly DateTime TuesdayNoon = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Greeting_is_answered_with_text_and_voice()
    {
        await using var h = new TestHarness();

        await h.SendAsync("Hi");

        Assert.Equal(2, h.Channel.Sent.Count);
        Assert.Contains("Contoso Clinic", h.Channel.Sent[0].Text);
        Assert.NotNull(h.Channel.Sent[1].AudioUrl);
        Assert.Single(h.Voice.Spoken);

        await using var db = h.Db();
        var outbound = await db.Messages.SingleAsync(m => m.Direction == MessageDirection.Outbound);
        Assert.Equal(MessageStatus.Sent, outbound.Status);
        Assert.Equal(ReplyMode.TextAndVoice, outbound.ReplyMode);
        Assert.Equal("Greeting", (await db.Messages.SingleAsync(m => m.Direction == MessageDirection.Inbound)).Intent);
    }

    [Fact]
    public async Task Booking_flow_offers_slot_then_writes_calendar_event_on_confirmation()
    {
        await using var h = new TestHarness();

        await h.SendAsync("Can I book an appointment tomorrow afternoon?");

        await using (var db = h.Db())
        {
            var conversation = await db.Conversations.SingleAsync();
            Assert.Equal(TuesdayNoon, conversation.PendingSlotStartUtc);
        }
        Assert.Contains("Tue 29 Sep, 12:00 PM", h.Channel.Sent[^1].Text);
        Assert.Empty(h.Calendar.Created);

        await h.SendAsync("Yes please");

        var created = Assert.Single(h.Calendar.Created);
        Assert.Equal(TuesdayNoon, created.StartUtc);
        Assert.Equal(TuesdayNoon.AddMinutes(30), created.EndUtc);
        Assert.Contains("Jane Doe", created.Subject);

        await using (var db = h.Db())
        {
            var appt = await db.Appointments.SingleAsync();
            Assert.Equal(AppointmentStatus.Booked, appt.Status);
            Assert.Equal(BookingMethod.Calendar, appt.Method);
            Assert.Equal("evt-1", appt.ExternalEventId);
            Assert.Null((await db.Conversations.SingleAsync()).PendingSlotStartUtc);
        }

        // Confirmation is sent as text and spoken (ConfirmAppointment intent is TextAndVoice by default).
        var texts = h.Channel.Sent.Where(s => s.Text is not null).Select(s => s.Text!).ToList();
        Assert.Contains(texts, t => t.StartsWith("You're all set, Jane!") && t.Contains("Tue 29 Sep, 12:00 PM"));
        Assert.NotNull(h.Channel.Sent[^1].AudioUrl);
    }

    [Fact]
    public async Task Busy_calendar_time_is_not_offered()
    {
        await using var h = new TestHarness();
        h.Calendar.Busy.Add(new TimeSlot(TuesdayNoon, TuesdayNoon.AddHours(1)));

        await h.SendAsync("Can I book an appointment tomorrow afternoon?");

        await using var db = h.Db();
        Assert.Equal(TuesdayNoon.AddHours(1), (await db.Conversations.SingleAsync()).PendingSlotStartUtc);
    }

    [Fact]
    public async Task Calendar_failure_falls_back_to_confirmation_email_with_ics()
    {
        await using var h = new TestHarness();
        h.Calendar.Throw = true;

        await h.SendAsync("Can I book an appointment tomorrow afternoon?");
        await h.SendAsync("yes");

        Assert.Empty(h.Calendar.Created);
        var email = Assert.Single(h.Email.Sent);
        Assert.Contains("owner@example.com", email.To);
        var ics = Assert.Single(email.Attachments!);
        Assert.Equal("text/calendar", ics.ContentType);
        Assert.Contains("DTSTART:20260929T120000Z", System.Text.Encoding.UTF8.GetString(ics.Data));

        await using var db = h.Db();
        var appt = await db.Appointments.SingleAsync();
        Assert.Equal(AppointmentStatus.EmailFallback, appt.Status);
        Assert.Contains("Graph down", appt.Error);
        Assert.Contains(h.Channel.Sent, s => s.Text?.Contains("notified by email") == true);
    }

    [Fact]
    public async Task Booking_fails_gracefully_when_neither_calendar_nor_email_is_available()
    {
        await using var h = new TestHarness();
        h.Calendar.IsConfigured = false;
        h.Email.IsConfigured = false;

        await h.SendAsync("Can I book an appointment tomorrow afternoon?");
        await h.SendAsync("yes");

        await using var db = h.Db();
        Assert.Equal(AppointmentStatus.Failed, (await db.Appointments.SingleAsync()).Status);
        Assert.True((await db.Conversations.SingleAsync()).NeedsAttention);
        Assert.Contains(h.Channel.Sent, s => s.Text?.Contains("couldn't finalise") == true);
    }

    [Fact]
    public async Task Llm_proposing_an_unavailable_slot_is_corrected_with_real_openings()
    {
        var sunday = new DateTime(2026, 10, 4, 10, 0, 0, DateTimeKind.Utc);
        var engine = new ScriptedEngine().Then(_ => new IntentResult("BookAppointment", 0.9, "Sunday at 10 works!", sunday));
        await using var h = new TestHarness(engine);

        await h.SendAsync("Can I come Sunday at 10?");

        var reply = h.Channel.Sent.Single().Text!;
        Assert.StartsWith("Sorry, that time isn't available", reply);
        Assert.DoesNotContain("Sunday at 10 works", reply);
        await using var db = h.Db();
        Assert.Null((await db.Conversations.SingleAsync()).PendingSlotStartUtc);

        // The engine was given real open slots and the business time zone.
        Assert.True(engine.Contexts[0].OpenSlots.Count > 100); // whole booking horizon, not just the first day
        Assert.Equal(new DateTime(2026, 9, 28, 9, 0, 0, DateTimeKind.Utc), engine.Contexts[0].OpenSlots[0].StartUtc);
    }

    [Fact]
    public async Task Llm_can_confirm_a_different_valid_slot_in_one_message()
    {
        var engine = new ScriptedEngine()
            .Then(_ => new IntentResult("BookAppointment", 0.9, "How about Tuesday at noon?", TuesdayNoon))
            .Then(_ => new IntentResult("ConfirmAppointment", 0.9, "", TuesdayNoon.AddHours(2), true, "Jane Smith", "jane@example.com"));
        await using var h = new TestHarness(engine);

        await h.SendAsync("book me in tuesday");
        await h.SendAsync("Actually 2pm is better, go ahead. I'm Jane Smith, jane@example.com");

        var created = Assert.Single(h.Calendar.Created);
        Assert.Equal(TuesdayNoon.AddHours(2), created.StartUtc);
        Assert.Equal("jane@example.com", created.CustomerEmail);
        await using var db = h.Db();
        var contact = await db.Contacts.SingleAsync();
        Assert.Equal("Jane Smith", contact.DisplayName);
        Assert.Equal("jane@example.com", contact.Email);
    }

    [Fact]
    public async Task Low_confidence_sends_handoff_message_and_flags_conversation()
    {
        var engine = new ScriptedEngine().Then(_ => new IntentResult("Other", 0.1, "I think maybe…"));
        await using var h = new TestHarness(engine);

        await h.SendAsync("asdf qwerty");

        Assert.Equal(new Core.Domain.BotSettings().HandoffMessage, h.Channel.Sent.Single().Text);
        await using var db = h.Db();
        Assert.True((await db.Conversations.SingleAsync()).NeedsAttention);
    }

    [Fact]
    public async Task Engine_exception_does_not_lose_the_message()
    {
        var engine = new ScriptedEngine().Then(_ => throw new InvalidOperationException("LLM quota exceeded"));
        await using var h = new TestHarness(engine);

        await h.SendAsync("hello?");

        Assert.Single(h.Channel.Sent);
        await using var db = h.Db();
        Assert.True((await db.Conversations.SingleAsync()).NeedsAttention);
        Assert.Equal(2, await db.Messages.CountAsync());
    }

    [Fact]
    public async Task Human_takeover_stops_automatic_replies()
    {
        await using var h = new TestHarness();
        await h.SendAsync("Hi");
        var conversationId = (await h.Db().Conversations.SingleAsync()).Id;
        var sentBefore = h.Channel.Sent.Count;

        await h.Service.SetModeAsync(conversationId, ConversationMode.HumanTakeover);
        await h.SendAsync("Hello again");

        Assert.Equal(sentBefore, h.Channel.Sent.Count);
        await using var db = h.Db();
        Assert.True((await db.Conversations.SingleAsync()).NeedsAttention);
    }

    [Fact]
    public async Task Duplicate_webhook_deliveries_are_processed_once()
    {
        await using var h = new TestHarness();

        await h.SendAsync("Hi", externalId: "mid.123");
        await h.SendAsync("Hi", externalId: "mid.123");

        await using var db = h.Db();
        Assert.Equal(1, await db.Messages.CountAsync(m => m.Direction == MessageDirection.Inbound));
        Assert.Equal(2, h.Channel.Sent.Count); // one text + one audio
    }

    [Fact]
    public async Task Approval_mode_creates_draft_that_admin_can_edit_and_send()
    {
        await using var h = new TestHarness();
        await h.UpdateSettingsAsync(s => s.RequireApproval = true);

        await h.SendAsync("What are your opening hours?");

        Assert.Empty(h.Channel.Sent);
        int draftId;
        await using (var db = h.Db())
        {
            var draft = await db.Messages.SingleAsync(m => m.Status == MessageStatus.Draft);
            Assert.Contains("Mon-Fri 09:00-17:00", draft.Text);
            draftId = draft.Id;
        }

        var sent = await h.Service.ApproveDraftAsync(draftId, "We're open 9-5 on weekdays.", ReplyMode.Text);

        Assert.Equal(MessageStatus.Sent, sent.Status);
        Assert.True(sent.IsManual);
        Assert.Equal("We're open 9-5 on weekdays.", h.Channel.Sent.Single().Text);
    }

    [Fact]
    public async Task Rules_run_before_the_engine()
    {
        var engine = new ScriptedEngine(); // would throw if called
        await using var h = new TestHarness(engine);

        await h.SendAsync("STOP");
        Assert.StartsWith("You won't receive further automated replies", h.Channel.Sent.Single().Text);

        await h.SendAsync("This is URGENT", sender: "user-2");
        await using var db = h.Db();
        var urgent = await db.Conversations.Include(c => c.Contact).SingleAsync(c => c.Contact.ExternalUserId == "user-2");
        Assert.Equal(ConversationMode.HumanTakeover, urgent.Mode);
        Assert.Empty(engine.Contexts);
    }

    [Fact]
    public async Task Voice_on_channel_without_audio_support_is_sent_as_text_with_link()
    {
        await using var h = new TestHarness();

        await h.SendAsync("Hi", channel: Channels.Twitter);

        var msg = h.TextOnlyChannel.Sent.Single();
        Assert.Contains("🔊 https://bot.example.com/media/", msg.Text);
        Assert.Null(msg.AudioUrl);
    }

    [Fact]
    public async Task Voice_falls_back_to_text_when_speech_is_not_configured()
    {
        await using var h = new TestHarness();
        h.Voice.IsConfigured = false;

        await h.SendAsync("Hi");

        Assert.Single(h.Channel.Sent);
        await using var db = h.Db();
        var outbound = await db.Messages.SingleAsync(m => m.Direction == MessageDirection.Outbound);
        Assert.Equal(ReplyMode.Text, outbound.ReplyMode);
        Assert.Equal(MessageStatus.Sent, outbound.Status);
    }

    [Fact]
    public async Task Voice_note_is_answered_with_voice_only()
    {
        await using var h = new TestHarness();

        await h.Service.ProcessInboundAsync(new InboundMessage(Channels.Simulator, "u", null, "what are your opening hours", "v1",
            DateTime.UtcNow, IsVoice: true), CancellationToken.None);

        var sent = h.Channel.Sent.Single();
        Assert.Null(sent.Text);
        Assert.NotNull(sent.AudioUrl);
    }

    [Fact]
    public async Task Untranscribable_voice_note_asks_the_customer_to_type()
    {
        await using var h = new TestHarness();

        await h.Service.ProcessInboundAsync(new InboundMessage(Channels.Simulator, "u", null, null, "v9", DateTime.UtcNow,
            IsVoice: true, AudioReference: "https://cdn.example.com/a.mp4"), CancellationToken.None);

        Assert.Equal("Sorry, I couldn't catch that voice message. Could you type it instead?", h.Channel.Sent.Single().Text);
    }

    [Fact]
    public async Task Manual_override_reply_is_delivered_and_marked_manual()
    {
        await using var h = new TestHarness();
        await h.SendAsync("Hi");
        var id = (await h.Db().Conversations.SingleAsync()).Id;

        var msg = await h.Service.SendManualReplyAsync(id, "Hello from a human!", ReplyMode.Text);

        Assert.True(msg.IsManual);
        Assert.Equal("Hello from a human!", h.Channel.Sent[^1].Text);
    }

    [Fact]
    public async Task Delivery_failure_is_recorded_and_flagged()
    {
        await using var h = new TestHarness();
        h.Channel.Fail = true;

        await h.SendAsync("What are your opening hours?");

        await using var db = h.Db();
        var outbound = await db.Messages.SingleAsync(m => m.Direction == MessageDirection.Outbound);
        Assert.Equal(MessageStatus.Failed, outbound.Status);
        Assert.Contains("boom", outbound.Error);
        Assert.True((await db.Conversations.SingleAsync()).NeedsAttention);
    }

    [Fact]
    public async Task Customer_can_cancel_their_appointment()
    {
        await using var h = new TestHarness();
        await h.SendAsync("Can I book an appointment tomorrow afternoon?");
        await h.SendAsync("yes");

        await h.SendAsync("please cancel my appointment");

        Assert.Equal("evt-1", h.Calendar.Cancelled.Single());
        await using var db = h.Db();
        Assert.Equal(AppointmentStatus.Cancelled, (await db.Appointments.SingleAsync()).Status);
        Assert.Contains(h.Channel.Sent, s => s.Text?.Contains("has been cancelled") == true);
    }

    [Fact]
    public async Task Offered_slot_is_no_longer_offered_to_others_after_booking()
    {
        await using var h = new TestHarness();
        await h.SendAsync("Can I book an appointment tomorrow afternoon?", sender: "a");
        await h.SendAsync("yes", sender: "a");

        await h.SendAsync("Can I book an appointment tomorrow afternoon?", sender: "b");

        await using var db = h.Db();
        var b = await db.Conversations.Include(c => c.Contact).SingleAsync(c => c.Contact.ExternalUserId == "b");
        Assert.Equal(TuesdayNoon.AddMinutes(30), b.PendingSlotStartUtc);
    }
}
