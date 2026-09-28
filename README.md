# AI Receptionist (.NET 10 / C#)

An AI receptionist that answers direct messages on **Facebook Messenger, Instagram, WhatsApp and X (Twitter)**.
It works out what the sender wants, replies in natural language as **text and/or a synthesized voice note**,
and books confirmed appointments straight into a **Microsoft 365 calendar**. If the calendar API is unavailable,
it sends a **confirmation email with an .ics attachment** instead. A **Blazor admin dashboard** shows every
message, lets you override replies, pause the bot per conversation, and edit intents, rules and settings without touching code.

![Dashboard](docs/images/conversation.png)

## Features

| Area | What it does |
|---|---|
| Social inboxes | Webhooks for Facebook Messenger, Instagram Direct and the WhatsApp Cloud API (with signature verification); polling for X DMs. New channels plug in by implementing one interface. |
| Understanding | Azure OpenAI (or OpenAI) in JSON mode picks one of *your* intents, drafts the reply and extracts the time slot, name and email. An offline keyword engine runs when no LLM key is set. |
| Conversation rules | Admin-defined rules (contains / exact / regex, per channel) run before the AI: force an intent, send a fixed reply, hand off to a human, or ignore. |
| Voice | Azure AI Speech neural voices. Replies go out as native audio (an OGG/Opus voice note on WhatsApp, MP3 on Messenger and Instagram). X can't carry audio, so it gets text plus a link. Inbound WhatsApp voice notes are transcribed. |
| Appointments | Free slots come from opening hours plus Microsoft 365 free/busy plus existing bookings. The bot offers a slot and waits for "yes", then checks the slot is still free before writing the event through Microsoft Graph. If Graph fails, it emails the owner (and customer) an .ics file. Customers can cancel too. |
| Dashboard | Live message log, conversation threads with audio playback, manual replies, approval mode (drafts), human takeover, and editors for intents, rules, settings and appointments. A built-in simulator for rehearsals. |
| Safety | Every AI-proposed time is checked against real availability. Duplicate webhooks are dropped. Dashboard login is protected by a cookie with an anti-forgery token. Tokens go in headers, never in URLs. |

## Quick start (offline mode, about 2 minutes)

Requirements: [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and Visual Studio 2026 (or VS Code with C# Dev Kit / Rider).

```bash
git clone <this repo>
cd AI-receptionist
dotnet user-secrets set "Admin:Password" "choose-a-password" --project src/AiReceptionist.Web
dotnet run --project src/AiReceptionist.Web
```

Or open `AiReceptionist.sln` in Visual Studio and press **F5**. Browse to https://localhost:5001, sign in as `admin`,
and open **Simulator**. Try *"Hi, can I book an appointment tomorrow afternoon?"* and then *"yes please"*.

With no keys configured, the app runs in offline mode: the keyword engine replaces the AI, replies are text only,
and bookings fail gracefully (or land as `.eml` files if you set `Email:PickupDirectory`). Add credentials one
service at a time as described in **[docs/SETUP.md](docs/SETUP.md)**. The dashboard home page shows which services are active.

## Documentation

- **[docs/SETUP.md](docs/SETUP.md)**: step-by-step setup for Azure OpenAI, Azure AI Speech, Microsoft 365 / Entra ID, SMTP, the Meta app (Facebook, Instagram, WhatsApp), X, the public webhook URL and Azure App Service deployment.
- **[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)**: how a message flows through the system, the design decisions, and how to add a new channel.
- **[docs/CLIENT-ONBOARDING.md](docs/CLIENT-ONBOARDING.md)**: setting it up for a client: the discovery survey, which accounts the client owns and how they give access, the setup runbook, the customer journey and the handover checklist.
- **[docs/ACCEPTANCE-TEST.md](docs/ACCEPTANCE-TEST.md)**: the pre-flight checklist and the 15-minute acceptance script. It also includes a shot list for recording the demo video.

## Solution layout

```
AiReceptionist.sln
├─ src/AiReceptionist.Core            Domain model, EF Core DbContext, conversation orchestrator,
│                                     rules, scheduling (hours, slots, booking + fallback), offline engine
├─ src/AiReceptionist.Infrastructure  Azure OpenAI engine, Azure Speech TTS/STT, Microsoft Graph calendar,
│                                     SMTP sender, Meta + X channel connectors, DI registration
├─ src/AiReceptionist.Web             ASP.NET Core host: webhook endpoints, background workers,
│                                     Blazor Server admin dashboard, login
└─ tests/AiReceptionist.Tests         67 xUnit tests: booking flows, fallbacks, rules, parsers,
                                      signatures, OAuth 1.0a, full webhook→reply integration test
```

Run the tests with `dotnet test`.

## Known limitations

- **Platform approval.** While a Meta app is in *Development* mode, only people with a role on the app (and, for WhatsApp test numbers, up to 5 registered recipients) can message the bot. Serving the public requires Meta App Review. See SETUP.md §6.
- **X (Twitter) DMs** need a paid X API plan that includes Direct Message endpoints. Messages are polled every 60 s by default because X only offers DM webhooks on Enterprise plans.
- **Scale.** The app uses one instance with SQLite and an in-memory queue. That suits a single business. To scale out, swap in SQL Server/PostgreSQL and a durable queue; the interfaces are already in place (see ARCHITECTURE.md).
- **Voice-note transcription** only covers WhatsApp's OGG/Opus notes. Audio sent on Messenger or Instagram is stored and flagged for a human.
