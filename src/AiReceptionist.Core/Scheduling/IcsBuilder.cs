using System.Globalization;
using System.Text;

namespace AiReceptionist.Core.Scheduling;

/// <summary>Builds a minimal RFC 5545 iCalendar file, attached to fallback confirmation emails
/// so the recipient can add the appointment to any calendar with one click.</summary>
public static class IcsBuilder
{
    public static string Build(string uid, DateTime startUtc, DateTime endUtc, string summary, string description,
        string? organizerEmail, string? attendeeEmail, DateTime stampUtc, bool cancel = false)
    {
        var sb = new StringBuilder();
        void Line(string s) => sb.Append(s).Append("\r\n");

        Line("BEGIN:VCALENDAR");
        Line("PRODID:-//AiReceptionist//EN");
        Line("VERSION:2.0");
        Line(cancel ? "METHOD:CANCEL" : "METHOD:PUBLISH");
        Line("BEGIN:VEVENT");
        Line($"UID:{Escape(uid)}");
        Line($"DTSTAMP:{Format(stampUtc)}");
        Line($"DTSTART:{Format(startUtc)}");
        Line($"DTEND:{Format(endUtc)}");
        Line($"SUMMARY:{Escape(summary)}");
        Line($"DESCRIPTION:{Escape(description)}");
        if (!string.IsNullOrWhiteSpace(organizerEmail)) Line($"ORGANIZER:mailto:{organizerEmail}");
        if (!string.IsNullOrWhiteSpace(attendeeEmail)) Line($"ATTENDEE;ROLE=REQ-PARTICIPANT:mailto:{attendeeEmail}");
        Line(cancel ? "STATUS:CANCELLED" : "STATUS:CONFIRMED");
        Line("END:VEVENT");
        Line("END:VCALENDAR");
        return sb.ToString();
    }

    private static string Format(DateTime utc) => utc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    private static string Escape(string s) => s
        .Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,")
        .Replace("\r\n", "\\n").Replace("\n", "\\n");
}
