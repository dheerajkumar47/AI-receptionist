using System.Globalization;
using System.Text.RegularExpressions;
using AiReceptionist.Core.Abstractions;
using AiReceptionist.Core.Domain;
using AiReceptionist.Core.Scheduling;

namespace AiReceptionist.Core.Conversations;

/// <summary>
/// Deterministic, offline intent engine based on each intent's example phrases and template replies.
/// Used automatically when no LLM is configured, so the whole pipeline (channels, voice, booking) can be
/// exercised without an OpenAI key. It understands simple time preferences ("tomorrow afternoon", "friday at 3pm").
/// </summary>
public sealed class KeywordIntentEngine : IIntentEngine
{
    private static readonly Regex Affirmative = new(
        @"^\s*(yes|yeah|yep|yup|sure|ok|okay|confirm(ed)?|perfect|great|sounds good|that works|works for me|book it|please do|go ahead|absolutely|definitely)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex EmailRegex = new(@"[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NameRegex = new(@"\b(?i:my name is|i am|i'm)\s+([A-Z][a-z]+(?:\s+[A-Z][a-z]+)?)", RegexOptions.Compiled);
    private static readonly Regex HourRegex = new(@"\b(\d{1,2})(?::(\d{2}))?\s*(am|pm)?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Word = new(@"[a-z']+", RegexOptions.Compiled);

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "you", "your", "can", "are", "with", "please", "what", "have", "want", "like", "would", "there", "this", "that",
    };

    public string Name => "Offline keyword engine";

    public Task<IntentResult> AnalyzeAsync(IntentContext ctx, CancellationToken ct)
    {
        var text = ctx.UserMessage.Trim();
        var lower = text.ToLowerInvariant();
        var email = EmailRegex.Match(text) is { Success: true } em ? em.Value : null;
        var name = NameRegex.Match(text) is { Success: true } nm ? nm.Groups[1].Value : null;
        var customerName = name ?? ctx.CustomerName;

        var confirm = ctx.Intents.FirstOrDefault(i => i.Action == IntentAction.ConfirmAppointment);
        if (ctx.PendingSlotUtc is not null && confirm is not null && Affirmative.IsMatch(lower))
            return Task.FromResult(new IntentResult(confirm.Name, 0.9, "", null, true, name, email));

        var (best, score) = ctx.Intents
            .Where(i => i.Action != IntentAction.ConfirmAppointment || ctx.PendingSlotUtc is not null)
            .Select(i => (Intent: i, Score: Score(i, lower)))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Intent.SortOrder)
            .FirstOrDefault();

        if (best is null || score < 0.25)
        {
            best = ctx.Intents.FirstOrDefault(i => i.Name.Equals("Other", StringComparison.OrdinalIgnoreCase)) ?? best;
            score = 0.5;
        }
        if (best is null)
            return Task.FromResult(new IntentResult("Other", 0.5, "Thanks for your message! A member of our team will reply shortly.", null, false, name, email));

        DateTime? proposed = null;
        if (best.Action == IntentAction.ProposeAppointment)
            proposed = PickSlot(lower, ctx);

        var values = new Dictionary<string, string?>
        {
            ["business"] = ctx.Settings.BusinessName,
            ["name"] = customerName?.Split(' ')[0],
            ["hours"] = ctx.Settings.BusinessHours,
            ["channel"] = ctx.Channel,
            ["slot"] = proposed is { } p ? SlotFormatter.Friendly(p, ctx.TimeZone) : "",
            ["slots"] = string.Join(", ", ctx.OpenSlots.Take(3).Select(s => SlotFormatter.Friendly(s.StartUtc, ctx.TimeZone))),
        };

        string reply;
        if (best.Action == IntentAction.ProposeAppointment && proposed is null)
            reply = "Sorry, we don't have any openings in the next few days. A team member will contact you to find a time.";
        else if (!string.IsNullOrWhiteSpace(best.TemplateReply))
            reply = TemplateRenderer.Render(best.TemplateReply, values);
        else if (best.Action == IntentAction.ProposeAppointment)
            reply = TemplateRenderer.Render("I can offer {slot}. Shall I book that for you?", values);
        else
            reply = TemplateRenderer.Render("Thanks for your message, {name}! How can I help?", values);

        return Task.FromResult(new IntentResult(best.Name, Math.Round(score, 2), reply, proposed, false, name, email));
    }

    private static double Score(IntentDefinition intent, string lower)
    {
        var messageWords = Word.Matches(lower).Select(m => m.Value).Where(w => w.Length > 2 && !StopWords.Contains(w)).ToHashSet();
        double best = 0;
        foreach (var example in intent.Examples.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var ex = example.ToLowerInvariant();
            if (Regex.IsMatch(lower, $@"(^|\W){Regex.Escape(ex)}($|\W)"))
            {
                // Longer phrase matches are stronger evidence than "hi".
                best = Math.Max(best, 0.75 + Math.Min(0.2, ex.Length / 100.0));
                continue;
            }

            var exWords = Word.Matches(ex).Select(m => m.Value).Where(w => w.Length > 2 && !StopWords.Contains(w)).ToList();
            if (exWords.Count == 0) continue;
            var overlap = exWords.Count(messageWords.Contains) / (double)exWords.Count;
            best = Math.Max(best, overlap * 0.7);
        }
        return best;
    }

    /// <summary>Chooses the first open slot matching any day / part-of-day / hour preference in the message.</summary>
    private static DateTime? PickSlot(string lower, IntentContext ctx)
    {
        var candidates = ctx.OpenSlots.Select(s => (Utc: s.StartUtc, Local: TimeZoneInfo.ConvertTimeFromUtc(s.StartUtc, ctx.TimeZone))).ToList();
        if (candidates.Count == 0) return null;

        var today = TimeZoneInfo.ConvertTimeFromUtc(ctx.NowUtc, ctx.TimeZone).Date;

        Func<DateTime, bool>? dayFilter = null;
        if (lower.Contains("tomorrow")) dayFilter = d => d.Date == today.AddDays(1);
        else if (lower.Contains("today")) dayFilter = d => d.Date == today;
        else
        {
            var day = Enum.GetValues<DayOfWeek>().Cast<DayOfWeek?>()
                .FirstOrDefault(d => lower.Contains(d!.Value.ToString().ToLowerInvariant()) ||
                                     Regex.IsMatch(lower, $@"\b{d.Value.ToString()[..3].ToLowerInvariant()}\b"));
            if (day is not null) dayFilter = d => d.DayOfWeek == day;
            else if (lower.Contains("next week")) dayFilter = d => d.Date > today.AddDays(7 - (int)today.DayOfWeek);
        }

        Func<DateTime, bool>? partOfDay = null;
        if (lower.Contains("morning")) partOfDay = d => d.Hour < 12;
        else if (lower.Contains("afternoon")) partOfDay = d => d.Hour is >= 12 and < 17;
        else if (lower.Contains("evening")) partOfDay = d => d.Hour >= 17;

        TimeSpan? clock = null;
        var hourMatch = HourRegex.Matches(lower).FirstOrDefault(m => m.Groups[3].Success || m.Groups[2].Success);
        if (hourMatch is not null)
        {
            var hour = int.Parse(hourMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            var minute = hourMatch.Groups[2].Success ? int.Parse(hourMatch.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
            var ampm = hourMatch.Groups[3].Value.ToLowerInvariant();
            if (ampm == "pm" && hour < 12) hour += 12;
            if (ampm == "am" && hour == 12) hour = 0;
            if (hour < 24 && minute < 60) clock = new TimeSpan(hour, minute, 0);
        }

        // Apply every preference; if nothing matches (e.g. "tomorrow" is a closed day), relax the day first,
        // keeping the preferred time of day, then relax everything.
        IEnumerable<(DateTime Utc, DateTime Local)> Apply(bool useDay, bool useTime)
        {
            var q = candidates.AsEnumerable();
            if (useDay && dayFilter is not null) q = q.Where(c => dayFilter(c.Local));
            if (useTime && partOfDay is not null) q = q.Where(c => partOfDay(c.Local));
            if (useTime && clock is { } t)
            {
                var exact = q.Where(c => c.Local.TimeOfDay == t).ToList();
                q = exact.Count > 0 ? exact : q.OrderBy(c => c.Local.Date).ThenBy(c => Math.Abs((c.Local.TimeOfDay - t).TotalMinutes));
            }
            return q;
        }

        var filtered = Apply(true, true);
        if (!filtered.Any()) filtered = Apply(false, true);
        if (!filtered.Any()) filtered = candidates;

        var list = filtered.ToList();
        return (list.Count > 0 ? list[0] : candidates[0]).Utc;
    }
}
