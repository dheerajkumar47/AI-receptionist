# Acceptance test and demo recording

This runbook covers the 15-minute acceptance session. Sample messages are sent from each platform; you should hear a
spoken acknowledgment and receive an appointment in the Microsoft 365 calendar. The same script doubles as the shot
list for the screen-capture demo.

## Pre-flight checklist (the day before)

- [ ] App deployed (or running locally behind a tunnel). `https://<public-url>/health` returns `{"status":"ok"}`.
- [ ] The dashboard home page shows **active** for Language understanding, Voice, Microsoft 365 calendar and Email fallback,
      and for all four channels.
- [ ] **Settings**: correct business name, description, **time zone**, opening hours. *Check availability now* says
      *Microsoft 365 calendar read OK* and lists slots.
- [ ] **Settings → Test voice** plays audio. **Send test email** arrives.
- [ ] **Meta roles**: the Facebook, Instagram and WhatsApp accounts used for testing have a role on the Meta app
      (Development mode). The WhatsApp test phone is a verified recipient.
- [ ] **X**: the tester's account can DM the business account (the business follows it, or open DMs are enabled).
- [ ] **Require approval** is *off*, **Auto-reply** is *on*. No conversation is left in *Human takeover*.
- [ ] Outlook (web or desktop) is open on `Microsoft365:CalendarUser`'s calendar.
- [ ] Meta tokens are long-lived (System User), not the 24-hour temporary tokens.

## 15-minute script

| Time | Platform | Send | Expected |
|---|---|---|---|
| 0:00 | Dashboard | – | Open **Dashboard**; all services are green. Keep a second window on **Message log**. |
| 1:00 | WhatsApp | "Hi!" | Text greeting **and a voice note** (Greeting is `TextAndVoice`). Play it. |
| 2:00 | WhatsApp | "Can I book an appointment tomorrow afternoon? I'm Sam Lee, sam@example.com" | The bot offers a specific free slot and asks for confirmation. The dashboard shows *Awaiting confirmation of slot …*. |
| 3:00 | WhatsApp | "Yes please" | Spoken + text confirmation. **An event appears in Outlook** (category *AI Receptionist*, transcript in the body). Sam gets an Outlook invite. **Appointments** shows *Booked / Calendar*. |
| 4:30 | WhatsApp | *(voice note)* "What are your opening hours?" | The voice note is transcribed and answered **with a voice note** (voice in → voice out). |
| 5:30 | Messenger | "Hello, do you have anything on Friday morning?" | The bot offers a Friday morning slot. |
| 6:30 | Messenger | "That works" | Confirmation with an **audio attachment**. A second event appears in Outlook. |
| 7:30 | Instagram | "Hi, what services do you offer?" | Answer drawn from the business description. |
| 8:30 | Instagram | "Book me in for next Tuesday at 3pm" → "yes" | Slot offered, then booked. Audio confirmation. |
| 10:00 | X | "Hi, can I book a visit?" → "yes" | Booked. X can't carry audio, so the confirmation includes a 🔊 link that plays the spoken version. |
| 11:30 | Any | "This is urgent!" | The *Urgent* rule hands off to a human: the conversation is paused and flagged **attention**. |
| 12:00 | Dashboard | – | Open that conversation, type a manual reply with mode **Voice**, and press **Send**. The customer receives a voice note. Click **Resume bot**. |
| 13:00 | Dashboard | – | **Intents**: edit *Greeting*'s template or guidance and **Save**. Send "hi" again and the new wording is used immediately. |
| 14:00 | Any | "Please cancel my appointment" | The event is **removed from Outlook**. The appointment shows *Cancelled*. |
| 14:30 | Optional | – | Demonstrate the email fallback: set a wrong `Microsoft365:ClientSecret`, restart, and book → an email with .ics arrives and the status is *EmailFallback*. |

If the LLM phrases a slot differently from the table, that is expected. What matters is that the proposed time lies
within opening hours and your free calendar time, and that the booking happens only after confirmation.

## Recording the demo

Record at 1080p with system audio on (e.g. OBS, Xbox Game Bar `Win+Alt+R`, or the Windows Snipping Tool screen recorder),
so the synthesized voice is audible. Suggested layout: the dashboard on the left, WhatsApp Web or the phone mirror on the right,
and Outlook calendar in a third window. Aim for about 3 minutes:

1. Dashboard home: services and channels green (10 s).
2. WhatsApp: "Hi!" → play the voice-note reply (30 s).
3. WhatsApp: booking request → offer → "yes" → play the spoken confirmation (45 s).
4. Switch to Outlook: the new event, opened to show details (20 s).
5. Messenger or Instagram: one quick booking, showing the audio attachment (40 s).
6. Dashboard: open the conversation, show the log with intents and confidence, send a manual voice reply (30 s).
7. Intents page: change a reply live (15 s).

`docs/media/offline-walkthrough.webm` is a recording of the dashboard and simulator running without any cloud keys
(offline engine, email-fallback booking). It shows the UI flow. The live demo above needs your own tenant and accounts.
