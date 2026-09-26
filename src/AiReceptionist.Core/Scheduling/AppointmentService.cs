using System.Text;
using AiReceptionist.Core.Abstractions;
using AiReceptionist.Core.Domain;
using Microsoft.Extensions.Logging;

namespace AiReceptionist.Core.Scheduling;

public sealed record BookingOutcome(AppointmentStatus Status, BookingMethod Method, string? EventId, string? WebLink, string? Error);

/// <summary>
/// Books appointments in Microsoft 365. If the calendar API is not configured or fails, falls back to
/// emailing a confirmation (with an .ics attachment) to the business owner and, if known, the customer.
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
                return new BookingOutcome(AppointmentStatus.Booked, BookingMethod.Calendar, created.EventId, created.WebLink, null);
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
            var ics = IcsBuilder.Build(request.IdempotencyKey + "@ai-receptionist", request.StartUtc, request.EndUtc,
                request.Subject, request.Body, _email.OwnerAddress, request.CustomerEmail, _time.GetUtcNow().UtcDateTime);

            var body = new StringBuilder()
                .AppendLine($"Appointment confirmed: {SlotFormatter.Friendly(request.StartUtc, tz)} ({tz.Id})")
                .AppendLine($"Duration: {(request.EndUtc - request.StartUtc).TotalMinutes:0} minutes")
                .AppendLine()
                .AppendLine(request.Body)
                .AppendLine()
                .AppendLine("The calendar could not be updated automatically, so this email was sent instead. Open the attached .ics file to add the appointment to your calendar.")
                .ToString();

            await _email.SendAsync(new EmailMessage(Recipients(request.CustomerEmail), request.Subject, body,
                new[] { new EmailAttachment("appointment.ics", "text/calendar", Encoding.UTF8.GetBytes(ics)) }), ct);

            return new BookingOutcome(AppointmentStatus.EmailFallback, BookingMethod.Email, null, null, calendarError);
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
            if (appointment.Method == BookingMethod.Calendar && appointment.ExternalEventId is not null && _calendar.IsConfigured)
            {
                await _calendar.CancelEventAsync(appointment.ExternalEventId, ct);
                return null;
            }

            if (_email.IsConfigured)
            {
                var tz = TimeZoneResolver.Resolve(timeZoneId);
                var ics = IcsBuilder.Build($"conv{appointment.ConversationId}-{appointment.StartUtc:yyyyMMddHHmm}@ai-receptionist",
                    appointment.StartUtc, appointment.EndUtc, appointment.Subject, "Cancelled", _email.OwnerAddress,
                    appointment.CustomerEmail, _time.GetUtcNow().UtcDateTime, cancel: true);
                await _email.SendAsync(new EmailMessage(Recipients(appointment.CustomerEmail),
                    "Cancelled: " + appointment.Subject,
                    $"The appointment on {SlotFormatter.Friendly(appointment.StartUtc, tz)} has been cancelled.",
                    new[] { new EmailAttachment("cancel.ics", "text/calendar", Encoding.UTF8.GetBytes(ics)) }), ct);
                return null;
            }
            return "Neither calendar nor email is configured.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Cancelling appointment {Id} failed.", appointment.Id);
            return ex.Message;
        }
    }

    private List<string> Recipients(string? customerEmail)
    {
        var to = new List<string>();
        if (!string.IsNullOrWhiteSpace(_email.OwnerAddress)) to.Add(_email.OwnerAddress);
        if (!string.IsNullOrWhiteSpace(customerEmail) && !to.Contains(customerEmail, StringComparer.OrdinalIgnoreCase)) to.Add(customerEmail);
        return to;
    }
}
