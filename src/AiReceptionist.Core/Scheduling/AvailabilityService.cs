using AiReceptionist.Core.Abstractions;
using AiReceptionist.Core.Data;
using AiReceptionist.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AiReceptionist.Core.Scheduling;

/// <summary>Snapshot of what can be booked right now.</summary>
public sealed class Availability
{
    private readonly BusinessHours _hours;
    private readonly IReadOnlyList<TimeSlot> _busy;
    private readonly BotSettings _settings;

    internal Availability(BotSettings settings, BusinessHours hours, TimeZoneInfo tz, DateTime nowUtc,
        IReadOnlyList<TimeSlot> busy, IReadOnlyList<TimeSlot> openSlots, bool calendarChecked)
    {
        _settings = settings;
        _hours = hours;
        _busy = busy;
        TimeZone = tz;
        NowUtc = nowUtc;
        OpenSlots = openSlots;
        CalendarChecked = calendarChecked;
    }

    public TimeZoneInfo TimeZone { get; }
    public DateTime NowUtc { get; }
    public IReadOnlyList<TimeSlot> OpenSlots { get; }

    /// <summary>False if the Microsoft 365 calendar could not be read (slots then only exclude our own bookings).</summary>
    public bool CalendarChecked { get; }

    public TimeSpan Duration => TimeSpan.FromMinutes(_settings.AppointmentMinutes);

    public bool IsBookable(DateTime startUtc) =>
        SlotCalculator.IsBookable(startUtc, _hours, TimeZone, NowUtc, Duration, _settings.BookingHorizonDays,
            TimeSpan.FromMinutes(_settings.MinLeadMinutes), _busy);

    /// <summary>Open slots closest to <paramref name="targetUtc"/> (or the earliest ones).</summary>
    public IReadOnlyList<TimeSlot> Nearest(DateTime? targetUtc, int count) =>
        (targetUtc is { } t ? OpenSlots.OrderBy(s => Math.Abs((s.StartUtc - t).Ticks)) : OpenSlots.AsEnumerable())
            .Take(count).OrderBy(s => s.StartUtc).ToList();
}

/// <summary>Combines opening hours, Microsoft 365 free/busy and appointments already in the database.</summary>
public sealed class AvailabilityService
{
    private readonly ICalendarProvider _calendar;
    private readonly IDbContextFactory<ReceptionistDbContext> _dbFactory;
    private readonly TimeProvider _time;
    private readonly ILogger<AvailabilityService> _log;

    public AvailabilityService(ICalendarProvider calendar, IDbContextFactory<ReceptionistDbContext> dbFactory,
        TimeProvider time, ILogger<AvailabilityService> log)
    {
        _calendar = calendar;
        _dbFactory = dbFactory;
        _time = time;
        _log = log;
    }

    public async Task<Availability> GetAsync(BotSettings settings, CancellationToken ct)
    {
        var tz = TimeZoneResolver.Resolve(settings.TimeZoneId);
        if (!BusinessHours.TryParse(settings.BusinessHours, out var hours, out var error))
        {
            _log.LogWarning("Invalid business hours '{Hours}': {Error}. Using Mon-Fri 09:00-17:00.", settings.BusinessHours, error);
            hours = BusinessHours.Parse("Mon-Fri 09:00-17:00");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var to = now.AddDays(settings.BookingHorizonDays + 2);
        var busy = new List<TimeSlot>();
        var calendarChecked = false;

        if (_calendar.IsConfigured)
        {
            try
            {
                busy.AddRange(await _calendar.GetBusyAsync(now, to, ct));
                calendarChecked = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Could not read calendar free/busy; offering slots from opening hours only.");
            }
        }

        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            var booked = await db.Appointments
                .Where(a => a.EndUtc > now && a.StartUtc < to &&
                            (a.Status == AppointmentStatus.Booked || a.Status == AppointmentStatus.EmailFallback))
                .Select(a => new { a.StartUtc, a.EndUtc })
                .ToListAsync(ct);
            busy.AddRange(booked.Select(a => new TimeSlot(a.StartUtc, a.EndUtc)));
        }

        var slots = SlotCalculator.GetOpenSlots(hours, tz, now, TimeSpan.FromMinutes(settings.AppointmentMinutes),
            settings.BookingHorizonDays, TimeSpan.FromMinutes(settings.MinLeadMinutes), busy, max: 5000);

        return new Availability(settings, hours, tz, now, busy, slots, calendarChecked);
    }
}
