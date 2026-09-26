using System.Globalization;
using AiReceptionist.Core.Abstractions;
using AiReceptionist.Core.Scheduling;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using TimeSlot = AiReceptionist.Core.Scheduling.TimeSlot;

namespace AiReceptionist.Infrastructure.Calendar;

/// <summary>
/// Outlook calendar via Microsoft Graph, in one of two sign-in modes:
/// <list type="bullet">
/// <item><b>App</b> (Microsoft 365 business): app-only client credentials with the application permission
/// Calendars.ReadWrite, writing to <see cref="GraphCalendarOptions.CalendarUser"/>'s mailbox.</item>
/// <item><b>Personal</b> (Outlook.com / any Microsoft account): delegated Calendars.ReadWrite. The owner signs in once
/// with a device code (shown in the log and on the dashboard); the refresh token is cached so restarts are silent.</item>
/// </list>
/// </summary>
public sealed class GraphCalendarProvider : ICalendarProvider
{
    private static readonly string[] DelegatedScopes = { "Calendars.ReadWrite" };
    private const string PreferUtc = "outlook.timezone=\"UTC\"";

    private readonly GraphCalendarOptions _options;
    private readonly ILogger<GraphCalendarProvider> _log;
    private readonly GraphServiceClient _client;
    private readonly DeviceCodeCredential? _personalCredential;
    private readonly string? _recordPath;

    public GraphCalendarProvider(IOptions<GraphCalendarOptions> options, ILogger<GraphCalendarProvider> log)
    {
        _options = options.Value;
        _log = log;

        if (!_options.IsPersonal)
        {
            _client = new GraphServiceClient(
                new ClientSecretCredential(_options.TenantId, _options.ClientId, _options.ClientSecret),
                new[] { "https://graph.microsoft.com/.default" });
            IsSignedIn = true;
            return;
        }

        _recordPath = Path.GetFullPath(_options.AuthRecordPath);
        var record = LoadRecord(_recordPath);
        _personalCredential = new DeviceCodeCredential(new DeviceCodeCredentialOptions
        {
            TenantId = string.IsNullOrWhiteSpace(_options.TenantId) ? "consumers" : _options.TenantId,
            ClientId = _options.ClientId,
            AuthenticationRecord = record,
            // Never block a customer message on an interactive prompt; sign-in only happens via SignInAsync.
            DisableAutomaticAuthentication = true,
            TokenCachePersistenceOptions = new TokenCachePersistenceOptions
            {
                Name = "ai-receptionist-graph",
                UnsafeAllowUnencryptedStorage = true, // only used where the OS has no protected storage
            },
            DeviceCodeCallback = (info, _) =>
            {
                SignInPrompt = info.Message;
                _log.LogWarning("Outlook calendar sign-in required: {Message}", info.Message);
                return Task.CompletedTask;
            },
        });
        IsSignedIn = record is not null;
        _client = new GraphServiceClient(_personalCredential, DelegatedScopes);
    }

    public bool IsConfigured => _options.IsConfigured;

    /// <summary>True when the calendar is usable (always true in App mode).</summary>
    public bool IsSignedIn { get; private set; }

    /// <summary>Personal mode only: "To sign in, open https://microsoft.com/devicelogin and enter the code ...".</summary>
    public string? SignInPrompt { get; private set; }

    public bool NeedsInteractiveSignIn => _personalCredential is not null && !IsSignedIn;

    /// <summary>Personal mode: runs the device-code sign-in and remembers the account. Completes when the owner has signed in.</summary>
    public async Task SignInAsync(CancellationToken ct)
    {
        if (_personalCredential is null || IsSignedIn) return;

        var record = await _personalCredential.AuthenticateAsync(new TokenRequestContext(DelegatedScopes), ct);
        Directory.CreateDirectory(Path.GetDirectoryName(_recordPath)!);
        await using (var file = File.Create(_recordPath!))
            await record.SerializeAsync(file, ct);

        IsSignedIn = true;
        SignInPrompt = null;
        _log.LogInformation("Outlook calendar connected as {User}.", record.Username);
    }

