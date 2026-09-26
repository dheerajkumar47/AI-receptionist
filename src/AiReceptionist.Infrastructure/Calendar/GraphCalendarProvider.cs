using System.Globalization;
using AiReceptionist.Core.Abstractions;
using AiReceptionist.Core.Scheduling;
using Azure.Identity;
using Microsoft.Extensions.Options;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using TimeSlot = AiReceptionist.Core.Scheduling.TimeSlot;

namespace AiReceptionist.Infrastructure.Calendar;

/// <summary>
/// Microsoft 365 calendar via Microsoft Graph using app-only (client credentials) auth.
/// Requires the Entra ID app permission Calendars.ReadWrite (admin consented); restrict it to the
/// receptionist mailbox with an Exchange application access policy (see docs/SETUP.md).
/// </summary>
public sealed class GraphCalendarProvider : ICalendarProvider
{
    private readonly GraphCalendarOptions _options;
    private readonly Lazy<GraphServiceClient> _client;

    public GraphCalendarProvider(IOptions<GraphCalendarOptions> options)
    {
        _options = options.Value;
        _client = new Lazy<GraphServiceClient>(() => new GraphServiceClient(
            new ClientSecretCredential(_options.TenantId, _options.ClientId, _options.ClientSecret),
            new[] { "https://graph.microsoft.com/.default" }));
    }

    public bool IsConfigured => _options.IsConfigured;

    private Microsoft.Graph.Users.Item.UserItemRequestBuilder Mailbox => _client.Value.Users[_options.CalendarUser];

    public async Task<IReadOnlyList<TimeSlot>> GetBusyAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var busy = new List<TimeSlot>();
        var page = await Mailbox.CalendarView.GetAsync(r =>
        {
            r.QueryParameters.StartDateTime = fromUtc.ToString("o", CultureInfo.InvariantCulture);
            r.QueryParameters.EndDateTime = toUtc.ToString("o", CultureInfo.InvariantCulture);
            r.QueryParameters.Select = new[] { "start", "end", "showAs", "isCancelled" };
            r.QueryParameters.Top = 250;
            r.Headers.Add("Prefer", "outlook.timezone=\"UTC\"");
        }, ct);

        while (page?.Value is not null)
        {
            foreach (var e in page.Value)
            {
                if (e.IsCancelled == true || e.ShowAs == FreeBusyStatus.Free) continue;
                if (TryParse(e.Start, out var s) && TryParse(e.End, out var en)) busy.Add(new TimeSlot(s, en));
            }
            if (page.OdataNextLink is null) break;
            page = await Mailbox.CalendarView.WithUrl(page.OdataNextLink).GetAsync(r =>
                r.Headers.Add("Prefer", "outlook.timezone=\"UTC\""), ct);
        }
        return busy;
    }

    public async Task<CalendarEventResult> CreateEventAsync(BookingRequest request, CancellationToken ct)
    {
        var ev = new Event
        {
            Subject = request.Subject,
            Body = new ItemBody { ContentType = BodyType.Text, Content = request.Body },
            Start = new DateTimeTimeZone { DateTime = request.StartUtc.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture), TimeZone = "UTC" },
            End = new DateTimeTimeZone { DateTime = request.EndUtc.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture), TimeZone = "UTC" },
            ShowAs = FreeBusyStatus.Busy,
            IsReminderOn = true,
            ReminderMinutesBeforeStart = 15,
            Categories = new List<string> { "AI Receptionist" },
            // Graph de-duplicates POSTs with the same transactionId, so a retried booking never creates two events.
            TransactionId = request.IdempotencyKey,
        };

        if (_options.InviteCustomer && !string.IsNullOrWhiteSpace(request.CustomerEmail))
        {
            ev.Attendees = new List<Attendee>
            {
                new()
                {
                    Type = AttendeeType.Required,
                    EmailAddress = new EmailAddress { Address = request.CustomerEmail, Name = request.CustomerName ?? request.CustomerEmail },
                },
            };
        }

        var created = await Mailbox.Events.PostAsync(ev, cancellationToken: ct)
                      ?? throw new InvalidOperationException("Graph returned no event.");
        return new CalendarEventResult(created.Id!, created.WebLink);
    }

    public Task CancelEventAsync(string eventId, CancellationToken ct) =>
        Mailbox.Events[eventId].DeleteAsync(cancellationToken: ct);

    private static bool TryParse(DateTimeTimeZone? value, out DateTime utc)
    {
        utc = default;
        if (value?.DateTime is null) return false;
        if (!DateTime.TryParse(value.DateTime, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)) return false;
        utc = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        return true;
    }
}
