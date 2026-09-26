# Setup guide

This guide takes you from a fresh clone to a receptionist that answers live DMs on Facebook, Instagram,
WhatsApp and X, speaks its replies, and writes appointments into your Microsoft 365 calendar.

Each service is optional and independent. The dashboard home page shows a green **active** badge as each one is
configured, and you can test each piece on its own before the next. Suggested order:

1. [Run locally](#1-run-locally)
2. [Public HTTPS URL for webhooks](#2-public-https-url-for-webhooks)
3. [Azure OpenAI (language understanding)](#3-azure-openai)
4. [Azure AI Speech (voice)](#4-azure-ai-speech)
5. [Microsoft 365 calendar (Microsoft Graph)](#5-microsoft-365-calendar)
6. [Email fallback (SMTP)](#6-email-fallback-smtp)
7. [Meta app: Facebook Messenger, Instagram, WhatsApp](#7-meta-app-facebook-instagram-whatsapp)
8. [X (Twitter) DMs](#8-x-twitter-direct-messages)
9. [Deploy to Azure App Service](#9-deploy-to-azure-app-service)
10. [Configuration reference](#10-configuration-reference)

> **Where do secrets go?** Never commit keys to `appsettings.json`. For local development, use
> `dotnet user-secrets` (examples below; they load when `ASPNETCORE_ENVIRONMENT=Development`, which the Visual Studio
> launch profile sets) or a git-ignored `src/AiReceptionist.Web/appsettings.Local.json`. In Azure, use App Service
> *Environment variables*, and Key Vault references if you like. Environment variables use `__` in place of `:`,
> e.g. `OpenAI__ApiKey`.

---

## 1. Run locally

1. Install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). With Visual Studio, install 2022 17.8 or later with the *ASP.NET and web development* workload.
2. Trust the development HTTPS certificate (one time): `dotnet dev-certs https --trust`
3. Set a dashboard password:
   ```bash
   cd src/AiReceptionist.Web
   dotnet user-secrets set "Admin:Password" "a-strong-password"
   ```
   If you skip this step, a temporary password is generated at start-up and printed in the console log.
4. Run it: open `AiReceptionist.sln` and press **F5**, or run `dotnet run --project src/AiReceptionist.Web`.
5. Browse to https://localhost:5001 and sign in as `admin`.
6. Open **Settings** and set your business name, description, **time zone**, opening hours and appointment length. Click **Check availability now**.
7. Open **Simulator** and chat. The offline keyword engine answers until you configure Azure OpenAI.

The SQLite database (`data/receptionist.db`) and generated audio (`data/media`) are created next to the web project.
Delete the `data` folder to reset everything.

## 2. Public HTTPS URL for webhooks

Meta must reach your app over **public HTTPS**, and it must also be able to download the voice clips the app generates.
- **Local development**: use a tunnel.
  - Visual Studio Dev Tunnels: *View → Other Windows → Dev Tunnels*. Create a **persistent**, **public** tunnel, select it in the run dropdown, and start debugging. Or use the CLI: `devtunnel host -p 5001 --allow-anonymous`.
  - Or ngrok: `ngrok http https://localhost:5001`
- **Production**: your App Service URL (section 9) or a custom domain.

Set the URL. Voice clip links are built from it:

```bash
dotnet user-secrets set "App:PublicBaseUrl" "https://your-tunnel-or-domain.example"
```

The webhook endpoints are:

| Channel | Callback URL |
|---|---|
| Facebook Messenger | `https://<public-url>/webhooks/facebook` |
| Instagram | `https://<public-url>/webhooks/instagram` |
| WhatsApp | `https://<public-url>/webhooks/whatsapp` |
| X (Twitter) | none (polled) |

`/webhooks/*`, `/media/*` and `/health` are anonymous. Everything else requires the dashboard login.

## 3. Azure OpenAI

1. In the [Azure portal](https://portal.azure.com), create an **Azure OpenAI** resource, or an *Azure AI Foundry* resource that includes Azure OpenAI.
2. Open it in **Azure AI Foundry** → *Deployments* → **Deploy model**, and choose a chat model that supports JSON output, such as `gpt-4.1-mini`. Note the **deployment name**.
3. Under *Keys and Endpoint*, copy **Endpoint** and **Key 1**.
4. Configure:
   ```bash
   dotnet user-secrets set "OpenAI:Endpoint"   "https://<resource>.openai.azure.com/"
   dotnet user-secrets set "OpenAI:ApiKey"     "<key>"
   dotnet user-secrets set "OpenAI:Deployment" "gpt-4.1-mini"
   ```
   To use OpenAI directly instead, set `OpenAI:Provider` = `OpenAI`, set `OpenAI:ApiKey`, and set `OpenAI:Deployment` to the model name. Leave the endpoint empty.
5. Restart the app. The dashboard should show *Language understanding: AzureOpenAI (gpt-4.1-mini)*. Re-run the simulator conversation.

**How the AI is steered.** The system prompt is assembled at run time from **Settings** (persona, business description,
hours, time zone) and **Intents** (name, description, examples, guidance), plus the live free time from your calendar.
The model returns JSON (`intent`, `confidence`, `reply`, `proposedSlotStart`, ...). The app does the booking itself and
never trusts the model's claim that a slot is free.

## 4. Azure AI Speech

1. In the Azure portal, create a **Speech service** resource (Azure AI services). The F0 free tier works for testing.
2. Under *Keys and Endpoint*, copy **Key 1** and the **Location/Region** (e.g. `eastus`).
3. Configure:
   ```bash
   dotnet user-secrets set "Speech:Key"    "<key>"
   dotnet user-secrets set "Speech:Region" "eastus"
   ```
4. In **Settings → Voice**, choose a neural voice (e.g. `en-US-AvaMultilingualNeural`, `en-GB-SoniaNeural`, `en-IN-NeerjaNeural`; see the
   [voice gallery](https://speech.microsoft.com/portal/voicegallery)) and click **Test voice**.
5. Choose which replies are spoken:
   - **Intents → Reply as**: `Text`, `Voice` or `TextAndVoice` per intent. Greeting, ConfirmAppointment and CancelAppointment default to `TextAndVoice`.
   - **Settings → Answer voice notes with voice**: a customer who sends a voice note gets a spoken reply.
   - **Conversation page → Send a reply**: pick the mode for a manual reply.

The same key enables transcription of inbound WhatsApp voice notes (`Speech:RecognitionLanguage`, default `en-US`).

## 5. Microsoft 365 calendar

The app writes events with **Microsoft Graph** using an Entra ID app registration with *application* permissions
(client-credentials flow), so no user has to stay signed in.

1. In the [Entra admin center](https://entra.microsoft.com), go to *Identity → Applications → App registrations* → **New registration**.
   - Name: `AI Receptionist`. Accounts: *Single tenant*. No redirect URI.
   - Copy the **Application (client) ID** and **Directory (tenant) ID**.
2. *Certificates & secrets* → **New client secret**. Copy the secret **value** now; you can't view it again later.
3. *API permissions* → **Add a permission** → *Microsoft Graph* → **Application permissions** → `Calendars.ReadWrite` → **Grant admin consent**.
4. **Recommended: limit the app to one mailbox.** Without this step, `Calendars.ReadWrite` covers every mailbox in the tenant. In Exchange Online PowerShell:
   ```powershell
   Connect-ExchangeOnline
   # Mail-enabled security group that contains only the receptionist/owner mailbox
   New-DistributionGroup -Name "AI Receptionist Calendars" -Type Security -Members owner@contoso.com
   New-ApplicationAccessPolicy -AppId <client-id> -PolicyScopeGroupId "AI Receptionist Calendars" `
       -AccessRight RestrictAccess -Description "AI receptionist: owner calendar only"
   Test-ApplicationAccessPolicy -AppId <client-id> -Identity owner@contoso.com   # -> Granted
   ```
   Microsoft's newer *RBAC for Applications in Exchange Online* also works. If you use it, assign the
   `Application Calendars.ReadWrite` role scoped to that mailbox **instead of** the tenant-wide consent in step 3.
5. Configure. `CalendarUser` is the mailbox whose calendar receives appointments:
   ```bash
   dotnet user-secrets set "Microsoft365:TenantId"     "<tenant-id>"
   dotnet user-secrets set "Microsoft365:ClientId"     "<client-id>"
   dotnet user-secrets set "Microsoft365:ClientSecret" "<secret-value>"
   dotnet user-secrets set "Microsoft365:CalendarUser" "owner@contoso.com"
   ```
6. Restart the app and click **Settings → Check availability now**. It should say *Microsoft 365 calendar read OK*.
   Existing meetings are now excluded from the offered slots.

Events are created with category *AI Receptionist*, a 15-minute reminder, a transcript of the conversation in the body,
and a Graph `transactionId` so retries never create duplicates. If the customer shared an email address,
they are added as an attendee and receive an Outlook invitation. Turn this off with `Microsoft365:InviteCustomer=false`.

## 6. Email fallback (SMTP)

The fallback is used when Graph is unconfigured, unreachable, or returns an error. The owner (and the customer,
if their email is known) receives a confirmation email with an `.ics` attachment, and the appointment is marked
**EmailFallback** in the dashboard.

The fallback uses plain SMTP, so it works even when Microsoft 365 is the thing that failed. Any provider works:

| Provider | Host / port | Notes |
|---|---|---|
| SendGrid, Mailgun, Postmark, Azure Communication Services Email | per provider, 587 | Recommended for reliable automated email. |
| Microsoft 365 SMTP AUTH | `smtp.office365.com` / 587 | SMTP AUTH must be enabled on the sending mailbox. Microsoft is retiring Basic auth for SMTP AUTH, so check its current status for your tenant. |
| Gmail / Google Workspace | `smtp.gmail.com` / 587 | Requires an app password. |

```bash
dotnet user-secrets set "Email:Host"         "smtp.sendgrid.net"
dotnet user-secrets set "Email:Port"         "587"
dotnet user-secrets set "Email:Security"     "StartTls"      # or SslOnConnect (465) / None
dotnet user-secrets set "Email:Username"     "apikey"
dotnet user-secrets set "Email:Password"     "<password or API key>"
dotnet user-secrets set "Email:FromAddress"  "receptionist@contoso.com"
dotnet user-secrets set "Email:OwnerAddress" "owner@contoso.com"
```

Click **Settings → Send test email** to check it.
**Local testing without SMTP:** leave `Email:Host` empty and set `Email:PickupDirectory` to e.g. `data/outbox`.
Emails are then written there as `.eml` files, which you can open in Outlook.

**How to force the fallback during a test:** temporarily set `Microsoft365:ClientSecret` to a wrong value and restart.
Booking still succeeds, by email.

## 7. Meta app (Facebook, Instagram, WhatsApp)

All three channels run through one Meta app.

### 7.1 Create the app

1. At https://developers.facebook.com/apps, click **Create app**. Choose the use cases for *Messenger*, *Instagram* and *WhatsApp*, or create a **Business** app and add those products. Link it to your Meta Business portfolio.
2. *App settings → Basic*: copy the **App secret**. Every webhook call is verified with it (`X-Hub-Signature-256`).
3. Make up a random **verify token**, e.g. `openssl rand -hex 16`.
   ```bash
   dotnet user-secrets set "Meta:AppSecret"   "<app secret>"
   dotnet user-secrets set "Meta:VerifyToken" "<random string>"
   ```
4. The public URL from section 2 must be live **before** you click *Verify and save* in the steps below. Meta calls the endpoint immediately.

> **Development vs Live mode.** Until the app passes App Review for the needed permissions (Advanced Access) and is
> switched to *Live*, **only people with a role on the app** (admin, developer or tester) can message the bot and get
> replies. Add your test accounts under *App roles → Roles* before the acceptance test.

### 7.2 Facebook Messenger

1. You need a Facebook **Page** that you admin.
2. Open *Messenger → Messenger API Settings* (in newer dashboards: *Use cases → Engage with customers on Messenger → Customize*).
   - **Access tokens**: connect your Page and click **Generate token**. The app needs the `pages_messaging` and `pages_manage_metadata` permissions.
     For production, use a long-lived token, e.g. a System User token from Business Settings.
   - **Webhooks**: Callback URL `https://<public-url>/webhooks/facebook`, Verify token = your `Meta:VerifyToken` → **Verify and save**.
   - Subscribe the Page to the webhook field **`messages`**.
3. Configure:
   ```bash
   dotnet user-secrets set "Meta:Facebook:PageAccessToken" "<page token>"
   ```
4. Test: from an account with an app role, message the Page. The reply should arrive within a few seconds, and voice replies arrive as audio attachments.

### 7.3 Instagram

1. Convert the Instagram account to a **Professional** (Business or Creator) account and **link it to the Facebook Page**.
2. In the Instagram app, go to *Settings → Messages and story replies → Message controls → Connected tools* and enable **Allow access to messages**.
3. There are two API options:
   - **Messenger API for Instagram (default).** Use a Page access token that has `instagram_basic` and `instagram_manage_messages`.
     Under *Webhooks*, select the **Instagram** object, set the callback URL to `https://<public-url>/webhooks/instagram` with the same verify token, and subscribe to **`messages`**.
     ```bash
     dotnet user-secrets set "Meta:Instagram:AccessToken" "<page token with instagram_manage_messages>"
     ```
   - **Instagram API with Instagram Login.** Generate the Instagram User token in *Instagram → API setup with Instagram login*, configure the webhook there (field `messages`), then set:
     ```bash
     dotnet user-secrets set "Meta:Instagram:AccessToken" "<instagram user token>"
     dotnet user-secrets set "Meta:Instagram:ApiBaseUrl"  "https://graph.instagram.com"
     ```
4. Test by sending a DM to the Instagram account from a tester account.

### 7.4 WhatsApp (Cloud API)

1. *WhatsApp → API Setup*: Meta provides a **test phone number**. Copy its **Phone number ID** (not the phone number itself).
   Add up to 5 recipient numbers under *To* and verify them. Test numbers can only message those recipients.
2. For a permanent token: in *Business Settings → Users → System users*, add a system user, assign the app and the WhatsApp account,
   then **Generate token** with `whatsapp_business_messaging` and `whatsapp_business_management`. The temporary token on the API Setup page expires after 24 hours.
3. *WhatsApp → Configuration → Webhook*: Callback URL `https://<public-url>/webhooks/whatsapp` plus the verify token → **Verify and save**,
   then **Manage** → subscribe to **`messages`**.
4. Configure:
   ```bash
   dotnet user-secrets set "Meta:WhatsApp:AccessToken"   "<system user token>"
   dotnet user-secrets set "Meta:WhatsApp:PhoneNumberId" "<phone number id>"
   ```
5. Test: from a verified recipient phone, message the test number. You can only send free-form replies within 24 hours of the customer's last message.
   The bot always replies inside that window. Voice replies arrive as playable voice notes (OGG/Opus), and your own voice notes are transcribed.

To go live with your own number, add and verify it under *WhatsApp → API Setup → Add phone number*, then update `PhoneNumberId`.

## 8. X (Twitter) Direct Messages

1. At https://developer.x.com, open the developer portal and create a **Project** and an **App**. Reading and sending DMs needs an X API plan that includes the
   Direct Message endpoints, which the free tier does not. Check the current plans on the portal.
2. *App settings → User authentication settings* → **Set up**:
   - App permissions: **Read and write and Direct message**
   - Type of app: *Web App, Automated App or Bot*
   - Callback URI / Website URL: your public URL (required by the form but not used)
3. *Keys and tokens*: copy the **API Key and Secret**, then generate an **Access Token and Secret** for the business account.
   Generate the access token **after** setting the permissions. If you change permissions later, regenerate it.
4. Configure:
   ```bash
   dotnet user-secrets set "Twitter:ConsumerKey"       "<API key>"
   dotnet user-secrets set "Twitter:ConsumerSecret"    "<API key secret>"
   dotnet user-secrets set "Twitter:AccessToken"       "<access token>"
   dotnet user-secrets set "Twitter:AccessTokenSecret" "<access token secret>"
   # optional: Twitter:PollSeconds (default 60, minimum 15)
   ```
5. Restart the app. On its first poll it records the newest DM without answering the backlog. After that, new DMs are answered within one poll interval.
   Voice replies on X are sent as text plus a link to the MP3, because X DMs can't carry audio attachments.

Polling is used because X offers DM webhooks (Account Activity API) only on Enterprise plans. To add webhook support,
implement `IWebhookChannel` on `TwitterDmChannel` (see ARCHITECTURE.md).

## 9. Deploy to Azure App Service

1. Create a **Web App**: publish *Code*, runtime stack **.NET 8 (LTS)**, Windows or Linux, B1 or higher.
2. *Configuration → General settings*: turn **Always On** on (keeps background workers and X polling alive) and **Web sockets** on (for Blazor Server).
3. *Environment variables*: add every setting from sections 3–8 using `__`, e.g. `OpenAI__ApiKey`, `Meta__WhatsApp__PhoneNumberId`. Also set:
   | Name | Value |
   |---|---|
   | `App__PublicBaseUrl` | `https://<app>.azurewebsites.net` |
   | `Admin__Password` | a strong password |
   | `ConnectionStrings__Receptionist` | Linux: `Data Source=/home/data/receptionist.db` · Windows: `Data Source=D:\home\data\receptionist.db` |
   | `Media__Directory` | Linux: `/home/data/media` · Windows: `D:\home\data\media` |
4. Deploy. In Visual Studio: right-click `AiReceptionist.Web` → **Publish** → Azure App Service. From the CLI:
   ```bash
   dotnet publish src/AiReceptionist.Web -c Release -o publish
   cd publish && zip -r ../app.zip . && cd ..
   az webapp deploy --resource-group <rg> --name <app> --src-path app.zip --type zip
   ```
5. Keep the app on **one instance**. It uses SQLite and an in-memory queue. See ARCHITECTURE.md for scaling out.
6. Update the three Meta webhook callback URLs to the App Service URL.

## 10. Configuration reference

| Key | Default | Description |
|---|---|---|
| `App:PublicBaseUrl` | `https://localhost:5001` | Public HTTPS base URL (used for voice clip links). |
| `Admin:Username` / `Admin:Password` | `admin` / *(random, logged)* | Dashboard login. |
| `ConnectionStrings:Receptionist` | `Data Source=data/receptionist.db` | SQLite database. |
| `OpenAI:Provider` | `AzureOpenAI` | `AzureOpenAI` or `OpenAI`. |
| `OpenAI:Endpoint` / `ApiKey` / `Deployment` / `Temperature` | | Language model settings. If no key is set, the offline engine is used. |
| `Speech:Key` / `Region` / `RecognitionLanguage` | | Azure AI Speech. If unset, replies are text only. |
| `Media:Directory` | `data/media` | Where generated audio is stored (served at `/media`). |
| `Microsoft365:TenantId` / `ClientId` / `ClientSecret` / `CalendarUser` / `InviteCustomer` | | Graph calendar. If unset, bookings use email. |
| `Email:Host` / `Port` / `Security` / `Username` / `Password` / `FromAddress` / `FromName` / `OwnerAddress` / `PickupDirectory` | | Email fallback. |
| `Meta:AppSecret` / `VerifyToken` / `GraphApiVersion` / `GraphBaseUrl` | `v21.0`, `https://graph.facebook.com` | Shared Meta settings. |
| `Meta:Facebook:PageAccessToken` | | Messenger. |
| `Meta:Instagram:AccessToken` / `ApiBaseUrl` | | Instagram. |
| `Meta:WhatsApp:AccessToken` / `PhoneNumberId` | | WhatsApp Cloud API. |
| `Twitter:ConsumerKey` / `ConsumerSecret` / `AccessToken` / `AccessTokenSecret` / `UserId` / `PollSeconds` / `ApiBaseUrl` | `60`, `https://api.x.com` | X DMs (OAuth 1.0a user context). |

Everything about *behaviour* (persona, business info, hours, time zone, reply templates, approval mode, voice,
confidence threshold, intents, rules) lives in the database and is edited in the dashboard, not in configuration files.

### Troubleshooting

| Symptom | Check |
|---|---|
| Meta says *"The URL couldn't be validated"* | The tunnel or app is running, `Meta:VerifyToken` matches, the channel's token is set (unconfigured channels return 404), and the URL is HTTPS. |
| Webhook calls are logged as *invalid signature* | `Meta:AppSecret` is the secret of the **same** app that sends the webhooks. |
| Messages arrive but get no reply | Open the conversation in the dashboard. Is the bot paused (human takeover), or is **Require approval** on? Look for red errors on the reply. |
| Messenger/Instagram error `(#10) ... outside of allowed window` or permission errors | The sender needs an app role while the app is in Development mode, and the Page token needs the listed permissions. |
| Voice replies arrive as text | Speech isn't configured, or the synthesis failed (the error is shown under the message). Meta also needs `App:PublicBaseUrl` to be publicly reachable. |
| Calendar says *not read* | Check the Entra secret and its expiry, admin consent, the application access policy (`Test-ApplicationAccessPolicy`), and the `CalendarUser` UPN. |
| X polling errors `429` | You hit a rate limit: raise `Twitter:PollSeconds`. A `403` means the app lacks DM permission or the plan doesn't include DMs. Regenerate the access token after changing permissions. |
