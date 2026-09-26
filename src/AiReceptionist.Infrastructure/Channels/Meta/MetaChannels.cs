using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AiReceptionist.Core.Abstractions;
using ChannelIds = AiReceptionist.Core.Domain.Channels;
using Microsoft.Extensions.Options;

namespace AiReceptionist.Infrastructure.Channels.Meta;

/// <summary>Common plumbing for Meta channels: webhook verification, signature checks and Graph API calls.</summary>
public abstract class MetaChannelBase : IWebhookChannel
{
    public const string HttpClientName = "meta-graph";
    private readonly IHttpClientFactory _http;

    protected MetaChannelBase(IHttpClientFactory http, IOptions<MetaOptions> options)
    {
        _http = http;
        Meta = options.Value;
    }

    protected MetaOptions Meta { get; }

    public abstract string ChannelId { get; }
    public abstract string DisplayName { get; }
    public abstract ChannelCapabilities Capabilities { get; }
    protected abstract string? AccessToken { get; }

    public virtual bool IsConfigured =>
        !string.IsNullOrWhiteSpace(AccessToken) && !string.IsNullOrWhiteSpace(Meta.AppSecret) && !string.IsNullOrWhiteSpace(Meta.VerifyToken);

    public string? VerifySubscription(IReadOnlyDictionary<string, string?> query) => MetaWebhook.Verify(query, Meta.VerifyToken);

    public bool ValidateSignature(Func<string, string?> getHeader, byte[] body) =>
        MetaWebhook.ValidateSignature(getHeader("X-Hub-Signature-256"), body, Meta.AppSecret);

    public abstract IReadOnlyList<InboundMessage> ParsePayload(string json);

    public abstract Task<SendResult> SendAsync(OutboundMessage message, CancellationToken ct);

    protected string GraphUrl(string path, string? baseUrl = null) =>
        $"{(string.IsNullOrWhiteSpace(baseUrl) ? Meta.GraphBaseUrl : baseUrl).TrimEnd('/')}/{Meta.GraphApiVersion}/{path.TrimStart('/')}";

    /// <summary>POSTs JSON with a bearer token (keeps tokens out of URLs and logs) and returns the parsed response.</summary>
    protected async Task<(bool Ok, JsonDocument? Body, string? Error)> PostJsonAsync(string url, object payload, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(payload) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
        using var response = await Client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return (false, null, await MetaWebhook.ReadErrorAsync(response, ct));
        return (true, JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)), null);
    }

    protected HttpClient Client => _http.CreateClient(HttpClientName);
}

/// <summary>Facebook Page inbox via the Messenger Platform (Send API + "messages" webhook field).</summary>
public sealed class FacebookMessengerChannel : MetaChannelBase
{
    public FacebookMessengerChannel(IHttpClientFactory http, IOptions<MetaOptions> options) : base(http, options) { }

    public override string ChannelId => ChannelIds.Facebook;
    public override string DisplayName => "Facebook Messenger";
    public override ChannelCapabilities Capabilities { get; } = new(SupportsAudio: true, AudioFormat.Mp3, MaxTextLength: 2000);
    protected override string? AccessToken => Meta.Facebook.PageAccessToken;

    public override IReadOnlyList<InboundMessage> ParsePayload(string json) => MetaWebhook.ParseMessaging(json, "page", ChannelId);

    public override async Task<SendResult> SendAsync(OutboundMessage message, CancellationToken ct)
    {
        object body = message.AudioUrl is not null
            ? new
            {
                recipient = new { id = message.RecipientId },
                messaging_type = "RESPONSE",
                message = new { attachment = new { type = "audio", payload = new { url = message.AudioUrl, is_reusable = false } } },
            }
            : new { recipient = new { id = message.RecipientId }, messaging_type = "RESPONSE", message = new { text = message.Text } };

        var (ok, doc, error) = await PostJsonAsync(GraphUrl("me/messages"), body, ct);
        using (doc)
            return ok ? SendResult.Ok(doc!.RootElement.TryGetProperty("message_id", out var id) ? id.GetString() : null) : SendResult.Fail(error!);
    }
}

/// <summary>Instagram professional-account DMs (Messenger API for Instagram, or Instagram API with Instagram Login).</summary>
public sealed class InstagramChannel : MetaChannelBase
{
    public InstagramChannel(IHttpClientFactory http, IOptions<MetaOptions> options) : base(http, options) { }

