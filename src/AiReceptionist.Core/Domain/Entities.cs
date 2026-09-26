namespace AiReceptionist.Core.Domain;

// All DateTime values are stored in UTC. SQLite cannot order DateTimeOffset columns, so plain UTC DateTime is used.

/// <summary>A person who messaged the business on a specific channel.</summary>
public class Contact
{
    public int Id { get; set; }

    /// <summary>Channel id, e.g. "facebook", "whatsapp" (see <see cref="Channels"/>).</summary>
    public string Channel { get; set; } = "";

    /// <summary>The sender id on that channel (PSID, IGSID, phone number, X user id...).</summary>
    public string ExternalUserId { get; set; } = "";

    public string? DisplayName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public DateTime CreatedUtc { get; set; }

    public List<Conversation> Conversations { get; set; } = new();
}

/// <summary>The running thread between the business and one <see cref="Contact"/>.</summary>
public class Conversation
{
    public int Id { get; set; }
    public int ContactId { get; set; }
    public Contact Contact { get; set; } = null!;
    public string Channel { get; set; } = "";

    public ConversationMode Mode { get; set; } = ConversationMode.Bot;

    /// <summary>Slot offered to the customer and awaiting confirmation.</summary>
    public DateTime? PendingSlotStartUtc { get; set; }

    public string? LastIntent { get; set; }

    /// <summary>Set when a human should look at the thread (handoff, draft, errors, low confidence).</summary>
    public bool NeedsAttention { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime LastActivityUtc { get; set; }

    public List<Message> Messages { get; set; } = new();
    public List<Appointment> Appointments { get; set; } = new();
}

public class Message
{
    public int Id { get; set; }
    public int ConversationId { get; set; }
    public Conversation Conversation { get; set; } = null!;

    /// <summary>Duplicated from the conversation so (Channel, ExternalId) can be uniquely indexed for de-duplication.</summary>
    public string Channel { get; set; } = "";

    public MessageDirection Direction { get; set; }
    public string? Text { get; set; }

    /// <summary>Public URL of synthesized (outbound) or received (inbound) audio.</summary>
    public string? AudioUrl { get; set; }

    public bool IsVoice { get; set; }

    /// <summary>Platform message id, used to drop webhook retries and poll overlaps.</summary>
    public string? ExternalId { get; set; }

    public string? Intent { get; set; }
    public double? Confidence { get; set; }
    public MessageStatus Status { get; set; }
    public ReplyMode? ReplyMode { get; set; }

    /// <summary>True when an admin wrote or edited the reply.</summary>
    public bool IsManual { get; set; }

    public string? Error { get; set; }
    public DateTime CreatedUtc { get; set; }
}

/// <summary>An intent the bot can recognise. Editable from the dashboard.</summary>
public class IntentDefinition
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>Example customer phrases, one per line. Fed to the LLM and used by the offline keyword engine.</summary>
    public string Examples { get; set; } = "";

    public IntentAction Action { get; set; } = IntentAction.Reply;
    public ReplyMode ReplyMode { get; set; } = ReplyMode.Text;

    /// <summary>Extra instructions for the LLM on how to answer this intent.</summary>
    public string? Guidance { get; set; }

    /// <summary>Canned reply. Used by the offline engine, when the LLM fails, or always if <see cref="AlwaysUseTemplate"/>.
    /// Supports placeholders: {business}, {name}, {slot}, {slots}, {hours}.</summary>
    public string? TemplateReply { get; set; }

    public bool AlwaysUseTemplate { get; set; }
    public bool Enabled { get; set; } = true;
    public int SortOrder { get; set; }
}

