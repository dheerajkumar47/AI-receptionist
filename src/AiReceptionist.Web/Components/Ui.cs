using AiReceptionist.Core.Domain;

namespace AiReceptionist.Web.Components;

/// <summary>Small formatting helpers shared by the dashboard pages.</summary>
public static class Ui
{
    public static string Local(DateTime utc, TimeZoneInfo tz) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz).ToString("ddd d MMM HH:mm");

    /// <summary>Serves our own voice clips from this host (relative URL) so playback works even if PublicBaseUrl points at a tunnel.</summary>
    public static string? MediaUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        var i = url.IndexOf("/media/", StringComparison.Ordinal);
        return i >= 0 ? url[i..] : url;
    }

    public static string ChannelIcon(string channel) => channel switch
    {
        Channels.Facebook => "📘",
        Channels.Instagram => "📸",
        Channels.WhatsApp => "💬",
        Channels.Twitter => "✖️",
        Channels.Simulator => "🧪",
        _ => "🔌",
    };

    public static string StatusClass(MessageStatus status) => status switch
    {
        MessageStatus.Failed => "badge danger",
        MessageStatus.Draft => "badge warn",
        MessageStatus.Ignored => "badge muted",
        MessageStatus.Sent => "badge ok",
        _ => "badge",
    };

    public static string AppointmentClass(AppointmentStatus status) => status switch
    {
        AppointmentStatus.Booked => "badge ok",
        AppointmentStatus.EmailFallback => "badge warn",
        AppointmentStatus.Failed => "badge danger",
        _ => "badge muted",
    };

    public static string Truncate(string? text, int max) =>
        string.IsNullOrEmpty(text) ? "" : text.Length <= max ? text : text[..max] + "…";
}
