using System.ClientModel;
using System.Text.Json;
using AiReceptionist.Core.Abstractions;
using AiReceptionist.Core.Scheduling;
using Azure.AI.OpenAI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI.Chat;

namespace AiReceptionist.Infrastructure.AI;

/// <summary>
/// Intent detection and reply generation with Azure OpenAI (or OpenAI) chat completions in JSON mode.
/// The model classifies the message into one of the admin-defined intents, drafts the reply and extracts
/// any slot/name/email. The orchestrator then validates the slot before anything is booked.
/// </summary>
public sealed class OpenAiIntentEngine : IIntentEngine
{
    private readonly ChatClient _chat;
    private readonly OpenAiOptions _options;
    private readonly ILogger<OpenAiIntentEngine> _log;

    public OpenAiIntentEngine(IOptions<OpenAiOptions> options, ILogger<OpenAiIntentEngine> log)
    {
        _options = options.Value;
        _log = log;
        _chat = _options.Provider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase)
            ? new ChatClient(_options.Deployment, new ApiKeyCredential(_options.ApiKey!))
            : new AzureOpenAIClient(new Uri(_options.Endpoint!), new ApiKeyCredential(_options.ApiKey!)).GetChatClient(_options.Deployment);
    }

    public string Name => $"{_options.Provider} ({_options.Deployment})";

    public async Task<IntentResult> AnalyzeAsync(IntentContext ctx, CancellationToken ct)
    {
        var messages = new List<ChatMessage> { new SystemChatMessage(PromptBuilder.BuildSystemPrompt(ctx)) };
        foreach (var turn in ctx.History)
            messages.Add(turn.FromCustomer ? new UserChatMessage(turn.Text) : new AssistantChatMessage(turn.Text));
        messages.Add(new UserChatMessage(ctx.UserMessage));

        var options = new ChatCompletionOptions
        {
            ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat(),
            Temperature = _options.Temperature,
            MaxOutputTokenCount = 400,
        };

        ChatCompletion completion = await _chat.CompleteChatAsync(messages, options, ct);
        var json = completion.Content.Count > 0 ? completion.Content[0].Text : "{}";
        _log.LogDebug("LLM response: {Json}", json);
        return Parse(json, ctx);
    }

    /// <summary>Lenient parser for the model's JSON. Unknown intent names fall back to the last configured intent.</summary>
    public static IntentResult Parse(string json, IntentContext ctx)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string? Str(string name) =>
            root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
                ? v.GetString()!.Trim()
                : null;

        var intentName = Str("intent") ?? "Other";
        var match = ctx.Intents.FirstOrDefault(i => i.Name.Equals(intentName, StringComparison.OrdinalIgnoreCase));
        if (match is null && ctx.Intents.Count > 0) match = ctx.Intents.OrderBy(i => i.SortOrder).Last();

        double confidence = 0.5;
        if (root.TryGetProperty("confidence", out var c))
        {
            if (c.ValueKind == JsonValueKind.Number) confidence = c.GetDouble();
            else if (c.ValueKind == JsonValueKind.String && double.TryParse(c.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)) confidence = d;
        }

        var confirms = root.TryGetProperty("confirmsPendingSlot", out var cf) && cf.ValueKind == JsonValueKind.True;

        return new IntentResult(
            match?.Name ?? intentName,
            Math.Clamp(confidence, 0, 1),
            Str("reply") ?? "",
            SlotFormatter.ParseToUtc(Str("proposedSlotStart"), ctx.TimeZone),
            confirms,
            Str("customerName"),
            Str("customerEmail"));
    }
}
