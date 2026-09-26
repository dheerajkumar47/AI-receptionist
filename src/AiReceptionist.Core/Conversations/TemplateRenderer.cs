using System.Text.RegularExpressions;

namespace AiReceptionist.Core.Conversations;

/// <summary>
/// Replaces {placeholders} in admin-editable reply templates. Unknown placeholders are left as-is.
/// Empty values are tidied so "Thanks, {name}!" renders as "Thanks!" when the name is unknown.
/// </summary>
public static class TemplateRenderer
{
    private static readonly Regex Placeholder = new(@"\{(\w+)\}", RegexOptions.Compiled);
    private static readonly Regex DanglingComma = new(@",\s*([!.?])", RegexOptions.Compiled);
    private static readonly Regex SpaceBeforePunctuation = new(@"[ \t]+([!.,?])", RegexOptions.Compiled);
    private static readonly Regex DoubleSpace = new(@"[ \t]{2,}", RegexOptions.Compiled);

    public static string Render(string template, IReadOnlyDictionary<string, string?> values)
    {
        var result = Placeholder.Replace(template, m =>
            values.TryGetValue(m.Groups[1].Value.ToLowerInvariant(), out var v) ? v ?? "" : m.Value);
        result = DanglingComma.Replace(result, "$1");
        result = SpaceBeforePunctuation.Replace(result, "$1");
        result = DoubleSpace.Replace(result, " ");
        return result.Trim();
    }
}
