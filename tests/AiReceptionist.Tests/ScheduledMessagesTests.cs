using AiReceptionist.Core.Abstractions;
using AiReceptionist.Core.Conversations;
using AiReceptionist.Core.Data;
using AiReceptionist.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace AiReceptionist.Tests;

public class ScheduledMessagesTests
{
    private static readonly DateTime Start = TestHarness.Now.UtcDateTime;                   // Mon 28 Sep 08:00 UTC
    private static readonly DateTime TuesdayNoon = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);

    private static ScriptedEngine OfferThenWait() => new ScriptedEngine()
        .Then(_ => new IntentResult("BookAppointment", 0.9, "I can offer Tuesday at noon. Shall I book it?", TuesdayNoon))
        .Then(_ => new IntentResult("ConfirmLater", 0.9, "No problem, take your time!"));

    private static List<OutboundMessage> FollowUps(TestHarness h) =>
        h.Channel.Sent.Where(s => s.Text?.StartsWith("Hi ") == true && s.Text.Contains("Tue 29 Sep, 12:00 PM")).ToList();

    [Fact]
    public async Task Customer_who_will_confirm_later_gets_two_follow_ups_then_none()
    {
        await using var h = new TestHarness(OfferThenWait());
        await h.SendAsync("Can I book a call tomorrow?");
        await h.SendAsync("wait, let me check and confirm later");

        h.SetNow(Start.AddMinutes(30));
        await h.RunScheduledAsync();
        Assert.Empty(FollowUps(h));

        h.SetNow(Start.AddMinutes(61));
        await h.RunScheduledAsync();
        await h.RunScheduledAsync(); // runs every minute: must not repeat
        var first = Assert.Single(FollowUps(h));
        Assert.Contains("Reply YES", first.Text);
        Assert.Equal(false, first.Hint?.OutsideReplyWindow);

        h.SetNow(Start.AddHours(23).AddMinutes(1));
        await h.RunScheduledAsync();
        Assert.Equal(2, FollowUps(h).Count);

        h.SetNow(Start.AddHours(30));
        await h.RunScheduledAsync();
        Assert.Equal(2, FollowUps(h).Count);

        await using var db = h.Db();
        Assert.Equal(2, await db.Messages.CountAsync(m => m.Intent == ConversationService.FollowUpIntent && m.Status == MessageStatus.Sent));
        Assert.Equal(TuesdayNoon, (await db.Conversations.SingleAsync()).PendingSlotStartUtc); // still bookable with "yes"
    }

    [Fact]
    public async Task No_follow_up_when_the_customer_answered_or_booked()
    {
        var engine = new ScriptedEngine()
            .Then(_ => new IntentResult("BookAppointment", 0.9, "Tuesday at noon?", TuesdayNoon))
            .Then(_ => new IntentResult("ConfirmAppointment", 0.9, "", null, true));
        await using var h = new TestHarness(engine);
        await h.SendAsync("Can I book a call tomorrow?");
        await h.SendAsync("yes");

        h.SetNow(Start.AddHours(2));
        await h.RunScheduledAsync();
        Assert.Empty(FollowUps(h));
    }

    [Fact]
    public async Task Follow_up_is_skipped_outside_the_24_hour_window()
    {
        await using var h = new TestHarness(OfferThenWait());
        h.Channel.Capabilities = h.Channel.Capabilities with { ReplyWindow = TimeSpan.FromHours(24) };
        await h.UpdateSettingsAsync(s => { s.FollowUp1Minutes = 60 * 24 + 10; });
        await h.SendAsync("Can I book a call tomorrow?");
        await h.SendAsync("let me check");

        h.SetNow(Start.AddHours(25));
        await h.RunScheduledAsync();
        Assert.Empty(FollowUps(h));
        await using var db = h.Db();
        Assert.Equal(2, (await db.Conversations.SingleAsync()).FollowUpsSent); // gave up
    }

    [Fact]
    public async Task Follow_ups_can_be_turned_off()
    {
        await using var h = new TestHarness(OfferThenWait());
        await h.UpdateSettingsAsync(s => s.FollowUpsEnabled = false);
        await h.SendAsync("Can I book a call tomorrow?");
        await h.SendAsync("wait");

        h.SetNow(Start.AddHours(2));
        await h.RunScheduledAsync();
        Assert.Empty(FollowUps(h));
    }

    [Fact]
    public async Task Reminder_is_sent_15_minutes_before_the_appointment_once()
    {
        await using var h = new TestHarness();
        await h.SendAsync("Can I book an appointment tomorrow afternoon? jane@example.com");
        await h.SendAsync("yes");
        var before = h.Channel.Sent.Count;

        h.SetNow(TuesdayNoon.AddMinutes(-20));
        await h.RunScheduledAsync();
        Assert.Equal(before, h.Channel.Sent.Count);

        h.SetNow(TuesdayNoon.AddMinutes(-15));
        await h.RunScheduledAsync();
        await h.RunScheduledAsync();
        var reminder = Assert.Single(h.Channel.Sent.Skip(before));
        Assert.Contains("starts in 15 minutes", reminder.Text);
        Assert.True(reminder.Hint?.IsAppointmentReminder);

        await using var db = h.Db();
        Assert.NotNull((await db.Appointments.SingleAsync()).ReminderSentUtc);
        Assert.True(await db.Messages.AnyAsync(m => m.Intent == ConversationService.ReminderIntent && m.Status == MessageStatus.Sent));
    }

    [Fact]
    public async Task Reminder_outside_the_window_uses_template_hint_or_falls_back_to_email()
    {
        await using var h = new TestHarness();
        h.Channel.Capabilities = h.Channel.Capabilities with { ReplyWindow = TimeSpan.FromHours(24) };
        await h.UpdateSettingsAsync(s => s.WhatsAppReminderTemplate = "appointment_reminder");
        await h.SendAsync("Can I book an appointment tomorrow afternoon? jane@example.com");
        await h.SendAsync("yes");
        h.Email.Sent.Clear();
        h.Channel.RejectOutsideWindow = true; // like Instagram

        h.SetNow(TuesdayNoon.AddMinutes(-15)); // 28 h after the customer's last message
        await h.RunScheduledAsync();

        var email = Assert.Single(h.Email.Sent);
        Assert.Equal(new[] { "jane@example.com" }, email.To);
        Assert.Contains("starts in 15 minutes", email.Body);

        await using var db = h.Db();
        var message = await db.Messages.SingleAsync(m => m.Intent == ConversationService.ReminderIntent);
        Assert.Equal(MessageStatus.Failed, message.Status);
        Assert.Contains("sent by email instead", message.Error);
        Assert.False((await db.Conversations.SingleAsync()).NeedsAttention);
    }

    [Fact]
    public async Task Reminder_hint_carries_the_whatsapp_template()
    {
        await using var h = new TestHarness();
        h.Channel.Capabilities = h.Channel.Capabilities with { ReplyWindow = TimeSpan.FromHours(24) };
        await h.UpdateSettingsAsync(s => s.WhatsAppReminderTemplate = "appointment_reminder");
        await h.SendAsync("Can I book an appointment tomorrow afternoon? I'm Jane");
        await h.SendAsync("yes");

        h.SetNow(TuesdayNoon.AddMinutes(-15));
        await h.RunScheduledAsync();

        var hint = h.Channel.Sent.Last().Hint!;
        Assert.True(hint.OutsideReplyWindow);
        Assert.Equal("appointment_reminder", hint.TemplateName);
        Assert.Equal(new[] { "Jane", "Contoso Clinic", "Tue 29 Sep, 12:00 PM" }, hint.TemplateParameters);
    }

    [Fact]
    public async Task Existing_database_gets_new_columns_and_defaults()
    {
        await using var h = new TestHarness();
        await using (var cmd = h.Connection.CreateCommand())
        {
            cmd.CommandText = """
                ALTER TABLE "Settings" DROP COLUMN "FollowUp1Message";
                ALTER TABLE "Settings" DROP COLUMN "SeedVersion";
                ALTER TABLE "Appointments" DROP COLUMN "ReminderSentUtc";
                ALTER TABLE "Conversations" DROP COLUMN "FollowUpsSent";
                DELETE FROM "Intents" WHERE "Name" = 'ConfirmLater';
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var db = h.Db()) await SeedData.InitializeAsync(db);

        await using var check = h.Db();
        var settings = await check.Settings.SingleAsync();
        Assert.Equal(new BotSettings().FollowUp1Message, settings.FollowUp1Message);
        Assert.Equal(SeedData.CurrentSeedVersion, settings.SeedVersion);
        Assert.True(await check.Intents.AnyAsync(i => i.Name == "ConfirmLater"));
        Assert.Empty(await check.Appointments.Where(a => a.ReminderSentUtc != null).ToListAsync());

        // Deleting the intent afterwards is respected (not re-added on the next start).
        await check.Intents.Where(i => i.Name == "ConfirmLater").ExecuteDeleteAsync();
        await using (var db = h.Db()) await SeedData.InitializeAsync(db);
        await using var again = h.Db();
        Assert.False(await again.Intents.AnyAsync(i => i.Name == "ConfirmLater"));
    }
}
