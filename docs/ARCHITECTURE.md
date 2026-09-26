# Architecture

## Message flow

```
 Facebook ─┐  POST /webhooks/facebook   ┌──────────────────────┐
 Instagram ─┼─ POST /webhooks/instagram ─▶ WebhookEndpoints      │ verify X-Hub-Signature-256,
 WhatsApp ─┘  POST /webhooks/whatsapp   │ (IWebhookChannel)     │ parse, enqueue, return 200 at once
                                        └──────────┬───────────┘
 X / Twitter ── ChannelPollingWorker (IPollingChannel, cursor in DB) ─┤
 Dashboard simulator ─────────────────────────────────────────────────┤
                                                                      ▼
                                                           IInboundQueue (in-memory)
                                                                      │
                                                     InboundProcessingWorker (sequential)
                                                                      │
                                                                      ▼
┌──────────────────────────────── ConversationService.ProcessInboundAsync ───────────────────────────────┐
│ 1. de-duplicate on (channel, platform message id)        6. run the intent's action:                    │
│ 2. contact + conversation; transcribe voice note            ProposeAppointment → validate slot, store   │
│ 3. stop if HumanTakeover / auto-reply off (flag it)         ConfirmAppointment → re-validate, book      │
│ 4. RuleEvaluator (admin rules, before any AI)               CancelAppointment → cancel next booking     │
│ 5. AvailabilityService (hours + M365 free/busy + DB)        HumanHandoff → pause bot, flag              │
│    → IIntentEngine (Azure OpenAI JSON / offline)         7. approval mode? save Draft : Deliver         │
└─────────────────────────────────────────────────────────────────────────────────────────────────────────┘
                                                                      │
                               Deliver: IVoiceSynthesizer (Azure Speech) → IMediaStore (/media/*.mp3|ogg)
                                        → IChannelConnector.SendAsync (text, then audio)
                                                                      │
                               AppointmentService: ICalendarProvider (Graph) ──fail──▶ IEmailSender (+ .ics)
                                                                      │
                                              IActivityNotifier ──▶ Blazor dashboard pages refresh live
```

## Projects

| Project | Responsibility | Depends on |
|---|---|---|
| `AiReceptionist.Core` | Domain entities, EF Core `ReceptionistDbContext`, `ConversationService` (orchestrator), `RuleEvaluator`, `TemplateRenderer`, `KeywordIntentEngine`, scheduling (`BusinessHours`, `SlotCalculator`, `AvailabilityService`, `AppointmentService`, `IcsBuilder`), all abstractions | EF Core only |
| `AiReceptionist.Infrastructure` | Implementations of the abstractions against external services: Azure OpenAI, Azure Speech (REST), Microsoft Graph, MailKit SMTP, Meta Graph API, X API v2; `AddAiReceptionist()` DI registration | Core |
| `AiReceptionist.Web` | ASP.NET Core host: webhook and auth endpoints, background workers, Blazor Server dashboard | Infrastructure |
| `AiReceptionist.Tests` | Unit tests plus an in-process integration test of the full webhook → reply path | Web |

## Key design decisions

- **The LLM proposes; the code decides.** The model classifies the intent and drafts the wording. Every slot it proposes is
  checked against opening hours, lead time, the booking horizon, Microsoft 365 free/busy and existing bookings, both
  when the slot is offered and again right before booking. The confirmation text comes from admin templates, so the
  bot never says "booked" unless the booking actually succeeded.
- **Two-step booking.** A proposal is stored on the conversation (`PendingSlotStartUtc`). Only a confirmation intent books it.
  This matches the requirement to book "once the user confirms a time".
- **Graceful degradation everywhere.** A missing or failing service falls back as follows: no LLM → offline keyword
  engine; no speech → text; channel without audio (X) → text plus a link to the audio; no calendar → email with .ics;
  engine error → handoff message and a *needs attention* flag. The customer always gets an answer, and the admin
  always sees what happened.
- **Webhooks return immediately.** Meta retries slow webhooks and eventually disables them. Payloads are verified,
  queued and acknowledged. Processing happens on a background worker, one message at a time, so replies stay in order.
