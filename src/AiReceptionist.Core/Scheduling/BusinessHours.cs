using System.Globalization;
using System.Text.RegularExpressions;

namespace AiReceptionist.Core.Scheduling;

/// <summary>A UTC time interval [StartUtc, EndUtc).</summary>
public readonly record struct TimeSlot(DateTime StartUtc, DateTime EndUtc)
{
    public bool Overlaps(TimeSlot other) => StartUtc < other.EndUtc && other.StartUtc < EndUtc;
}

/// <summary>
/// Weekly opening hours parsed from a human-friendly string such as
/// <c>"Mon-Fri 09:00-12:30,13:30-17:00; Sat 10:00-13:00"</c>. "Daily" means every day.
/// </summary>
public sealed class BusinessHours
{
    private static readonly string[] DayNames = { "sun", "mon", "tue", "wed", "thu", "fri", "sat" };
    private static readonly Regex RangeRegex = new(@"^(\d{1,2}):(\d{2})\s*-\s*(\d{1,2}):(\d{2})$", RegexOptions.Compiled);

    private readonly Dictionary<DayOfWeek, List<(TimeSpan Open, TimeSpan Close)>> _windows = new();

    public IReadOnlyList<(TimeSpan Open, TimeSpan Close)> For(DayOfWeek day) =>
        _windows.TryGetValue(day, out var list) ? list : Array.Empty<(TimeSpan, TimeSpan)>();

    public static bool TryParse(string? text, out BusinessHours hours, out string? error)
    {
        hours = new BusinessHours();
        error = null;
        if (string.IsNullOrWhiteSpace(text)) { error = "Business hours are empty."; return false; }

        foreach (var rawPart in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var space = rawPart.IndexOf(' ');
            if (space < 0) { error = $"'{rawPart}' must look like 'Mon-Fri 09:00-17:00'."; return false; }

            var daysPart = rawPart[..space].Trim().ToLowerInvariant();
            var rangesPart = rawPart[(space + 1)..].Trim();

            if (!TryParseDays(daysPart, out var days)) { error = $"Unknown day range '{daysPart}'."; return false; }

            foreach (var range in rangesPart.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var m = RangeRegex.Match(range);
                if (!m.Success) { error = $"Invalid time range '{range}'."; return false; }
                var open = new TimeSpan(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), 0);
                var close = new TimeSpan(int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture), 0);
                if (close <= open || close > TimeSpan.FromHours(24)) { error = $"Range '{range}' must end after it starts."; return false; }

                foreach (var d in days)
                {
                    if (!hours._windows.TryGetValue(d, out var list)) hours._windows[d] = list = new();
                    list.Add((open, close));
                    list.Sort((a, b) => a.Open.CompareTo(b.Open));
                }
            }
        }
        return true;
    }

    public static BusinessHours Parse(string text) =>
        TryParse(text, out var h, out var err) ? h : throw new FormatException(err);

    private static bool TryParseDays(string text, out List<DayOfWeek> days)
    {
        days = new List<DayOfWeek>();
        if (text is "daily" or "everyday")
        {
            days.AddRange(Enum.GetValues<DayOfWeek>());
            return true;
        }

        foreach (var token in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var ends = token.Split('-');
            var from = IndexOf(ends[0]);
            var to = ends.Length > 1 ? IndexOf(ends[1]) : from;
            if (from < 0 || to < 0 || ends.Length > 2) return false;
            // Walk forward so wrap-around ranges such as "Sat-Mon" work.
            for (var i = from; ; i = (i + 1) % 7)
            {
                days.Add((DayOfWeek)i);
                if (i == to) break;
            }
        }
        return days.Count > 0;
    }

    private static int IndexOf(string token)
    {
        token = token.Trim();
        if (token.Length < 3) return -1;
        return Array.IndexOf(DayNames, token[..3]);
    }
}