/// <summary>A deterministic rule evaluated (in <see cref="Priority"/> order) before the language model.</summary>
public class ConversationRule
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int Priority { get; set; }
    public RuleMatchType MatchType { get; set; } = RuleMatchType.Contains;
    public string Pattern { get; set; } = "";

    /// <summary>Optional channel id; empty means all channels.</summary>
    public string? ChannelFilter { get; set; }

    public RuleAction Action { get; set; }

    /// <summary>Intent name for <see cref="RuleAction.ForceIntent"/>, reply text for <see cref="RuleAction.FixedReply"/>.</summary>
    public string? Value { get; set; }

    public bool Enabled { get; set; } = true;
}

/// <summary>Single-row table with everything an admin can tune without redeploying.</summary>
public class BotSettings
{
    public int Id { get; set; }
    public string BusinessName { get; set; } = "Contoso Clinic";
    public string BusinessDescription { get; set; } = "";

    /// <summary>IANA ("Europe/London") or Windows ("GMT Standard Time") time-zone id.</summary>
    public string TimeZoneId { get; set; } = "UTC";

    /// <summary>Weekly opening hours, e.g. "Mon-Fri 09:00-17:00; Sat 10:00-13:00".</summary>
    public string BusinessHours { get; set; } = "Mon-Fri 09:00-17:00";

    public int AppointmentMinutes { get; set; } = 30;
    public int BookingHorizonDays { get; set; } = 14;
    public int MinLeadMinutes { get; set; } = 60;

    /// <summary>Persona / base instructions for the LLM.</summary>
    public string SystemPrompt { get; set; } = "";

    public bool AutoReplyEnabled { get; set; } = true;

    /// <summary>When true every bot reply is saved as a draft for an admin to approve or edit.</summary>
    public bool RequireApproval { get; set; }

    public ReplyMode DefaultReplyMode { get; set; } = ReplyMode.Text;

    /// <summary>Answer voice notes with voice.</summary>
    public bool VoiceReplyToVoice { get; set; } = true;

    /// <summary>Azure neural voice, e.g. "en-US-AvaMultilingualNeural".</summary>
    public string VoiceName { get; set; } = "en-US-AvaMultilingualNeural";

    /// <summary>Below this LLM confidence the handoff message is sent and the thread is flagged.</summary>
    public double MinConfidence { get; set; } = 0.35;

    public string HandoffMessage { get; set; } = "Thanks for your message! A member of our team will get back to you shortly.";

    /// <summary>Calendar event subject. Placeholders: {fullname}, {name}, {channel}, {business}.</summary>
    public string AppointmentSubject { get; set; } = "Appointment with {fullname} ({channel})";

    /// <summary>Reply after a successful calendar booking. Placeholders: {name}, {slot}, {business}.</summary>
    public string BookedReply { get; set; } = "You're all set, {name}! Your appointment is confirmed for {slot}. See you then.";

    /// <summary>Reply when the calendar was unavailable and email fallback was used.</summary>
    public string EmailFallbackReply { get; set; } = "Thanks {name}, your appointment for {slot} is confirmed. Our team has been notified by email.";

    public string BookingFailedReply { get; set; } = "Sorry, I couldn't finalise that booking. A team member will contact you to confirm.";

    public string SlotUnavailableReply { get; set; } = "Sorry, that time isn't available. The nearest openings are: {slots}. Which one works for you?";

    /// <summary>How many previous messages are sent to the LLM as context.</summary>
    public int HistoryMessages { get; set; } = 12;
}

public class Appointment
{
    public int Id { get; set; }
    public int ConversationId { get; set; }
    public Conversation Conversation { get; set; } = null!;
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    public string Subject { get; set; } = "";
    public string? CustomerName { get; set; }
    public string? CustomerEmail { get; set; }
    public AppointmentStatus Status { get; set; }
    public BookingMethod Method { get; set; }

    /// <summary>Microsoft Graph event id when <see cref="Method"/> is Calendar.</summary>
    public string? ExternalEventId { get; set; }

    public string? WebLink { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedUtc { get; set; }
}

/// <summary>Polling position for channels that are polled rather than pushed (e.g. X/Twitter DMs).</summary>
public class ChannelCursor
{
    public string Channel { get; set; } = "";
    public string? Cursor { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