    public override string ChannelId => ChannelIds.Instagram;
    public override string DisplayName => "Instagram Direct";
    public override ChannelCapabilities Capabilities { get; } = new(SupportsAudio: true, AudioFormat.Mp3, MaxTextLength: 1000);
    protected override string? AccessToken => Meta.Instagram.AccessToken;

    public override IReadOnlyList<InboundMessage> ParsePayload(string json) => MetaWebhook.ParseMessaging(json, "instagram", ChannelId);

    public override async Task<SendResult> SendAsync(OutboundMessage message, CancellationToken ct)
    {
        object body = message.AudioUrl is not null
            ? new { recipient = new { id = message.RecipientId }, message = new { attachment = new { type = "audio", payload = new { url = message.AudioUrl } } } }
            : new { recipient = new { id = message.RecipientId }, message = new { text = message.Text } };

        var (ok, doc, error) = await PostJsonAsync(GraphUrl("me/messages", Meta.Instagram.ApiBaseUrl), body, ct);
        using (doc)
            return ok ? SendResult.Ok(doc!.RootElement.TryGetProperty("message_id", out var id) ? id.GetString() : null) : SendResult.Fail(error!);
    }
}

/// <summary>WhatsApp Business via the WhatsApp Cloud API. Voice replies are delivered as OGG/Opus voice notes.</summary>
public sealed class WhatsAppChannel : MetaChannelBase, IAudioDownloadChannel
{
    public WhatsAppChannel(IHttpClientFactory http, IOptions<MetaOptions> options) : base(http, options) { }

    public override string ChannelId => ChannelIds.WhatsApp;
    public override string DisplayName => "WhatsApp Business";
    public override ChannelCapabilities Capabilities { get; } = new(SupportsAudio: true, AudioFormat.OggOpus, MaxTextLength: 4096);
    protected override string? AccessToken => Meta.WhatsApp.AccessToken;

    public override bool IsConfigured => base.IsConfigured && !string.IsNullOrWhiteSpace(Meta.WhatsApp.PhoneNumberId);

    public override IReadOnlyList<InboundMessage> ParsePayload(string json) => MetaWebhook.ParseWhatsApp(json, ChannelId);

    public override async Task<SendResult> SendAsync(OutboundMessage message, CancellationToken ct)
    {
        object body = message.AudioUrl is not null
            ? new { messaging_product = "whatsapp", recipient_type = "individual", to = message.RecipientId, type = "audio", audio = new { link = message.AudioUrl } }
            : new { messaging_product = "whatsapp", recipient_type = "individual", to = message.RecipientId, type = "text", text = new { preview_url = false, body = message.Text } };

        var (ok, doc, error) = await PostJsonAsync(GraphUrl($"{Meta.WhatsApp.PhoneNumberId}/messages"), body, ct);
        using (doc)
        {
            if (!ok) return SendResult.Fail(error!);
            var id = doc!.RootElement.TryGetProperty("messages", out var msgs) && msgs.GetArrayLength() > 0
                ? msgs[0].GetProperty("id").GetString()
                : null;
            return SendResult.Ok(id);
        }
    }

    /// <summary>Two-step media download: resolve the media id to a short-lived URL, then fetch it with the token.</summary>
    public async Task<(byte[] Data, string ContentType)?> DownloadAudioAsync(string audioReference, CancellationToken ct)
    {
        using var metaRequest = new HttpRequestMessage(HttpMethod.Get, GraphUrl(audioReference));
        metaRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
        using var metaResponse = await Client.SendAsync(metaRequest, ct);
        if (!metaResponse.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(await metaResponse.Content.ReadAsStringAsync(ct));
        var url = doc.RootElement.GetProperty("url").GetString();
        var mime = doc.RootElement.TryGetProperty("mime_type", out var m) ? m.GetString() ?? "audio/ogg" : "audio/ogg";

        using var fileRequest = new HttpRequestMessage(HttpMethod.Get, url);
        fileRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
        using var fileResponse = await Client.SendAsync(fileRequest, ct);
        if (!fileResponse.IsSuccessStatusCode) return null;
        return (await fileResponse.Content.ReadAsByteArrayAsync(ct), mime);
    }
}
