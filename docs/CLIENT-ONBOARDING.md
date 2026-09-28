# Client onboarding: from signed deal to live receptionist

This guide covers setting up the AI Receptionist for a paying client, from three points of view:

| Who | Role |
|---|---|
| **You** (provider, Dheeraj Software Solutions) | Set up, host and support the receptionist |
| **Client** (the business owner, e.g. a clinic) | Owns the accounts, uses the dashboard, meets the customers |
| **Customer** (the client's customer) | Sends DMs on WhatsApp, Messenger or Instagram and books appointments |

Each client gets **their own copy** of the app (their own web app, database, settings and accounts). Several low-traffic
clients can share one Azure App Service plan, so an extra client costs little.

---

## 1. Discovery survey (send to the client before setup)

Send these questions as a form or WhatsApp message. The answers fill the dashboard **Settings** and **Intents** pages.

**A. Business**
1. Business name (as customers should see it)?
2. What do you offer? List your services, one line each.
3. Address or service area? Online, on-site, or both?
4. Policies customers often ask about (parking, payment methods, refunds, what to bring)?
5. Should the bot mention prices? If yes, which prices exactly? (The bot never invents prices.)

**B. Appointments**
6. What is booked (consultation, visit, class, call)? How long is one appointment?
7. Opening days and hours? Time zone (e.g. Asia/Karachi)?
8. How far ahead can customers book (e.g. 14 days)? Minimum notice (e.g. 2 hours)?
9. Which calendar do you use: Microsoft 365 (work account), Outlook.com (personal), or Google Calendar?
   (Google Calendar is not built in yet: bookings then arrive by email with an .ics file.)
10. Which email address should receive booking notifications?

**C. Channels**
11. Which channels do you want: WhatsApp, Facebook Messenger, Instagram, X?
12. WhatsApp: which phone number will the bot use? (It must not be active in the normal WhatsApp app, or it must be moved.)
13. Do you have a Facebook Page and an Instagram *professional* account? Are they linked?
14. Is your business verified in Meta Business Manager (needed for Instagram DMs and to message the public)?

**D. Tone and handoff**
15. Languages your customers write in (English, Urdu, Roman Urdu…)? Formal or friendly tone?
16. Text only, or voice replies too? Male or female voice?
17. When should a human take over (complaints, urgent cases, price negotiation)? Who answers, and how fast?
18. The 10 most common questions customers ask, with your preferred answers.

**E. Admin**
19. Who needs dashboard access (name, email)?
20. Hosting: in your own Azure account (you pay Azure directly), or hosted by us (included in the monthly fee)?

---

## 2. Connecting accounts: the Connections page

Every account is connected from the dashboard's **Connections** page: no code, no Azure settings, no terminal.

| Card | What the client (or you) does |
|---|---|
| WhatsApp, Messenger, Instagram | Paste the token and IDs → **Test** → **Link / Subscribe** (the one-time Meta call that makes messages arrive) |
| Meta app & webhooks | Paste the app secret; **Generate verify token**; **Copy** the callback URLs into the Meta app |
| Outlook calendar | Choose Outlook.com, **Save & apply**, then sign in with the code shown on the card (microsoft.com/link). *Disconnect* switches accounts |
| Email | **Use Gmail** (fills the server), enter the address and app password → **Send test email** |
| AI, Voice | Usually yours as the provider: paste once per client, **Test** |
| Dashboard login | Change the admin username and password |

Each card has a *Where do I find these?* guide. Values are stored **encrypted** in the app's own data folder and override
server settings; passwords are never shown again. **Save & apply** restarts the receptionist (about 30 seconds on Azure).

*Why tokens and not a "Log in with Facebook" button?* Meta's one-click onboarding (Facebook Login for Business and
WhatsApp Embedded Signup) is only available to verified **Meta Tech Providers**. Once your business is verified and
approved as a Tech Provider, a login button can replace the token fields; until then, the Connections page is the fastest
compliant route.

## 3. Accounts the client owns (and how they give you access)

The client **owns** every account. You get admin or partner access and **never ask for their personal passwords**.

| Account | Client does | Gives you |
|---|---|---|
| **Meta Business portfolio** (business.facebook.com) | Create it, or use the existing one; complete **business verification** | Add you as **Admin** (Settings → People) or add your business as a **Partner** |
| **Facebook Page** | Must be in the portfolio | Page access through the portfolio |
| **Instagram professional account** | Switch to Professional, link to the Page; *Settings → Messages → Allow access to messages* ON | Access through the portfolio |
| **WhatsApp Business number** | Choose the number and receive the verification SMS or call during setup | Nothing extra (it is added in the portfolio) |
| **Calendar: Microsoft 365** | IT admin grants consent to the app registration (SETUP.md §5b) | Tenant ID; you create the app registration with them |
| **Calendar: Outlook.com** | Signs in once with the code shown on the dashboard (microsoft.com/link) | Nothing: the client signs in themselves |
| **Email for notifications** | Gmail: create an **App password** (Google Account → Security → 2-Step Verification → App passwords) | The app password (only for the bot's sending address) |
| **Azure** (if self-hosted) | Create a subscription | Add you as **Contributor** on a resource group |

**Tip:** create the Meta app inside the **client's** business portfolio, not yours. Then the WhatsApp number, tokens and
verification stay with the client, and you can hand over or step away cleanly.

---

## 4. Your setup runbook (about 2–4 hours per client)

| # | Step | Guide |
|---|---|---|
| 1 | Create the client's Web App (same plan as other clients, or the client's subscription) | SETUP.md §9 |
| 2 | Azure OpenAI + Speech: reuse yours (bill usage in the monthly fee) or create them in the client's subscription | SETUP.md §3–4 |
| 3 | Meta app in the client's portfolio: WhatsApp, Messenger, Instagram; **permanent system-user token** | SETUP.md §7, PROJECT-JOURNAL.md §8–10 |
| 4 | Calendar and notification email | SETUP.md §5–6 |
| 5 | Open the client's dashboard → **Connections**: fill and test every card (or paste all settings into the Web App's *Environment variables*) | Section 2 |
| 6 | Dashboard **Settings**: fill from survey section A–B; **Intents**: add the top 10 questions from D-18 as guidance/examples | — |
| 7 | Webhook URLs in the Meta app → the client's Azure address; `subscribed_apps` for WhatsApp and Instagram | SETUP.md §9 step 7 |
| 8 | Acceptance test on every channel, then **Settings → Start fresh** | ACCEPTANCE-TEST.md |
| 9 | Handover session with the client (section 6) | — |

