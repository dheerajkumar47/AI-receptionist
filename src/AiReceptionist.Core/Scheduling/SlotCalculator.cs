using System.Globalization;

namespace AiReceptionist.Core.Scheduling;

/// <summary>Pure functions that turn opening hours + busy time into bookable slots.</summary>
public static class SlotCalculator
{
    /// <summary>Enumerates free slots of <paramref name="duration"/> aligned to the start of each opening window.</summary>
    public static IReadOnlyList<TimeSlot> GetOpenSlots(
        BusinessHours hours,
        TimeZoneInfo tz,
        DateTime nowUtc,
        TimeSpan duration,
        int horizonDays,
        TimeSpan minLead,
        IEnumerable<TimeSlot> busy,
        int max = 200)
    {
        var busyList = busy.ToList();
        var earliest = nowUtc + minLead;
        var todayLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz).Date;
        var result = new List<TimeSlot>();

        for (var day = 0; day <= horizonDays && result.Count < max; day++)
        {
            var date = todayLocal.AddDays(day);
            foreach (var (open, close) in hours.For(date.DayOfWeek))
            {
                for (var start = open; start + duration <= close; start += duration)
                {
                    if (!TryToUtc(date + start, tz, out var startUtc)) continue;
                    var slot = new TimeSlot(startUtc, startUtc + duration);
                    if (slot.StartUtc < earliest) continue;
                    if (busyList.Any(b => b.Overlaps(slot))) continue;
                    result.Add(slot);
                    if (result.Count >= max) return result;
                }
            }
        }
        return result;
    }

    /// <summary>True if [startUtc, startUtc + duration) lies inside opening hours, respects lead time and is free.</summary>
    public static bool IsBookable(
        DateTime startUtc,
        BusinessHours hours,
        TimeZoneInfo tz,
        DateTime nowUtc,
        TimeSpan duration,
        int horizonDays,
        TimeSpan minLead,
        IEnumerable<TimeSlot> busy)
    {
        if (startUtc < nowUtc + minLead) return false;
        if (startUtc > nowUtc.AddDays(horizonDays + 1)) return false;

        var local = TimeZoneInfo.ConvertTimeFromUtc(startUtc, tz);
        var tod = local.TimeOfDay;
        var inHours = hours.For(local.DayOfWeek).Any(w => tod >= w.Open && tod + duration <= w.Close);
        if (!inHours) return false;

        var slot = new TimeSlot(startUtc, startUtc + duration);
        return !busy.Any(b => b.Overlaps(slot));
    }

    private static bool TryToUtc(DateTime local, TimeZoneInfo tz, out DateTime utc)
    {
        utc = default;
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (tz.IsInvalidTime(unspecified)) return false; // skipped by a DST jump
        utc = TimeZoneInfo.ConvertTimeToUtc(unspecified, tz);
        return true;
    }
}

/// <summary>Formatting helpers so the LLM, templates and dashboard all show times the same way.</summary>
public static class SlotFormatter
{
    /// <summary>Human-friendly local time, e.g. "Tue 29 Sep, 2:00 PM".</summary>
    public static string Friendly(DateTime utc, TimeZoneInfo tz) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz)
            .ToString("ddd d MMM, h:mm tt", CultureInfo.InvariantCulture);

    /// <summary>ISO-8601 local time with offset, e.g. "2026-09-29T14:00:00+01:00".</summary>
    public static string Iso(DateTime utc, TimeZoneInfo tz)
    {
        var utcKind = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        var offset = tz.GetUtcOffset(utcKind);
        return new DateTimeOffset(utcKind).ToOffset(offset).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
    }

    /// <summary>Parses an ISO timestamp. Without an offset it is interpreted in the business time zone.</summary>
    public static DateTime? ParseToUtc(string? value, TimeZoneInfo tz)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();

        var hasOffset = value.EndsWith('Z') || System.Text.RegularExpressions.Regex.IsMatch(value, @"[+-]\d{2}:?\d{2}$");
        if (hasOffset && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
            return dto.UtcDateTime;

        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            if (tz.IsInvalidTime(unspecified)) return null;
            return TimeZoneInfo.ConvertTimeToUtc(unspecified, tz);
        }
        return null;
    }
}

public static class TimeZoneResolver
{
    /// <summary>Resolves IANA or Windows ids on any OS; falls back to UTC for unknown ids.</summary>
    public static TimeZoneInfo Resolve(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return TimeZoneInfo.Utc;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception) { /* try conversion below */ }

        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var win))
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(win); } catch (Exception) { }
        }
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var iana))
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(iana); } catch (Exception) { }
        }
        return TimeZoneInfo.Utc;
    }

    public static bool IsKnown(string? id) =>
        id is not null && (id == "UTC" || Resolve(id) != TimeZoneInfo.Utc);
}
