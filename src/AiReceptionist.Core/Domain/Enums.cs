namespace AiReceptionist.Core.Domain;

/// <summary>Direction of a stored message relative to the business.</summary>
public enum MessageDirection
{
    Inbound,
    Outbound,
}

/// <summary>Lifecycle state of a stored message.</summary>
public enum MessageStatus
{
    /// <summary>Inbound message received from a customer.</summary>
    Received,
    /// <summary>Bot reply waiting for admin approval (approval mode).</summary>
    Draft,
    /// <summary>Reply delivered to the channel.</summary>
    Sent,
    /// <summary>Reply could not be delivered; see <see cref="Message.Error"/>.</summary>
    Failed,
    /// <summary>Inbound message deliberately not answered (rule, takeover, auto-reply off).</summary>
    Ignored,
}

/// <summary>How a reply is delivered to the customer.</summary>
public enum ReplyMode
{
    Text,
    Voice,
    TextAndVoice,
}

/// <summary>What the orchestrator does when an intent is detected.</summary>
public enum IntentAction
{
    /// <summary>Just send the natural-language reply.</summary>
    Reply,
    /// <summary>Offer an appointment slot and wait for the customer to confirm it.</summary>
    ProposeAppointment,
    /// <summary>Customer confirmed the pending slot: book it.</summary>
    ConfirmAppointment,
    /// <summary>Cancel the customer's next upcoming appointment.</summary>
    CancelAppointment,
    /// <summary>Hand the conversation over to a human and pause the bot.</summary>
    HumanHandoff,
}

/// <summary>Who is currently in charge of a conversation.</summary>
public enum ConversationMode
{
    Bot,
    HumanTakeover,
}

public enum RuleMatchType
{
    Contains,
    Exact,
    Regex,
}

/// <summary>Effect of a matching <see cref="ConversationRule"/>. Rules run before the language model.</summary>
public enum RuleAction
{
    /// <summary>Skip intent detection's choice and use the intent named in <see cref="ConversationRule.Value"/>.</summary>
    ForceIntent,
    /// <summary>Reply with the literal text in <see cref="ConversationRule.Value"/>; the language model is not called.</summary>
    FixedReply,
    /// <summary>Hand the conversation to a human.</summary>
    HumanHandoff,
    /// <summary>Store the message but do not reply.</summary>
    Ignore,
}

public enum AppointmentStatus
{
    /// <summary>Written to the Microsoft 365 calendar.</summary>
    Booked,
    /// <summary>Calendar unavailable; a confirmation email (with .ics) was sent instead.</summary>
    EmailFallback,
    Cancelled,
    /// <summary>Neither the calendar nor email succeeded.</summary>
    Failed,
}

public enum BookingMethod
{
    None,
    Calendar,
    Email,
}