---

## 5. The customer's experience

```
Customer sends a DM ─► AI replies in seconds (text, or voice for voice notes)
        │
        ├─ Question about services, hours, location ─► answered from the business description
        │
        ├─ "Can I book…?" ─► AI offers a free slot from the real calendar
        │        └─ "Yes" ─► booked in the calendar
        │                    ├─ chat confirmation (text + voice)
        │                    └─ confirmation email with appointment.ics (if they gave an email)
        │
        │        └─ "Let me check and confirm" ─► "No problem, take your time!"
        │                    └─ still no answer ─► follow-up after 1 hour, and again after ~23 hours
        │
        ├─ 15 minutes before the appointment ─► reminder message (or email)
        │
        ├─ "Please cancel" ─► removed from the calendar + cancellation email
        │
        └─ Complaint / urgent / unclear ─► handed to a human; the chat is flagged in the dashboard
```

What the customer receives:

| Moment | In the chat | By email (if they shared one) |
|---|---|---|
| Booking confirmed | "You're all set, {name}! … confirmed for {slot}" (text, and voice if enabled) | **Confirmed: Appointment with {business} on {slot}**, with `appointment.ics` for any calendar (Google, Outlook, Apple) |
| Booking cancelled | "Your appointment … has been cancelled" | **Cancelled: your appointment on {slot}**, with `cancel.ics` |
| Offered a time but didn't confirm | Follow-up after 1 h and ~23 h: "Shall I book your free consultation for {slot}?" | — |
| 15 min before the appointment | "A quick reminder: your appointment … starts in 15 minutes" | Reminder email, only if the chat platform doesn't allow the message |

**Platform rules for messages the business starts:** WhatsApp, Messenger and Instagram allow free messages only within
24 hours of the customer's last message, so follow-ups stay inside that window. A reminder that falls later is sent on
Messenger as an appointment update, on WhatsApp with an approved template (Settings → *WhatsApp reminder template*),
and otherwise by email. Timings and texts are editable in **Settings → Follow-ups & reminders**.

The emails are sent from the client's notification address (SMTP settings) and contain **no internal notes or chat
transcript**. The AI asks for the customer's name and email before booking. If the customer doesn't give an email, the
booking still happens, but only the chat confirmation is sent.

---

## 6. The client's daily use (handover checklist)

Show the client these in a 20-minute session:

| Task | Where |
|---|---|
| See today's activity and anything needing attention | **Dashboard** |
| Read all chats; reply personally (text or voice) | **Conversations** → open a chat → *Send a reply* |
| Pause the bot for one customer and take over | *Take over (pause bot)* → later *Resume bot* |
| Check every message, with its intent and confidence | **Message log** |
| See and cancel bookings; spot email or calendar problems (red text) | **Appointments** |
| Change what the bot says, without code | **Intents** (Save applies to the next message) |
| Urgent keywords that always go to a human | **Rules** |
| Opening hours, business info, voice, approval mode | **Settings** |
| Follow-up and reminder timing and texts | **Settings → Follow-ups & reminders** |
| Change a token, the email password, or reconnect Outlook | **Connections** |
| Review replies before they are sent (for the first week) | **Settings → Require approval** |

Suggest **Require approval = on** for the first few days, so the client builds trust in the replies, then turn it off.

---

## 7. How the channels are monitored (for technical buyers)

| Channel | Method | Latency |
|---|---|---|
| WhatsApp (Cloud API) | Webhook push (streamed by Meta), signature-checked | ~1–3 s |
| Facebook Messenger | Webhook push, signature-checked | ~1–3 s |
| Instagram Direct | Webhook push, signature-checked (needs a published Meta app for public DMs) | ~1–3 s |
| X (Twitter) DMs | Polling the X API every 60 s (needs a paid X API plan) | ≤ 60 s |
| Dashboard simulator | Built in | instant |

Every channel implements the same small interface (`IWebhookChannel` for push or `IPollingChannel` for polling), and the
rest of the system is channel-agnostic. A new channel (Telegram, website chat, SMS, email…) is one class plus one
registration line. See ARCHITECTURE.md → *Adding a channel*.

---

## 8. Go-live checklist

- [ ] Survey answers entered in **Settings** and **Intents**; *Check availability now* shows the calendar read OK.
- [ ] **Test voice** and **Send test email** work.
- [ ] WhatsApp, Messenger (and Instagram if published) each answer a "Hi" and complete a booking.
- [ ] The customer confirmation email arrives (check the spam folder once, and mark it as *Not spam*).
- [ ] Cancelling removes the event and sends the cancellation email.
- [ ] **Start fresh** clears the test chats; the test events are deleted from the calendar.
- [ ] Client can sign in to the dashboard and knows *Take over*, *Intents* and *Require approval*.
- [ ] Budget alert set on the Azure subscription.
