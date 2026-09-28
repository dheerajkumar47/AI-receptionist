using System.Text;
using AiReceptionist.Core.Abstractions;
using AiReceptionist.Core.Domain;
using Microsoft.Extensions.Logging;

namespace AiReceptionist.Core.Scheduling;

public sealed record BookingOutcome(AppointmentStatus Status, BookingMethod Method, string? EventId, string? WebLink, string? Error);

/// <summary>
/// Books appointments in Microsoft 365. If the calendar API is not configured or fails, falls back to
/// emailing the business owner (with the chat summary and an .ics attachment).
/// Either way the customer, if they gave an email, receives a clean confirmation email with an .ics attachment.
/// </summary>
public sealed class AppointmentService
{
    private readonly ICalendarProvider _calendar;
    private readonly IEmailSender _email;
    private readonly TimeProvider _time;
    private readonly ILogger<AppointmentService> _log;

    public AppointmentService(ICalendarProvider calendar, IEmailSender email, TimeProvider time, ILogger<AppointmentService> log)
    {
        _calendar = calendar;
        _email = email;
        _time = time;
        _log = log;
    }

    public async Task<BookingOutcome> BookAsync(BookingRequest request, CancellationToken ct)
    {
        string calendarError;
        if (_calendar.IsConfigured)
        {
            try
            {
                var created = await _calendar.CreateEventAsync(request, ct);
                _log.LogInformation("Created calendar event {EventId} at {Start:o}", created.EventId, request.StartUtc);
                var notifyError = await TryConfirmToCustomerAsync(request, ct);
                return new BookingOutcome(AppointmentStatus.Booked, BookingMethod.Calendar, created.EventId, created.WebLink, notifyError);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "Calendar booking failed; falling back to email confirmation.");
                calendarError = "Calendar error: " + ex.Message;
            }
        }
        else
        {
            calendarError = "Calendar not configured.";
        }

        if (!_email.IsConfigured)
            return new BookingOutcome(AppointmentStatus.Failed, BookingMethod.None, null, null, calendarError + " Email fallback not configured.");

