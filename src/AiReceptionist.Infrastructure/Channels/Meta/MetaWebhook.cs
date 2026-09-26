using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiReceptionist.Core.Abstractions;

namespace AiReceptionist.Infrastructure.Channels.Meta;

/// <summary>Shared Meta (Facebook / Instagram / WhatsApp) webhook handshake, signature check and payload parsing.</summary>
public static class MetaWebhook
{
    /// <summary>GET handshake: echo hub.challenge when hub.verify_token matches.</summary>
    public static string? Verify(IReadOnlyDictionary<string, string?> query, string? verifyToken)
    {
        if (string.IsNullOrEmpty(verifyToken)) return null;
        query.TryGetValue("hub.mode", out var mode);
        query.TryGetValue("hub.verify_token", out var token);
        query.TryGetValue("hub.challenge", out var challenge);
        return mode == "subscribe" && token is not null &&
               CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(verifyToken))
            ? challenge
            : null;
    }

    /// <summary>Validates X-Hub-Signature-256: "sha256=" + hex(HMACSHA256(appSecret, rawBody)).</summary>
    public static bool ValidateSignature(string? header, byte[] body, string? appSecret)
    {
        if (string.IsNullOrEmpty(appSecret) || string.IsNullOrEmpty(header) || !header.StartsWith("sha256=", StringComparison.Ordinal))
            return false;

        byte[] expected;
        try { expected = Convert.FromHexString(header["sha256=".Length..]); }
        catch (FormatException) { return false; }

        var actual = HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), body);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public static string Sign(byte[] body, string appSecret) =>
        "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), body)).ToLowerInvariant();

    /// <summary>
    /// Parses Messenger-platform payloads (object "page" for Facebook, "instagram" for Instagram):
    /// entry[].messaging[] with sender.id, timestamp and message { mid, text, is_echo, attachments }.
    /// </summary>
    public static IReadOnlyList<InboundMessage> ParseMessaging(string json, string expectedObject, string channelId)
    {
        var result = new List<InboundMessage>();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("object", out var obj) || obj.GetString() != expectedObject) return result;
        if (!root.TryGetProperty("entry", out var entries) || entries.ValueKind != JsonValueKind.Array) return result;

        foreach (var entry in entries.EnumerateArray())
        {
            if (!entry.TryGetProperty("messaging", out var events) || events.ValueKind != JsonValueKind.Array) continue;
            foreach (var ev in events.EnumerateArray())
            {
                if (!ev.TryGetProperty("message", out var msg)) continue; // delivery/read receipts, postbacks...
                if (msg.TryGetProperty("is_echo", out var echo) && echo.ValueKind == JsonValueKind.True) continue; // our own replies
                if (msg.TryGetProperty("is_deleted", out var del) && del.ValueKind == JsonValueKind.True) continue;

                var senderId = ev.GetProperty("sender").GetProperty("id").GetString();
                var mid = msg.TryGetProperty("mid", out var m) ? m.GetString() : null;
                if (senderId is null || mid is null) continue;

                var text = msg.TryGetProperty("text", out var t) ? t.GetString() : null;
                string? audioUrl = null;
                if (msg.TryGetProperty("attachments", out var atts) && atts.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in atts.EnumerateArray())
                    {
                        if (a.TryGetProperty("type", out var type) && type.GetString() == "audio" &&
                            a.TryGetProperty("payload", out var payload) && payload.TryGetProperty("url", out var url))
                            audioUrl = url.GetString();
                    }
                }
                if (text is null && audioUrl is null && !msg.TryGetProperty("attachments", out _)) continue;

                var ts = ev.TryGetProperty("timestamp", out var tsEl) && tsEl.TryGetInt64(out var ms)
                    ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime
                    : DateTime.UtcNow;

                result.Add(new InboundMessage(channelId, senderId, null, text, mid, ts, audioUrl is not null && text is null, audioUrl));
            }
        }
        return result;
    }

    /// <summary>
    /// Parses WhatsApp Cloud API payloads (object "whatsapp_business_account"):
    /// entry[].changes[].value.messages[] plus value.contacts[] for profile names. Status updates are ignored.
    /// </summary>
    public static IReadOnlyList<InboundMessage> ParseWhatsApp(string json, string channelId)
    {
        var result = new List<InboundMessage>();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("object", out var obj) || obj.GetString() != "whatsapp_business_account") return result;
        if (!root.TryGetProperty("entry", out var entries) || entries.ValueKind != JsonValueKind.Array) return result;

        foreach (var entry in entries.EnumerateArray())
        {
            if (!entry.TryGetProperty("changes", out var changes)) continue;
            foreach (var change in changes.EnumerateArray())
            {
                if (!change.TryGetProperty("value", out var value) || !value.TryGetProperty("messages", out var messages)) continue;

                var names = new Dictionary<string, string>();
                if (value.TryGetProperty("contacts", out var contacts))
                {
                    foreach (var c in contacts.EnumerateArray())
                    {
                        if (c.TryGetProperty("wa_id", out var wa) && c.TryGetProperty("profile", out var p) && p.TryGetProperty("name", out var n))
                            names[wa.GetString()!] = n.GetString()!;
                    }
                }

                foreach (var msg in messages.EnumerateArray())
                {
                    var from = msg.GetProperty("from").GetString()!;
                    var id = msg.GetProperty("id").GetString()!;
                    var type = msg.TryGetProperty("type", out var ty) ? ty.GetString() : "text";
                    var ts = msg.TryGetProperty("timestamp", out var tsEl) && long.TryParse(tsEl.GetString(), out var secs)
                        ? DateTimeOffset.FromUnixTimeSeconds(secs).UtcDateTime
                        : DateTime.UtcNow;
                    names.TryGetValue(from, out var name);

                    switch (type)
                    {
                        case "text":
                            result.Add(new InboundMessage(channelId, from, name, msg.GetProperty("text").GetProperty("body").GetString(), id, ts));
                            break;
                        case "audio":
                            var mediaId = msg.GetProperty("audio").GetProperty("id").GetString();
                            result.Add(new InboundMessage(channelId, from, name, null, id, ts, IsVoice: true, AudioReference: mediaId));
                            break;
                        case "button":
                            result.Add(new InboundMessage(channelId, from, name, msg.GetProperty("button").GetProperty("text").GetString(), id, ts));
                            break;
                        case "interactive":
                            var inter = msg.GetProperty("interactive");
                            var title = inter.TryGetProperty("button_reply", out var br) ? br.GetProperty("title").GetString()
                                : inter.TryGetProperty("list_reply", out var lr) ? lr.GetProperty("title").GetString() : null;
                            result.Add(new InboundMessage(channelId, from, name, title, id, ts));
                            break;
                        default:
                            // images, stickers, locations...: stored with no text so a human is alerted.
                            result.Add(new InboundMessage(channelId, from, name, null, id, ts));
                            break;
                    }
                }
            }
        }
        return result;
    }

    /// <summary>Reads a Graph API error body into a short message.</summary>
    public static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m))
                return $"{(int)response.StatusCode}: {m.GetString()}";
        }
        catch (JsonException) { }
        return $"{(int)response.StatusCode}: {body}";
    }
}