- **Idempotency.** A unique index on `(Channel, ExternalId)` drops webhook retries. Graph events carry a `transactionId`.
- **Azure Speech over REST** rather than the native Speech SDK. It has no native binaries, runs the same on
  Windows, Linux and App Service, and produces exactly the formats each channel wants (OGG/Opus for WhatsApp voice notes, MP3 elsewhere).
- **X is polled** because DM webhooks need an Enterprise plan. The cursor is persisted, and the first run skips the backlog.
- **Configuration lives in two places.** Credentials are in `IConfiguration` (user-secrets, environment variables, Key Vault).
  Behaviour (intents, rules, persona, hours, templates) is in the database, so the owner can tune it from the dashboard with no redeploy.
- **Channels are strings, not an enum**, so a new channel can live in its own assembly.

## Adding a channel

Implement `IChannelConnector`, plus `IWebhookChannel` (push) or `IPollingChannel` (pull), and register it. The webhook
route `/webhooks/{ChannelId}`, the polling worker, conversation storage, voice and booking all work automatically.
For example, a Telegram bot:

```csharp
public sealed class TelegramChannel : IWebhookChannel
{
    private readonly HttpClient _http;
    private readonly string _token, _secret;

    public TelegramChannel(IHttpClientFactory f, IConfiguration c)
    {
        _http = f.CreateClient();
        _token = c["Telegram:BotToken"] ?? "";
        _secret = c["Telegram:SecretToken"] ?? "";
    }

    public string ChannelId => "telegram";
    public string DisplayName => "Telegram";
    public bool IsConfigured => _token.Length > 0;
    public ChannelCapabilities Capabilities { get; } = new(SupportsAudio: true, AudioFormat.OggOpus, MaxTextLength: 4096);

    // Telegram has no GET handshake; the webhook is registered with setWebhook(secret_token=...)
    public string? VerifySubscription(IReadOnlyDictionary<string, string?> query) => null;

    public bool ValidateSignature(Func<string, string?> header, byte[] body) =>
        header("X-Telegram-Bot-Api-Secret-Token") == _secret;

    public IReadOnlyList<InboundMessage> ParsePayload(string json)
    {
        var msg = JsonDocument.Parse(json).RootElement.GetProperty("message");
        return new[] { new InboundMessage(ChannelId, msg.GetProperty("chat").GetProperty("id").ToString(),
            msg.GetProperty("from").GetProperty("first_name").GetString(),
            msg.TryGetProperty("text", out var t) ? t.GetString() : null,
            msg.GetProperty("message_id").ToString(), DateTime.UtcNow) };
    }

    public async Task<SendResult> SendAsync(OutboundMessage m, CancellationToken ct)
    {
        var (method, payload) = m.AudioUrl is null
            ? ("sendMessage", (object)new { chat_id = m.RecipientId, text = m.Text })
            : ("sendVoice", new { chat_id = m.RecipientId, voice = m.AudioUrl });
        var r = await _http.PostAsJsonAsync($"https://api.telegram.org/bot{_token}/{method}", payload, ct);
        return r.IsSuccessStatusCode ? SendResult.Ok() : SendResult.Fail(await r.Content.ReadAsStringAsync(ct));
    }
}

// DependencyInjection.cs
services.AddSingleton<IChannelConnector, TelegramChannel>();
```

To show the new channel in the dashboard's channel filters, add its id to the arrays in `Conversations.razor`,
`MessageLog.razor` and `Rules.razor`, and give it an icon in `Ui.ChannelIcon`.

## Scaling out

The defaults (SQLite, in-memory queue, one worker) suit a single business on one App Service instance. To run several instances:

1. Switch EF Core to SQL Server or PostgreSQL (`UseSqlServer` in `DependencyInjection.cs`) and introduce migrations.
2. Replace `InMemoryInboundQueue` with an `IInboundQueue` backed by Azure Service Bus or Storage Queues. Partition by conversation to keep ordering.
3. Replace `FileMediaStore` with Azure Blob Storage (public-read container or SAS URLs).
4. Replace `ActivityNotifier` with a backplane (e.g. Azure SignalR Service) if dashboard users connect to different instances.
5. Run X polling in only one instance, or use a distributed lock.
