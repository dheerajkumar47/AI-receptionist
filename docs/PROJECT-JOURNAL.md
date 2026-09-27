# Project journal: what was set up, how to use it, how to fix it

The owner's personal record of the setup, in the order it was done. No secrets are stored here.
All keys and tokens live in **user-secrets** (`dotnet user-secrets list --project src/AiReceptionist.Web`).

**Business:** Dheeraj Software Solutions, a software services company (Pakistan). The bot answers DMs, explains
services, never quotes prices, and books a **free 30-minute consultation call**.

---

## Status

| # | Part | Status | Provider |
|---|---|---|---|
| 0 | Local run (VS Code, .NET 10) | ✅ | — |
| 1 | Business settings | ✅ | Dashboard → Settings |
| 2 | Intents (software business) | ✅ | Dashboard → Intents → *Restore recommended intents* |
| 3 | AI understanding and replies | ✅ | Azure OpenAI, deployment `gpt-4.1-mini` |
| 4 | Voice replies | ✅ | Azure AI Speech (F0 free tier) |
| 5 | Calendar booking | ✅ | Personal Outlook.com calendar via Microsoft Graph (SignInMode = Personal) |
| 6 | Email fallback | ✅ | Gmail SMTP with an App Password |
| 7 | Public URL | ✅ | Microsoft Dev Tunnel `ai-receptionist` → `https://<tunnel-id>-5000.<region>.devtunnels.ms` |
| 8 | WhatsApp | ✅ | Meta WhatsApp Cloud API, test number, app **not published** |
| 9 | Facebook Messenger | ✅ | Same Meta app, Page "Dheeraj Software Solutions", app **not published** |
| 10 | Instagram | ✅ connected (test webhook) | "Instagram API with Instagram Login". Real DMs need a **published** app (Instagram enforces it; WhatsApp and Messenger don't) |
| 11 | X (Twitter) | ⏸ skipped | Needs a paid X API plan |
| 12 | Deploy to Azure App Service | optional | docs/SETUP.md §9 |
| 13 | Demo recording | ⏳ | docs/ACCEPTANCE-TEST.md |

---

## Daily use

**Start (two terminals in VS Code, from `D:\AI-receptionist`):**

| Terminal | Command | Purpose |
|---|---|---|
| 1 (app) | `dotnet run --project src/AiReceptionist.Web --launch-profile AiReceptionist.Web` | The receptionist, dashboard and webhooks |
| 2 (tunnel) | `devtunnel host ai-receptionist` | Makes the app reachable by Meta |

- Dashboard: https://localhost:5001 (or the tunnel URL), user `admin`.
- Stop: **Ctrl+C** in each terminal.
- Get code updates: `git pull`, then restart terminal 1.
- **Always stop the app before changing secrets.** Paste long tokens in two steps so PowerShell doesn't split them:
  ```powershell
  $t = "PASTE_VALUE"
  dotnet user-secrets set "Section:Key" $t --project src/AiReceptionist.Web
  ```
- See what is configured: `dotnet user-secrets list --project src/AiReceptionist.Web`. **This prints secrets; never share it.**

**Dashboard pages:**

| Page | Use it to |
|---|---|
| Dashboard | See which services and channels are *active*; recent messages; the Outlook sign-in code if one is needed |
| Conversations | Open a chat, read the AI's intent and confidence, **send a manual reply** (text or voice), **Take over** (pause the bot) / **Resume bot** |
| Message log | Every message on every channel, with errors in red |
| Appointments | All bookings, their status (Booked / EmailFallback / Failed) and cancel |
| Simulator | Chat as a fake customer: the full pipeline without any social platform |
| Intents | What customers want and how the bot reacts; edits apply to the next message |
| Rules | Keyword/regex rules checked before the AI (e.g. "urgent" → human) |
| Settings | Business info, hours, time zone, reply templates, voice, approval mode, **Check availability**, **Test voice**, **Send test email**, **Start fresh** (clear all chats) |

**Start fresh (clear all test chats):** Settings → *Start fresh* → **Delete all conversation history…** → **Yes, delete everything**.
It deletes contacts, conversations, messages and appointment records (tick the box to also delete saved voice files).
It **keeps** settings, intents, rules, secrets and the Outlook sign-in. Events already booked stay in Outlook: delete them there.
Don't delete the `data` folder instead: that also removes the Outlook sign-in (`outlook-token-cache.bin`) and your Settings page values.

**Public pages:** `/health` (tunnel check), `/privacy` (privacy policy used for the Meta app).

---

## Setup record, in order

### 0. Local run
1. Installed the .NET 8 SDK (upgraded to **.NET 10 SDK** on 27 Sep 2026, because .NET 8 support ends 10 Nov 2026) and the VS Code **C# Dev Kit** extension; cloned the repo to `D:\AI-receptionist`.
2. `dotnet dev-certs https --trust`
3. `dotnet user-secrets set "Admin:Password" "<password>" --project src/AiReceptionist.Web`
4. Tests: `dotnet test` (all pass).

### 1. Business settings (Dashboard → Settings)
Business name, description (services, tech stack, "quote after free consultation", contact), persona prompt,
time zone **`Asia/Karachi`**, hours **`Mon-Fri 10:00-19:00; Sat 11:00-15:00`**, 30 min, 14 days ahead, 120 min notice.
Calendar subject: `Consultation call with {fullname} ({channel})`.
Placeholders are typed literally with braces, and the app fills them in: `{name}`, `{fullname}`, `{slot}`, `{slots}`, `{business}`, `{channel}`, `{hours}`.

### 2. Intents
Dashboard → Intents → **Restore recommended intents**. This loads Greeting, BusinessInfo, PricingQuote, BookAppointment,
ConfirmAppointment, CancelAppointment, HumanAgent and Other, tuned for a software business.

### 3. Azure OpenAI
1. Signed in to https://portal.azure.com with the personal Microsoft account. It had no subscription at first, so the free trial was activated (**Azure subscription 1**).
2. Created an **Azure OpenAI** resource (resource group `ai-receptionist`, Standard S0). Tags page: leave empty.
3. Foundry → Deployments → deployed **`gpt-4.1-mini`**. (`gpt-4o-mini` is retired for new deployments.)
4. Secrets: `OpenAI:Endpoint`, `OpenAI:ApiKey`, `OpenAI:Deployment` = **exact deployment name**.

### 4. Azure Speech
Created a **Speech services** resource (Free F0, East US). Secrets: `Speech:Key`, `Speech:Region` (e.g. `eastus`).
Voice is set in Settings → Voice. This is a separate resource from OpenAI; the chat model cannot speak.

### 5. Personal Outlook calendar
1. The Microsoft account was registered with a Gmail address, so Outlook asked to **create an @outlook.com alias**. That's required for a calendar and is the same account.
2. Azure portal → **App registrations** → New: *Personal Microsoft accounts only*, no redirect URI.
3. **Allow public client flows = Yes**. In the new "Authentication (Preview)" page this is under the Settings tab, or set `"isFallbackPublicClient": true` in **Manifest**.
4. API permissions → Microsoft Graph → **Delegated** → `Calendars.ReadWrite`.
5. Secrets: `Microsoft365:SignInMode` = `Personal`, `Microsoft365:ClientId` = application (client) ID.
6. Start the app, open https://www.microsoft.com/link, enter the code from the terminal or Dashboard, and sign in. The login is saved in
   `src/AiReceptionist.Web/data/outlook-token-cache.bin` (encrypted for your Windows user), so restarts don't ask again.

### 6. Gmail email fallback
Google account → 2-Step Verification → **App passwords** → create one (16 letters, no spaces). Secrets:
`Email:Host=smtp.gmail.com`, `Email:Port=587`, `Email:Username`, `Email:Password` (the app password),
`Email:FromAddress`, `Email:OwnerAddress`; removed `Email:PickupDirectory`.

### 7. Dev tunnel (public HTTPS URL)
```powershell
winget install Microsoft.devtunnel
devtunnel user login
devtunnel create ai-receptionist --allow-anonymous
devtunnel port create ai-receptionist -p 5000
devtunnel host ai-receptionist
```
URL: `https://<tunnel-id>-5000.<region>.devtunnels.ms` (use the `-5000` form). Secret: `App:PublicBaseUrl` = that URL, with no trailing `/`.
"Connection refused" in the tunnel terminal just means the app (terminal 1) isn't running.

### 8. WhatsApp (Meta Cloud API)
1. developers.facebook.com → Create app → use case **Connect with customers through WhatsApp** → business portfolio *Dheeraj Software Solutions*.
2. WhatsApp → **API Setup (Step 1. Try it out)**: test number, **Phone number ID**, **WhatsApp Business Account ID**, access token; added own phone to the **To** list.
3. App settings → Basic: **App secret**. Made up a **verify token**.
4. Secrets: `Meta:AppSecret`, `Meta:VerifyToken`, `Meta:WhatsApp:AccessToken`, `Meta:WhatsApp:PhoneNumberId`.
5. Step 2. Production setup → **Configure Webhooks**: Callback `https://<tunnel-id>-5000.<region>.devtunnels.ms/webhooks/whatsapp` plus the verify token → green tick.
6. **The fix that made real messages arrive:** link the WhatsApp account to the app:
   ```powershell
   $token = "<access token>"; $waba = "<WhatsApp Business Account ID>"
   Invoke-RestMethod -Method Post -Uri "https://graph.facebook.com/v21.0/$waba/subscribed_apps" -Headers @{Authorization="Bearer $token"}
   ```
7. **Permanent token:** business.facebook.com → Settings → System users → `ai-receptionist-bot` (Admin) → assign app and WhatsApp account (full control) →
   Generate token (never expires; `whatsapp_business_messaging`, `whatsapp_business_management`) → saved as `Meta:WhatsApp:AccessToken`.
8. App Settings → Basic: Privacy Policy URL `https://<tunnel-id>-5000.<region>.devtunnels.ms/privacy`, app icon `docs/images/app-icon-1024.jpg`, category Business.
9. **Publishing is NOT needed for testing.** It requires business verification and is only for serving the public.
   Unpublished works for your own number plus up to 5 recipient numbers (API Setup → To → Manage phone number list).
   Don't submit App Review. If a review request was added by mistake, delete it with the trash icons.

### 9. Facebook Messenger
1. Created the Facebook Page **Dheeraj Software Solutions**.
2. Meta app → ✏️ Use cases → **Add use case** → *Engage with customers on Messenger from Meta* → **Customize**.
3. **Generate access tokens** → Connect the Page → Generate → secret `Meta:Facebook:PageAccessToken` (restart; the Dashboard shows Messenger *active*).
4. **Configure webhooks**: Callback `https://<tunnel-id>-5000.<region>.devtunnels.ms/webhooks/facebook`, Verify token = the **made-up word**
   saved as `Meta:VerifyToken` (**not** an access token).
5. Page → **Add subscriptions** → tick only **`messages`**.
6. Test: from your **personal** Facebook profile (not acting as the Page), open the Page → **Message** → "Hi". Text and audio reply ✅.

### 10. Instagram
1. Business account = personal Instagram switched to **Professional**, with *Allow access to messages* ON (Settings → Messages → Connected tools).
   A second Instagram account is used as the test "customer".
2. Meta app → Use cases → **Manage messaging & content on Instagram** → *API setup with Instagram login*:
   **Add all required permissions**; copy the **Instagram app secret** (different from the Meta app secret).
3. App roles → Roles → add **both** accounts as **Instagram Tester**; accept at `instagram.com/accounts/manage_access/` → Tester invites.
4. **Add account** (business) → Generate token. Secrets: `Meta:Instagram:AccessToken`, `Meta:Instagram:AppSecret`,
   `Meta:Instagram:ApiBaseUrl` = `https://graph.instagram.com`.
5. Webhook `https://<tunnel-id>-5000.<region>.devtunnels.ms/webhooks/instagram` + verify token → subscribe `messages`.
6. Link the account to the app (like WhatsApp's `subscribed_apps`):
   ```powershell
   $ig = "<instagram token>"
   Invoke-RestMethod -Method Post -Uri "https://graph.instagram.com/v21.0/me/subscribed_apps?subscribed_fields=messages&access_token=$ig"
   ```
7. Result: Meta's **Test** delivery (Configure webhooks → messages → Test) reaches the app ✅, but real DMs arrive only as
   `"read"` events while the app is unpublished. Real Instagram DMs start working once the app is published (business verification).

*Twilio (alternative WhatsApp provider):* the code is in the project (`Twilio:AccountSid` / `Twilio:AuthToken`), but Twilio signup
rejected the Pakistani phone numbers. **Leave the Twilio secrets empty**, or the app would switch WhatsApp to Twilio.

### 11. Azure deployment (live 24/7, no terminal)

**Done so far:** Web App `AI-receptionist` created (28 Sep 2026) in resource group `AI-receptionist_group`, Central India,
Linux, .NET 10 (LTS), Basic B1 (~13 USD/month), Application Insights on, no database/cache/VNet, continuous deployment off.
GitHub secret + variable added; first **Deploy to Azure** run succeeded (27 Sep 2026).
Secrets pasted into App settings; live dashboard `https://<your-app>.azurewebsites.net`
shows AI, Voice, Outlook, Email and Messenger/Instagram/WhatsApp **active**. Remaining: fill Settings, Outlook sign-in, move Meta webhooks.
(West US 3 failed with "No available instances"; the create wizard's optional PostgreSQL + Redis were removed: not used, ~$250/month.)

Follow docs/SETUP.md §9. Summary: Linux Web App (.NET 10 LTS, B1) → Always On + Web sockets + SCM Basic Auth on →
`scripts\export-appsettings.ps1` pastes all secrets into *Environment variables → Advanced edit* → publish profile as the
GitHub secret `AZURE_WEBAPP_PUBLISH_PROFILE` and app name as the variable `AZURE_WEBAPP_NAME` → run **Deploy to Azure**
in GitHub Actions → fill Settings and sign in to Outlook on the live dashboard → change the 3 Meta webhook URLs and the
privacy URL to `https://<app>.azurewebsites.net/...`. Every push to `main` then redeploys automatically.
Live data is in `/home/data` on the App Service (kept across restarts and deployments).

---

## Troubleshooting (every problem hit so far)

| Symptom | Cause | Fix |
|---|---|---|
| Azure: "account does not exist in tenant 'Microsoft Services'" | Opened a link for another tenant | Go directly to https://portal.azure.com |
| Azure: "Don't have a subscription" / credit info error | No subscription yet | Activate the free trial; check **Subscriptions** shows *Active* |
| Foundry: `ServiceModelDeprecating` | Model retired | Deploy `gpt-4.1-mini` (or `gpt-5-mini`) |
| Bot replies "A member of our team…" with intent **EngineError** | AI call failed | Open the conversation: the red error under the message says why |
| `DeploymentNotFound` (404) | `OpenAI:Deployment` doesn't match Foundry | Set it to the exact deployment name |
| Outlook asks for a new code on every check | (Fixed in code) token wasn't saved | `git pull`, then sign in once more |
| "Calendar not read" in Settings | Not signed in, or the sign-in expired | Dashboard shows the code → microsoft.com/link |
| `Missing value for option 'project'` | Long token split the pasted command | Use the `$t = "..."` two-step method |
| Login page crash (antiforgery) | (Fixed in code) form re-submitted | `git pull` |
| Tunnel: "connection refused" | App not running | Start terminal 1 |
| WhatsApp webhook verified but no messages | WhatsApp account not linked to the app | Run the `subscribed_apps` command (step 8.6) |
| WhatsApp: `0 message(s) queued` lines | Delivery/read receipts, which is normal | Ignore; the payload is logged below the line |
| WhatsApp replies stop after a day | Temporary 24-hour token | Use the permanent system-user token (step 8.7) |
| WhatsApp text arrives but no voice note | WhatsApp rejected the audio *link* | (Fixed in code) audio is now uploaded to WhatsApp and sent by media id: `git pull` |
| Voice note on Messenger/Instagram gets "team will contact you" | Messenger audio is MP4, which the old speech-to-text rejected | (Fixed in code) Azure fast transcription handles MP4/AAC/OGG/MP3: `git pull`. Unclear notes now get "could you type it instead?" |
| Customers speak Urdu/English mixed | — | Set `Speech:RecognitionLanguage` to `en-US,ur-IN` (auto-detects between them) |
| Messenger "Verify and save" error | Access token pasted as verify token, or Messenger not *active* | Use the made-up `Meta:VerifyToken` word; save the Page token and restart first |
| Instagram: only `"read"` events, never messages | Instagram delivers real DMs only to published apps | Use the Test delivery for demos; publish later |
| Instagram verify fails, tunnel says "refused" | App wasn't running during verification | Start the app, test `/webhooks/instagram?hub.mode=subscribe&hub.verify_token=…&hub.challenge=12345` in a browser (should print 12345), retry |
| Meta "Publish" button grey | Needs business verification | Not needed for testing; skip |
| Dashboard parts cut off / page scrolls sideways | (Fixed in code) layout wasn't responsive | `git pull`, restart, then **Ctrl+F5** in the browser. Wide tables now scroll inside their card; on a phone the menu moves to the top |
| Azure create screen: runtime, OS, continuous deployment | Portal defaults | Choose **.NET 10 (LTS)** (the app targets .NET 10), **Linux** (Outlook sign-in storage), and **Disable** continuous deployment (the repo already has its own workflow) |
| `dotnet run` says "A compatible .NET SDK was not found" (needs 10.0) | Project moved to .NET 10 | Install the .NET 10 SDK from dotnet.microsoft.com/download/dotnet/10.0, reopen VS Code |
| Azure: GitHub **Deploy to Azure** fails at *webapps-deploy* | Publish profile missing/old, or SCM Basic Auth off | Turn SCM Basic Auth on, download the profile again, update the secret, re-run |
| Azure: dashboard loads but buttons do nothing | Web sockets off | App Service → Configuration → Web sockets **On** |
| Azure: app shows an error page after deploy | A setting is missing or mistyped | App Service → *Log stream*; check the names use `__` (e.g. `OpenAI__ApiKey`) |
| See whether Meta is calling at all | — | Open `https://<tunnel-id>-5000-inspect.<region>.devtunnels.ms` |

---

## Next steps

1. **Demo recording** (docs/ACCEPTANCE-TEST.md shot list): WhatsApp and Messenger live with voice, Outlook event, dashboard,
   and Instagram via Meta's Test delivery.
2. Deploy to Azure App Service (step 11 above / docs/SETUP.md §9) so it runs 24/7 without your PC.
3. Optional later: business verification → publish the Meta app → Instagram DMs from anyone.
