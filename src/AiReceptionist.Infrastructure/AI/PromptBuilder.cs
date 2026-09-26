using System.Globalization;
using System.Text;
using AiReceptionist.Core.Abstractions;
using AiReceptionist.Core.Domain;
using AiReceptionist.Core.Scheduling;

namespace AiReceptionist.Infrastructure.AI;

/// <summary>Builds the system prompt from the admin-editable settings, intents and live availability.</summary>
public static class PromptBuilder
{
    public static string BuildSystemPrompt(IntentContext ctx)
    {
        var s = ctx.Settings;
        var tz = ctx.TimeZone;
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(ctx.NowUtc, tz);
        var sb = new StringBuilder();

        sb.AppendLine(string.IsNullOrWhiteSpace(s.SystemPrompt) ? "You are a helpful receptionist." : s.SystemPrompt.Trim());
        sb.AppendLine();
        sb.AppendLine("## Business");
        sb.AppendLine($"Name: {s.BusinessName}");
        if (!string.IsNullOrWhiteSpace(s.BusinessDescription)) sb.AppendLine(s.BusinessDescription.Trim());
        sb.AppendLine($"Opening hours: {s.BusinessHours} ({tz.Id})");
        sb.AppendLine($"Appointment length: {s.AppointmentMinutes} minutes");
        sb.AppendLine(FormattableString.Invariant($"Current local time: {nowLocal:dddd yyyy-MM-dd HH:mm} ({SlotFormatter.Iso(ctx.NowUtc, tz)})"));
        sb.AppendLine();

        sb.AppendLine("## Customer");
        sb.AppendLine($"Channel: {ctx.Channel}");
        sb.AppendLine($"Name: {ctx.CustomerName ?? "unknown"}");
        sb.AppendLine($"Email: {ctx.CustomerEmail ?? "unknown"}");
        sb.AppendLine();

        sb.AppendLine("## Intents (choose exactly one name)");
        foreach (var i in ctx.Intents)
        {
            sb.AppendLine($"- {i.Name}: {i.Description} [action: {Describe(i.Action)}]");
            var examples = i.Examples.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (examples.Length > 0) sb.AppendLine($"  Examples: {string.Join(" | ", examples)}");
            if (!string.IsNullOrWhiteSpace(i.Guidance)) sb.AppendLine($"  How to reply: {i.Guidance.Trim()}");
        }
        sb.AppendLine();

        sb.AppendLine($"## Free time for appointments (local time; a {s.AppointmentMinutes}-minute appointment may start at the start of a range or any multiple of {s.AppointmentMinutes} minutes after it, and must end inside the range)");
        if (ctx.OpenSlots.Count == 0) sb.AppendLine("None available in the booking window.");
        foreach (var line in FreeRanges(ctx.OpenSlots, tz)) sb.AppendLine(line);
        sb.AppendLine();

        sb.AppendLine("## Pending offer");
        sb.AppendLine(ctx.PendingSlotUtc is { } p
            ? $"You already offered {SlotFormatter.Friendly(p, tz)} ({SlotFormatter.Iso(p, tz)}) and are waiting for the customer to confirm."
            : "No slot is currently awaiting confirmation.");
        sb.AppendLine();

        sb.AppendLine("""
            ## Rules
            - Only offer times inside the free ranges. Put the offered start in proposedSlotStart as ISO-8601 local time with that day's UTC offset, e.g. 2026-09-29T14:00:00+01:00.
            - If the customer has no preference, suggest the earliest suitable time (or 2-3 options) and still set proposedSlotStart to your first suggestion.
            - Never say an appointment is booked or confirmed: the system books it after the customer accepts and sends its own confirmation.
            - When the customer accepts the pending offer, use the intent whose action is "confirm" and set confirmsPendingSlot to true.
            - If they accept a different listed time in the same message, set proposedSlotStart to it and confirmsPendingSlot to true.
            - Extract the customer's name and email if they mention them.
            - Replies are sent as chat messages and may be read aloud: 1-3 short sentences, plain text, no markdown, no emojis, same language as the customer.
            - confidence is how sure you are about the intent, from 0 to 1.

            Respond with ONLY a JSON object:
            {"intent": "<intent name>", "confidence": 0.0, "reply": "<message to the customer>", "proposedSlotStart": "<ISO value or null>", "confirmsPendingSlot": false, "customerName": null, "customerEmail": null}
            """);
        return sb.ToString();
    }

    /// <summary>Collapses consecutive open slots into per-day ranges, e.g. "- Tue 29 Sep (UTC+01:00): 09:00-12:00, 13:30-17:00".</summary>
    public static IEnumerable<string> FreeRanges(IReadOnlyList<TimeSlot> slots, TimeZoneInfo tz)
    {
        foreach (var day in slots.GroupBy(sl => TimeZoneInfo.ConvertTimeFromUtc(sl.StartUtc, tz).Date))
        {
            var ranges = new List<(DateTime Start, DateTime End)>();
            foreach (var sl in day.OrderBy(x => x.StartUtc))
            {
                if (ranges.Count > 0 && ranges[^1].End == sl.StartUtc) ranges[^1] = (ranges[^1].Start, sl.EndUtc);
                else ranges.Add((sl.StartUtc, sl.EndUtc));
            }
            var offset = tz.GetUtcOffset(DateTime.SpecifyKind(day.First().StartUtc, DateTimeKind.Utc));
            var sign = offset < TimeSpan.Zero ? "-" : "+";
            var text = string.Join(", ", ranges.Select(r => FormattableString.Invariant(
                $"{TimeZoneInfo.ConvertTimeFromUtc(r.Start, tz):HH:mm}-{TimeZoneInfo.ConvertTimeFromUtc(r.End, tz):HH:mm}")));
            yield return FormattableString.Invariant($"- {day.Key:ddd d MMM yyyy} (UTC{sign}{offset:hh\\:mm}): {text}");
        }
    }

    private static string Describe(IntentAction action) => action switch
    {
        IntentAction.ProposeAppointment => "offer a slot",
        IntentAction.ConfirmAppointment => "confirm",
        IntentAction.CancelAppointment => "cancel appointment",
        IntentAction.HumanHandoff => "hand over to a human",
        _ => "reply",
    };
}
