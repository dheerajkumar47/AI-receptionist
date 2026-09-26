using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiReceptionist.Core.Abstractions;
using Microsoft.Extensions.Options;
using ChannelIds = AiReceptionist.Core.Domain.Channels;

namespace AiReceptionist.Infrastructure.Channels.Twilio;

/// <summary>
/// WhatsApp through Twilio (an official WhatsApp Business Solution Provider). Works with the Twilio WhatsApp
/// Sandbox, which needs no Meta business verification or app publishing, and with approved Twilio WhatsApp senders.
/// Inbound: Twilio POSTs form-encoded webhooks to /webhooks/whatsapp, signed with X-Twilio-Signature.
/// Outbound: Twilio Messages REST API; voice replies are sent as OGG/Opus media, which WhatsApp plays as voice notes.
/// Replaces the Meta WhatsApp connector when Twilio credentials are configured.
/// </summary>
public sealed class TwilioWhatsAppChannel : IWebhookChannel, IAudioDownloadChannel
{
    public const string HttpClientName = "twilio";
    private readonly IHttpClientFactory _http;
    private readonly TwilioOptions _options;
    private readonly string _webhookUrl;

    public TwilioWhatsAppChannel(IHttpClientFactory http, IOptions<TwilioOptions> options, IOptions<AppOptions> app)
    {
        _http = http;
        _options = options.Value;
        _webhookUrl = $"{app.Value.PublicBaseUrl.TrimEnd('/')}/webhooks/{ChannelIds.WhatsApp}";
    }

    public string ChannelId => ChannelIds.WhatsApp;
    public string DisplayName => "WhatsApp (via Twilio)";
    public bool IsConfigured => _options.IsConfigured;
    public ChannelCapabilities Capabilities { get; } = new(SupportsAudio: true, AudioFormat.OggOpus, MaxTextLength: 1600);

    /// <summary>Twilio has no subscription handshake.</summary>
    public string? VerifySubscription(IReadOnlyDictionary<string, string?> query) => null;

    public bool ValidateSignature(Func<string, string?> getHeader, byte[] body)
    {
        var header = getHeader("X-Twilio-Signature");
        if (string.IsNullOrEmpty(header) || string.IsNullOrEmpty(_options.AuthToken)) return false;
        var expected = Encoding.ASCII.GetBytes(Sign(_webhookUrl, ParseForm(Encoding.UTF8.GetString(body)), _options.AuthToken));
        return CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(header));
    }

    public IReadOnlyList<InboundMessage> ParsePayload(string body)
    {
        var form = ParseForm(body);
        if (!form.TryGetValue("MessageSid", out var sid) || !form.TryGetValue("From", out var from)) return Array.Empty<InboundMessage>();

        var sender = from.Replace("whatsapp:", "", StringComparison.OrdinalIgnoreCase).TrimStart('+');
        form.TryGetValue("ProfileName", out var name);
        form.TryGetValue("Body", out var text);
        if (string.IsNullOrWhiteSpace(text)) text = null;

        string? audioUrl = null;
        if (form.TryGetValue("NumMedia", out var n) && n != "0" &&
            form.TryGetValue("MediaContentType0", out var type) && type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            form.TryGetValue("MediaUrl0", out audioUrl);

        return new[]
        {
            new InboundMessage(ChannelId, sender, name, text, sid, DateTime.UtcNow,
                IsVoice: audioUrl is not null && text is null, AudioReference: audioUrl),
        };
    }

    public async Task<SendResult> SendAsync(OutboundMessage message, CancellationToken ct)
    {
        var fields = new List<KeyValuePair<string, string>>
        {
            new("From", NormalizeWhatsApp(_options.WhatsAppFrom)),
            new("To", "whatsapp:+" + message.RecipientId.TrimStart('+')),
        };
        if (message.AudioUrl is not null) fields.Add(new("MediaUrl", message.AudioUrl));
        else fields.Add(new("Body", message.Text ?? ""));

        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{_options.ApiBaseUrl.TrimEnd('/')}/2010-04-01/Accounts/{_options.AccountSid}/Messages.json")
        {
            Content = new FormUrlEncodedContent(fields),
        };
        request.Headers.Authorization = BasicAuth();

        using var response = await _http.CreateClient(HttpClientName).SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        if (!response.IsSuccessStatusCode)
        {
            var error = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : json;
            return SendResult.Fail($"{(int)response.StatusCode}: {error}");
        }
        return SendResult.Ok(doc.RootElement.TryGetProperty("sid", out var s) ? s.GetString() : null);
    }

    /// <summary>Downloads an inbound voice note (Twilio media URLs accept the account's Basic auth).</summary>
    public async Task<(byte[] Data, string ContentType)?> DownloadAudioAsync(string audioReference, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, audioReference);
        request.Headers.Authorization = BasicAuth();
        using var response = await _http.CreateClient(HttpClientName).SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return null;
        return (await response.Content.ReadAsByteArrayAsync(ct), response.Content.Headers.ContentType?.MediaType ?? "audio/ogg");
    }

    /// <summary>Twilio request signature: Base64(HMAC-SHA1(authToken, url + each key+value sorted by key)).</summary>
    public static string Sign(string url, IReadOnlyDictionary<string, string> form, string authToken)
    {
        var data = new StringBuilder(url);
        foreach (var kv in form.OrderBy(kv => kv.Key, StringComparer.Ordinal)) data.Append(kv.Key).Append(kv.Value);
        return Convert.ToBase64String(HMACSHA1.HashData(Encoding.UTF8.GetBytes(authToken), Encoding.UTF8.GetBytes(data.ToString())));
    }

    public static Dictionary<string, string> ParseForm(string body)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = pair.IndexOf('=');
            var key = Decode(i < 0 ? pair : pair[..i]);
            result[key] = i < 0 ? "" : Decode(pair[(i + 1)..]);
        }
        return result;

        static string Decode(string s) => Uri.UnescapeDataString(s.Replace('+', ' '));
    }

    private AuthenticationHeaderValue BasicAuth() =>
        new("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"{_options.AccountSid}:{_options.AuthToken}")));

    private static string NormalizeWhatsApp(string number) =>
        number.StartsWith("whatsapp:", StringComparison.OrdinalIgnoreCase) ? number : "whatsapp:" + number;
}
