using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AiReceptionist.Core.Abstractions;
using ChannelIds = AiReceptionist.Core.Domain.Channels;
using AiReceptionist.Infrastructure.Voice;
using Microsoft.Extensions.Options;

namespace AiReceptionist.Infrastructure.Channels.Meta;

/// <summary>Common plumbing for Meta channels: webhook verification, signature checks and Graph API calls.</summary>
public abstract class MetaChannelBase : IWebhookChannel, IAudioDownloadChannel
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
        !string.IsNullOrWhiteSpace(AccessToken) && !string.IsNullOrWhiteSpace(WebhookSecret) && !string.IsNullOrWhiteSpace(Meta.VerifyToken);

    public string? VerifySubscription(IReadOnlyDictionary<string, string?> query) => MetaWebhook.Verify(query, Meta.VerifyToken);

    public bool ValidateSignature(Func<string, string?> getHeader, byte[] body) =>
        MetaWebhook.ValidateSignature(getHeader("X-Hub-Signature-256"), body, WebhookSecret);

    /// <summary>Secret used to sign this channel's webhooks (the Meta app secret unless a channel overrides it).</summary>
    protected virtual string? WebhookSecret => Meta.AppSecret;

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

    /// <summary>Messenger and Instagram voice notes arrive as a public CDN URL (usually MP4/AAC).</summary>
    public virtual async Task<(byte[] Data, string ContentType)?> DownloadAudioAsync(string audioReference, CancellationToken ct)
    {
        if (!Uri.TryCreate(audioReference, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;
        using var response = await Client.GetAsync(uri, ct);
        if (!response.IsSuccessStatusCode) return null;
        return (await response.Content.ReadAsByteArrayAsync(ct), response.Content.Headers.ContentType?.MediaType ?? "audio/mp4");
    }
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
    protected override string? WebhookSecret => string.IsNullOrWhiteSpace(Meta.Instagram.AppSecret) ? Meta.AppSecret : Meta.Instagram.AppSecret;

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
public sealed class WhatsAppChannel : MetaChannelBase
{
    private readonly FileMediaStore? _media;

    public WhatsAppChannel(IHttpClientFactory http, IOptions<MetaOptions> options, FileMediaStore? media = null) : base(http, options)
        => _media = media;

    public override string ChannelId => ChannelIds.WhatsApp;
    public override string DisplayName => "WhatsApp Business";
    public override ChannelCapabilities Capabilities { get; } = new(SupportsAudio: true, AudioFormat.OggOpus, MaxTextLength: 4096);
    protected override string? AccessToken => Meta.WhatsApp.AccessToken;

    public override bool IsConfigured => base.IsConfigured && !string.IsNullOrWhiteSpace(Meta.WhatsApp.PhoneNumberId);

    public override IReadOnlyList<InboundMessage> ParsePayload(string json) => MetaWebhook.ParseWhatsApp(json, ChannelId);

    public override async Task<SendResult> SendAsync(OutboundMessage message, CancellationToken ct)
    {
        object body;
        if (message.AudioUrl is not null)
        {
            // Prefer uploading the clip to WhatsApp and sending it by media id: WhatsApp's media fetcher is strict about
            // links (tunnels, interstitial pages, content types), while an upload always works.
            var mediaId = await TryUploadAudioAsync(message.AudioUrl, ct);
            body = mediaId is not null
                ? new { messaging_product = "whatsapp", recipient_type = "individual", to = message.RecipientId, type = "audio", audio = (object)new { id = mediaId } }
                : new { messaging_product = "whatsapp", recipient_type = "individual", to = message.RecipientId, type = "audio", audio = (object)new { link = message.AudioUrl } };
        }
        else
        {
            body = new { messaging_product = "whatsapp", recipient_type = "individual", to = message.RecipientId, type = "text", text = new { preview_url = false, body = message.Text } };
        }

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

    /// <summary>Uploads a locally stored clip via POST /{phone-number-id}/media and returns the WhatsApp media id.</summary>
    private async Task<string?> TryUploadAudioAsync(string audioUrl, CancellationToken ct)
    {
        if (_media is null || !_media.TryGetLocalPath(audioUrl, out var path)) return null;

        var contentType = path.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) ? "audio/ogg" : "audio/mpeg";
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("whatsapp"), "messaging_product");
        form.Add(new StringContent(contentType), "type");
        var file = new ByteArrayContent(await File.ReadAllBytesAsync(path, ct));
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", Path.GetFileName(path));

        using var request = new HttpRequestMessage(HttpMethod.Post, GraphUrl($"{Meta.WhatsApp.PhoneNumberId}/media")) { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
        using var response = await Client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return null; // fall back to sending the link

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("id", out var id) ? id.GetString() : null;
    }

    /// <summary>Two-step media download: resolve the media id to a short-lived URL, then fetch it with the token.</summary>
    public override async Task<(byte[] Data, string ContentType)?> DownloadAudioAsync(string audioReference, CancellationToken ct)
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
