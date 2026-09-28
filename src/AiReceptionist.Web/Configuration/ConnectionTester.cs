using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AiReceptionist.Core.Abstractions;
using AiReceptionist.Infrastructure;
using AiReceptionist.Infrastructure.Email;
using Microsoft.Extensions.Options;

namespace AiReceptionist.Web.Configuration;

public sealed record TestResult(bool Ok, string Message);

/// <summary>
/// Checks credentials typed on the Connections page against the real services before they are saved, and runs the
/// one-time "link" calls Meta requires (subscribing the WhatsApp account, Page or Instagram account to the app).
/// </summary>
public sealed class ConnectionTester(IHttpClientFactory http, IConfiguration config)
{
    private const string ClientName = "connection-tests";
    private string GraphVersion => config["Meta:GraphApiVersion"] is { Length: > 0 } v ? v : "v21.0";
    private string GraphBase => (config["Meta:GraphBaseUrl"] is { Length: > 0 } b ? b : "https://graph.facebook.com").TrimEnd('/');

    public async Task<TestResult> OpenAiAsync(string? provider, string? endpoint, string? key, string? deployment, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(deployment)) return new(false, "Enter the API key and deployment name.");
        var isOpenAi = string.Equals(provider, "OpenAI", StringComparison.OrdinalIgnoreCase);
        if (!isOpenAi && string.IsNullOrWhiteSpace(endpoint)) return new(false, "Enter the endpoint.");

        using var request = isOpenAi
            ? new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
            : new HttpRequestMessage(HttpMethod.Post, $"{endpoint!.TrimEnd('/')}/openai/deployments/{Uri.EscapeDataString(deployment)}/chat/completions?api-version=2024-10-21");
        if (isOpenAi) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        else request.Headers.Add("api-key", key);
        request.Content = JsonContent.Create(new
        {
            model = isOpenAi ? deployment : null,
            messages = new[] { new { role = "user", content = "Reply with the single word OK." } },
        }, options: new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

        return await RunAsync(request, doc =>
        {
            var reply = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            return $"Connected. The model replied: \"{reply?.Trim()}\"";
        }, ct);
    }

    public async Task<TestResult> SpeechAsync(string? key, string? region, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(region)) return new(false, "Enter the key and region.");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{region.Trim().ToLowerInvariant()}.api.cognitive.microsoft.com/sts/v1.0/issueToken");
        request.Headers.Add("Ocp-Apim-Subscription-Key", key.Trim());
        request.Content = new StringContent("");
        return await RunAsync(request, _ => "Connected. The key and region are valid.", ct, parseJson: false);
    }

    public async Task<TestResult> EmailAsync(SmtpOptions options, CancellationToken ct)
    {
        if (!options.IsConfigured) return new(false, "Enter the server, sender address and owner address.");
        try
        {
            var sender = new SmtpEmailSender(Options.Create(options));
            await sender.SendAsync(new EmailMessage(new[] { options.OwnerAddress! }, "AI Receptionist: email connected",
                "This test email confirms that booking confirmations and reminders can be sent."), ct);
            return new(true, $"Test email sent to {options.OwnerAddress}. Check the inbox (and spam folder).");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(false, ex.Message);
        }
    }

    public Task<TestResult> WhatsAppAsync(string? token, string? phoneNumberId, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(phoneNumberId)
            ? Task.FromResult(new TestResult(false, "Enter the access token and phone number ID."))
            : GetAsync($"{GraphBase}/{GraphVersion}/{Uri.EscapeDataString(phoneNumberId.Trim())}?fields=display_phone_number,verified_name", token,
                doc => $"Connected to {Str(doc, "verified_name")} ({Str(doc, "display_phone_number")}).", ct);

    /// <summary>Links the WhatsApp Business Account to the Meta app so its messages reach the webhook.</summary>
    public Task<TestResult> LinkWhatsAppAsync(string? token, string? businessAccountId, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(businessAccountId)
            ? Task.FromResult(new TestResult(false, "Enter the access token and WhatsApp Business Account ID."))
            : PostAsync($"{GraphBase}/{GraphVersion}/{Uri.EscapeDataString(businessAccountId.Trim())}/subscribed_apps", token,
                "WhatsApp account linked: customer messages will now reach the receptionist.", ct);

    public Task<TestResult> MessengerAsync(string? pageToken, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(pageToken)
            ? Task.FromResult(new TestResult(false, "Enter the Page access token."))
            : GetAsync($"{GraphBase}/{GraphVersion}/me?fields=name,id", pageToken, doc => $"Connected to the Page \"{Str(doc, "name")}\".", ct);

    public Task<TestResult> SubscribePageAsync(string? pageToken, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(pageToken)
            ? Task.FromResult(new TestResult(false, "Enter the Page access token."))
            : PostAsync($"{GraphBase}/{GraphVersion}/me/subscribed_apps?subscribed_fields=messages", pageToken,
                "Page subscribed: Messenger messages will now reach the receptionist.", ct);

    public Task<TestResult> InstagramAsync(string? token, string? apiBaseUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token)) return Task.FromResult(new TestResult(false, "Enter the Instagram access token."));
        var igLogin = !string.IsNullOrWhiteSpace(apiBaseUrl);
        var url = igLogin
            ? $"{apiBaseUrl!.TrimEnd('/')}/{GraphVersion}/me?fields=username"
            : $"{GraphBase}/{GraphVersion}/me?fields=name,instagram_business_account{{username}}";
        return GetAsync(url, token, doc => igLogin
            ? $"Connected to @{Str(doc, "username")}."
            : doc.RootElement.TryGetProperty("instagram_business_account", out var ig)
                ? $"Connected to @{Str(ig, "username")} through the Page \"{Str(doc, "name")}\"."
                : $"Token works for \"{Str(doc, "name")}\", but no Instagram professional account is linked to this Page.", ct);
    }

    public Task<TestResult> LinkInstagramAsync(string? token, string? apiBaseUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token)) return Task.FromResult(new TestResult(false, "Enter the Instagram access token."));
        var baseUrl = string.IsNullOrWhiteSpace(apiBaseUrl) ? GraphBase : apiBaseUrl.TrimEnd('/');
        return PostAsync($"{baseUrl}/{GraphVersion}/me/subscribed_apps?subscribed_fields=messages", token,
            "Instagram account linked: DMs will reach the receptionist (real DMs need a published Meta app).", ct);
    }

    // ------------------------------------------------------------------ helpers

    private async Task<TestResult> GetAsync(string url, string token, Func<JsonDocument, string> describe, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        return await RunAsync(request, describe, ct);
    }

    private async Task<TestResult> PostAsync(string url, string token, string success, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent("") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        return await RunAsync(request, _ => success, ct);
    }

    private async Task<TestResult> RunAsync(HttpRequestMessage request, Func<JsonDocument, string> describe, CancellationToken ct, bool parseJson = true)
    {
        try
        {
            using var response = await http.CreateClient(ClientName).SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode) return new(false, $"{(int)response.StatusCode}: {ErrorMessage(body)}");
            if (!parseJson) return new(true, describe(JsonDocument.Parse("{}")));
            using var doc = JsonDocument.Parse(body);
            return new(true, describe(doc));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(false, ex.Message);
        }
    }

    private static string ErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e))
                return e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var m) ? m.GetString() ?? body : e.ToString();
        }
        catch (JsonException) { }
        return string.IsNullOrWhiteSpace(body) ? "request rejected (check the key or token)" : body.Length > 300 ? body[..300] : body;
    }

    private static string Str(JsonDocument doc, string name) => Str(doc.RootElement, name);

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v.ToString() : "?";
}
