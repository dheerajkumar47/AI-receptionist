using System.Text.RegularExpressions;
using AiReceptionist.Core.Domain;

namespace AiReceptionist.Core.Conversations;

/// <summary>Evaluates admin-defined <see cref="ConversationRule"/>s. First enabled match (lowest priority number) wins.</summary>
public static class RuleEvaluator
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);

    public static ConversationRule? Match(IEnumerable<ConversationRule> rules, string text, string channel)
    {
        var normalized = text.Trim();
        foreach (var rule in rules.Where(r => r.Enabled).OrderBy(r => r.Priority).ThenBy(r => r.Id))
        {
            if (!string.IsNullOrWhiteSpace(rule.ChannelFilter) &&
                !string.Equals(rule.ChannelFilter.Trim(), channel, StringComparison.OrdinalIgnoreCase))
                continue;

            if (IsMatch(rule, normalized)) return rule;
        }
        return null;
    }

    public static bool IsMatch(ConversationRule rule, string text)
    {
        if (string.IsNullOrEmpty(rule.Pattern)) return false;
        switch (rule.MatchType)
        {
            case RuleMatchType.Contains:
                return text.Contains(rule.Pattern, StringComparison.OrdinalIgnoreCase);
            case RuleMatchType.Exact:
                return string.Equals(text.TrimEnd('.', '!', '?'), rule.Pattern.Trim(), StringComparison.OrdinalIgnoreCase);
            case RuleMatchType.Regex:
                try
                {
                    return Regex.IsMatch(text, rule.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
                }
                catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
                {
                    return false; // invalid pattern entered in the dashboard: treat as no match
                }
            default:
                return false;
        }
    }

    /// <summary>Returns an error message if the rule's regex is invalid, otherwise null. Used by the dashboard.</summary>
    public static string? Validate(ConversationRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Pattern)) return "Pattern is required.";
        if (rule.MatchType != RuleMatchType.Regex) return null;
        try { _ = new Regex(rule.Pattern, RegexOptions.None, RegexTimeout); return null; }
        catch (ArgumentException ex) { return ex.Message; }
    }
}
