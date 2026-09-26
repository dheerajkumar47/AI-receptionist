using System.Globalization;
using System.Net.Http.Json;
using System.Numerics;
using System.Text.Json;
using AiReceptionist.Core.Abstractions;
using ChannelIds = AiReceptionist.Core.Domain.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiReceptionist.Infrastructure.Channels.Twitter;

/// <summary>
/// X (Twitter) Direct Messages via API v2. X only offers DM webhooks (Account Activity API) on enterprise
/// plans, so this channel polls GET /2/dm_events and replies with POST /2/dm_conversations/with/:id/messages.
/// X DMs cannot carry audio attachments, so voice replies are sent as text plus a link to the audio clip.
/// </summary>
public sealed class TwitterDmChannel : IPollingChannel
{
    public const string HttpClientName = "x-api";
    private readonly IHttpClientFactory _http;
    private readonly TwitterOptions _options;
    private readonly ILogger<TwitterDmChannel> _log;
    private string? _myUserId;

    public TwitterDmChannel(IHttpClientFactory http, IOptions<TwitterOptions> options, ILogger<TwitterDmChannel> log)
    {
        _http = http;
        _options = options.Value;
        _log = log;
        _myUserId = string.IsNullOrWhiteSpace(_options.UserId) ? null : _options.UserId;
    }

    public string ChannelId => ChannelIds.Twitter;
    public string DisplayName => "X (Twitter) DMs";
    public bool IsConfigured => _options.IsConfigured;
    public ChannelCapabilities Capabilities { get; } = new(SupportsAudio: false, AudioFormat.Mp3, MaxTextLength: 9000);
    public TimeSpan PollInterval => TimeSpan.FromSeconds(Math.Max(15, _options.PollSeconds));

    public async Task<PollResult> PollAsync(string? cursor, CancellationToken ct)
    {
        _myUserId ??= await GetMyUserIdAsync(ct);

        var fresh = new List<InboundMessage>();
        string? newest = null;
        string? paginationToken = null;
        var cursorValue = ParseId(cursor);

        // Events are returned newest first; page until we reach the cursor (max 5 pages per poll).
        for (var page = 0; page < 5; page++)
        {
            var url = $"{_options.ApiBaseUrl.TrimEnd('/')}/2/dm_events?event_types=MessageCreate&max_results=100" +
                      "&dm_event.fields=id,text,event_type,created_at,sender_id,dm_conversation_id" +
                      (paginationToken is null ? "" : "&pagination_token=" + Uri.EscapeDataString(paginationToken));
            using var response = await SendSignedAsync(HttpMethod.Get, url, null, ct);
            var json = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"X dm_events returned {(int)response.StatusCode}: {json}");

            var (events, next) = ParseEvents(json);
            if (page == 0 && events.Count > 0) newest = events[0].Id;

            var reachedCursor = false;
            foreach (var e in events)
            {
                if (cursorValue is null || ParseId(e.Id) <= cursorValue) { reachedCursor = true; break; }
                if (e.SenderId == _myUserId) continue;
                fresh.Add(new InboundMessage(ChannelId, e.SenderId, null, e.Text, e.Id, e.CreatedUtc));
            }
            if (reachedCursor || next is null) break;
            paginationToken = next;
        }

        if (cursor is null)
        {
            // First run: do not answer the account's entire DM history, just remember where we are.
            _log.LogInformation("X DM polling initialised at event {Id}.", newest ?? "0");
            return new PollResult(Array.Empty<InboundMessage>(), newest ?? "0");
        }

        fresh.Reverse(); // oldest first
        return new PollResult(fresh, newest is not null && ParseId(newest) > cursorValue ? newest : cursor);
    }

    public async Task<SendResult> SendAsync(OutboundMessage message, CancellationToken ct)
    {
        var url = $"{_options.ApiBaseUrl.TrimEnd('/')}/2/dm_conversations/with/{Uri.EscapeDataString(message.RecipientId)}/messages";
        var text = message.Text ?? message.AudioUrl ?? "";
        using var response = await SendSignedAsync(HttpMethod.Post, url, JsonContent.Create(new { text }), ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) return SendResult.Fail($"{(int)response.StatusCode}: {body}");

        using var doc = JsonDocument.Parse(body);
        var id = doc.RootElement.TryGetProperty("data", out var d) && d.TryGetProperty("dm_event_id", out var e) ? e.GetString() : null;
        return SendResult.Ok(id);
    }

    public sealed record DmEvent(string Id, string SenderId, string Text, DateTime CreatedUtc);

    public static (IReadOnlyList<DmEvent> Events, string? NextToken) ParseEvents(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<DmEvent>();
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in data.EnumerateArray())
            {
                if (e.TryGetProperty("event_type", out var type) && type.GetString() != "MessageCreate") continue;
                var created = e.TryGetProperty("created_at", out var c) && DateTime.TryParse(c.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt) ? dt : DateTime.UtcNow;
                list.Add(new DmEvent(
                    e.GetProperty("id").GetString()!,
                    e.TryGetProperty("sender_id", out var s) ? s.GetString() ?? "" : "",
                    e.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "",
                    created));
            }
        }
        var next = doc.RootElement.TryGetProperty("meta", out var meta) && meta.TryGetProperty("next_token", out var n) ? n.GetString() : null;
        return (list, next);
    }

    private async Task<string> GetMyUserIdAsync(CancellationToken ct)
    {
        using var response = await SendSignedAsync(HttpMethod.Get, $"{_options.ApiBaseUrl.TrimEnd('/')}/2/users/me", null, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"X users/me returned {(int)response.StatusCode}: {json}");
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("data").GetProperty("id").GetString()!;
    }

    private async Task<HttpResponseMessage> SendSignedAsync(HttpMethod method, string url, HttpContent? content, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.TryAddWithoutValidation("Authorization", OAuth1Signer.BuildAuthorizationHeader(
            method.Method, new Uri(url), _options.ConsumerKey!, _options.ConsumerSecret!, _options.AccessToken!, _options.AccessTokenSecret!));
        return await _http.CreateClient(HttpClientName).SendAsync(request, ct);
    }

    private static BigInteger? ParseId(string? id) =>
        id is not null && BigInteger.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;
}