    public Task<IReadOnlyList<TimeSlot>> GetBusyAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct) => Guard(async () =>
    {
        var start = fromUtc.ToString("o", CultureInfo.InvariantCulture);
        var end = toUtc.ToString("o", CultureInfo.InvariantCulture);
        var fields = new[] { "start", "end", "showAs", "isCancelled" };

        var page = _options.IsPersonal
            ? await _client.Me.CalendarView.GetAsync(r =>
            {
                r.QueryParameters.StartDateTime = start;
                r.QueryParameters.EndDateTime = end;
                r.QueryParameters.Select = fields;
                r.QueryParameters.Top = 250;
                r.Headers.Add("Prefer", PreferUtc);
            }, ct)
            : await _client.Users[_options.CalendarUser].CalendarView.GetAsync(r =>
            {
                r.QueryParameters.StartDateTime = start;
                r.QueryParameters.EndDateTime = end;
                r.QueryParameters.Select = fields;
                r.QueryParameters.Top = 250;
                r.Headers.Add("Prefer", PreferUtc);
            }, ct);

        var busy = new List<TimeSlot>();
        while (page?.Value is not null)
        {
            foreach (var e in page.Value)
            {
                if (e.IsCancelled == true || e.ShowAs == FreeBusyStatus.Free) continue;
                if (TryParse(e.Start, out var s) && TryParse(e.End, out var en)) busy.Add(new TimeSlot(s, en));
            }
            if (page.OdataNextLink is null) break;
            page = _options.IsPersonal
                ? await _client.Me.CalendarView.WithUrl(page.OdataNextLink).GetAsync(r => r.Headers.Add("Prefer", PreferUtc), ct)
                : await _client.Users[_options.CalendarUser].CalendarView.WithUrl(page.OdataNextLink).GetAsync(r => r.Headers.Add("Prefer", PreferUtc), ct);
        }
        return (IReadOnlyList<TimeSlot>)busy;
    });

    public Task<CalendarEventResult> CreateEventAsync(BookingRequest request, CancellationToken ct) => Guard(async () =>
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

        var created = (_options.IsPersonal
                          ? await _client.Me.Events.PostAsync(ev, cancellationToken: ct)
                          : await _client.Users[_options.CalendarUser].Events.PostAsync(ev, cancellationToken: ct))
                      ?? throw new InvalidOperationException("Graph returned no event.");
        return new CalendarEventResult(created.Id!, created.WebLink);
    });

    public Task CancelEventAsync(string eventId, CancellationToken ct) => Guard(async () =>
    {
        if (_options.IsPersonal) await _client.Me.Events[eventId].DeleteAsync(cancellationToken: ct);
        else await _client.Users[_options.CalendarUser].Events[eventId].DeleteAsync(cancellationToken: ct);
        return true;
    });

    /// <summary>Turns "not signed in" into a clear error (so bookings fall back to email) and notices expired sign-ins.</summary>
    private async Task<T> Guard<T>(Func<Task<T>> call)
    {
        if (NeedsInteractiveSignIn)
            throw new InvalidOperationException("Outlook calendar is not signed in yet. " + (SignInPrompt ?? "See the dashboard for the sign-in code."));
        try
        {
            return await call();
        }
        catch (AuthenticationRequiredException ex)
        {
            _log.LogWarning(ex, "Outlook calendar token could not be refreshed silently: {Reason}", ex.InnerException?.Message ?? ex.Message);
            IsSignedIn = false; // the sign-in worker will show a new device code
            throw new InvalidOperationException("Outlook calendar sign-in expired; sign in again with the code on the dashboard.", ex);
        }
    }

    private static AuthenticationRecord? LoadRecord(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var file = File.OpenRead(path);
            return AuthenticationRecord.Deserialize(file);
        }
        catch (Exception)
        {
            return null; // corrupt file: sign in again
        }
    }

    private static bool TryParse(DateTimeTimeZone? value, out DateTime utc)
    {
        utc = default;
        if (value?.DateTime is null) return false;
        if (!DateTime.TryParse(value.DateTime, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)) return false;
        utc = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        return true;
    }
}