        try
        {
            var tz = TimeZoneResolver.Resolve(request.TimeZoneId);
            var ics = IcsBuilder.Build(Uid(request.IdempotencyKey), request.StartUtc, request.EndUtc,
                request.Subject, request.Body, _email.OwnerAddress, request.CustomerEmail, _time.GetUtcNow().UtcDateTime);

            var body = new StringBuilder()
                .AppendLine($"Appointment confirmed: {SlotFormatter.Friendly(request.StartUtc, tz)} ({tz.Id})")
                .AppendLine($"Duration: {(request.EndUtc - request.StartUtc).TotalMinutes:0} minutes")
                .AppendLine()
                .AppendLine(request.Body)
                .AppendLine()
                .AppendLine("The calendar could not be updated automatically, so this email was sent instead. Open the attached .ics file to add the appointment to your calendar.")
                .ToString();

            if (OwnerOnly().Count > 0)
                await _email.SendAsync(new EmailMessage(OwnerOnly(), request.Subject, body,
                    new[] { new EmailAttachment("appointment.ics", "text/calendar", Encoding.UTF8.GetBytes(ics)) }), ct);

            var notifyError = await TryConfirmToCustomerAsync(request, ct);
            return new BookingOutcome(AppointmentStatus.EmailFallback, BookingMethod.Email, null, null,
                notifyError is null ? calendarError : calendarError + " " + notifyError);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Email fallback failed.");
            return new BookingOutcome(AppointmentStatus.Failed, BookingMethod.None, null, null, calendarError + " Email error: " + ex.Message);
        }
    }

    /// <summary>Removes the calendar event, or emails a cancellation notice for email-fallback bookings.</summary>
    public async Task<string?> CancelAsync(Appointment appointment, string timeZoneId, CancellationToken ct)
    {
        try
        {
            var removedFromCalendar = false;
            if (appointment.Method == BookingMethod.Calendar && appointment.ExternalEventId is not null && _calendar.IsConfigured)
            {
                await _calendar.CancelEventAsync(appointment.ExternalEventId, ct);
                removedFromCalendar = true;
            }

            if (!_email.IsConfigured)
                return removedFromCalendar ? null : "Neither calendar nor email is configured.";

            var tz = TimeZoneResolver.Resolve(timeZoneId);
            var when = SlotFormatter.Friendly(appointment.StartUtc, tz);
            // Same UID as the confirmation's .ics, so calendars that imported it remove the entry.
            var ics = IcsBuilder.Build(Uid($"conv{appointment.ConversationId}-{appointment.StartUtc:yyyyMMddHHmm}"),
                appointment.StartUtc, appointment.EndUtc, appointment.Subject, "Cancelled", _email.OwnerAddress,
                appointment.CustomerEmail, _time.GetUtcNow().UtcDateTime, cancel: true);
            var attachment = new[] { new EmailAttachment("cancel.ics", "text/calendar", Encoding.UTF8.GetBytes(ics)) };

            if (!removedFromCalendar && OwnerOnly().Count > 0)
                await _email.SendAsync(new EmailMessage(OwnerOnly(), "Cancelled: " + appointment.Subject,
                    $"The appointment on {when} ({tz.Id}) has been cancelled.", attachment), ct);

            if (IsCustomerAddress(appointment.CustomerEmail))
                await _email.SendAsync(new EmailMessage(new[] { appointment.CustomerEmail! }, $"Cancelled: your appointment on {when}",
                    $"Hi {appointment.CustomerName ?? "there"},\n\nYour appointment on {when} ({tz.Id}) has been cancelled.\n" +
                    "If you would like a new time, just message us again.\n", attachment), ct);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Cancelling appointment {Id} failed.", appointment.Id);
            return ex.Message;
        }
    }

    /// <summary>Emails the appointment reminder to the customer, used when the chat platform can't deliver it.
    /// Returns null on success, otherwise the reason it was not sent.</summary>
    public async Task<string?> SendReminderEmailAsync(Appointment appointment, string text, string businessName, CancellationToken ct)
    {
        if (!IsCustomerAddress(appointment.CustomerEmail)) return "No reminder email: the customer's email is unknown.";
        if (!_email.IsConfigured) return "No reminder email: email (SMTP) is not configured.";
        try
        {
            await _email.SendAsync(new EmailMessage(new[] { appointment.CustomerEmail! },
                $"Reminder: your appointment with {businessName}", text + "\n"), ct);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Reminder email failed.");
            return "Reminder email failed: " + ex.Message;
        }
    }

    /// <summary>
    /// Sends the customer a short confirmation (no internal notes or chat transcript) with an .ics file.
    /// Returns an error note for the dashboard if it could not be sent; a failure never undoes the booking.
    /// </summary>
    private async Task<string?> TryConfirmToCustomerAsync(BookingRequest request, CancellationToken ct)
    {
        if (!IsCustomerAddress(request.CustomerEmail)) return null;
        if (!_email.IsConfigured) return "No confirmation email sent to the customer: email (SMTP) is not configured.";

        try
        {
            var tz = TimeZoneResolver.Resolve(request.TimeZoneId);
            var when = SlotFormatter.Friendly(request.StartUtc, tz);
            var business = string.IsNullOrWhiteSpace(request.BusinessName) ? "us" : request.BusinessName;
            var title = string.IsNullOrWhiteSpace(request.BusinessName) ? "Appointment" : $"Appointment with {request.BusinessName}";
            var minutes = (request.EndUtc - request.StartUtc).TotalMinutes;

            var ics = IcsBuilder.Build(Uid(request.IdempotencyKey), request.StartUtc, request.EndUtc, title,
                $"{minutes:0}-minute appointment with {business}.", _email.OwnerAddress, request.CustomerEmail,
                _time.GetUtcNow().UtcDateTime);

            var body = new StringBuilder()
                .AppendLine($"Hi {request.CustomerName ?? "there"},")
                .AppendLine()
                .AppendLine($"Your appointment with {business} is confirmed.")
                .AppendLine()
                .AppendLine($"When: {when} ({tz.Id})")
                .AppendLine($"Duration: {minutes:0} minutes")
                .AppendLine()
                .AppendLine("Open the attached appointment.ics file to add it to your calendar.")
                .AppendLine("To reschedule or cancel, just reply to us in the same chat.")
                .AppendLine()
                .AppendLine("Thank you,")
                .AppendLine(string.IsNullOrWhiteSpace(request.BusinessName) ? "The team" : request.BusinessName)
                .ToString();

            await _email.SendAsync(new EmailMessage(new[] { request.CustomerEmail! }, $"Confirmed: {title} on {when}", body,
                new[] { new EmailAttachment("appointment.ics", "text/calendar", Encoding.UTF8.GetBytes(ics)) }), ct);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Confirmation email to the customer failed.");
            return "Confirmation email to the customer failed: " + ex.Message;
        }
    }

    private bool IsCustomerAddress(string? email) =>
        !string.IsNullOrWhiteSpace(email) && !string.Equals(email, _email.OwnerAddress, StringComparison.OrdinalIgnoreCase);

    private IReadOnlyList<string> OwnerOnly() =>
        string.IsNullOrWhiteSpace(_email.OwnerAddress) ? Array.Empty<string>() : new[] { _email.OwnerAddress };

    private static string Uid(string key) => key + "@ai-receptionist";
}
