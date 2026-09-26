using AiReceptionist.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace AiReceptionist.Core.Data;

/// <summary>Creates the database and a sensible default configuration on first start.
/// Everything seeded here can be changed from the dashboard afterwards.</summary>
public static class SeedData
{
    public static async Task InitializeAsync(ReceptionistDbContext db, CancellationToken ct = default)
    {
        await db.Database.EnsureCreatedAsync(ct);

        if (!await db.Settings.AnyAsync(ct))
        {
            db.Settings.Add(new BotSettings
            {
                BusinessName = "Contoso Clinic",
                BusinessDescription = "A friendly wellness clinic offering 30-minute consultations. Located at 1 Main Street. Parking available.",
                SystemPrompt =
                    "You are the virtual receptionist for the business described below. " +
                    "You answer direct messages on social media warmly and concisely, help people book appointments, " +
                    "and never invent prices, policies or medical advice that are not in the business description.",
            });
        }

        if (!await db.Intents.AnyAsync(ct))
        {
            db.Intents.AddRange(DefaultIntents());
        }

        if (!await db.Rules.AnyAsync(ct))
        {
            db.Rules.AddRange(
                new ConversationRule
                {
                    Name = "Urgent requests go to a human",
                    Priority = 10,
                    MatchType = RuleMatchType.Regex,
                    Pattern = @"\b(urgent|emergency|asap)\b",
                    Action = RuleAction.HumanHandoff,
                },
                new ConversationRule
                {
                    Name = "Opt-out",
                    Priority = 20,
                    MatchType = RuleMatchType.Exact,
                    Pattern = "stop",
                    Action = RuleAction.FixedReply,
                    Value = "You won't receive further automated replies. Message us any time if you change your mind.",
                });
        }

        await db.SaveChangesAsync(ct);
    }

    public static IEnumerable<IntentDefinition> DefaultIntents() => new[]
    {
        new IntentDefinition
        {
            Name = "Greeting", SortOrder = 1, Action = IntentAction.Reply, ReplyMode = ReplyMode.TextAndVoice,
            Description = "The customer says hello or starts the conversation without a specific request.",
            Examples = "hi\nhello\nhey there\ngood morning",
            Guidance = "Greet them, introduce yourself as the receptionist for the business and ask how you can help.",
            TemplateReply = "Hi {name}! Thanks for contacting {business}. How can I help you today? I can answer questions or book an appointment.",
        },
        new IntentDefinition
        {
            Name = "BusinessInfo", SortOrder = 2, Action = IntentAction.Reply, ReplyMode = ReplyMode.Text,
            Description = "Questions about opening hours, location, services or pricing.",
            Examples = "what are your opening hours\nwhere are you located\nwhat services do you offer\nare you open on saturday",
            Guidance = "Answer only from the business description and opening hours. If unknown, say a team member will follow up.",
            TemplateReply = "We're open {hours}. Would you like to book an appointment?",
        },
        new IntentDefinition
        {
            Name = "BookAppointment", SortOrder = 3, Action = IntentAction.ProposeAppointment, ReplyMode = ReplyMode.Text,
            Description = "The customer wants to book, schedule or reschedule an appointment, or asks about availability.",
            Examples = "can I book an appointment\nI'd like to schedule a visit\ndo you have anything tomorrow afternoon\nbook me in for friday\navailability next week",
            Guidance = "Offer one specific open slot that best matches their request (or 2-3 if they gave no preference) and ask them to confirm. Ask for their name if unknown.",
            TemplateReply = "I can offer {slot}. Shall I book that for you?",
        },
        new IntentDefinition
        {
            Name = "ConfirmAppointment", SortOrder = 4, Action = IntentAction.ConfirmAppointment, ReplyMode = ReplyMode.TextAndVoice,
            Description = "The customer accepts the slot that was just offered (yes, that works, book it...).",
            Examples = "yes\nyes please\nthat works\nperfect, book it\nsounds good",
            Guidance = "Only use when a pending slot exists (or you are proposing one in the same reply) and the customer clearly accepts it.",
        },
        new IntentDefinition
        {
            Name = "CancelAppointment", SortOrder = 5, Action = IntentAction.CancelAppointment, ReplyMode = ReplyMode.TextAndVoice,
            Description = "The customer wants to cancel an existing appointment.",
            Examples = "cancel my appointment\nI can't make it\nplease cancel",
        },
        new IntentDefinition
        {
            Name = "HumanAgent", SortOrder = 6, Action = IntentAction.HumanHandoff, ReplyMode = ReplyMode.Text,
            Description = "The customer asks for a person, complains, or has a problem the bot cannot solve.",
            Examples = "can I speak to a human\nthis is unacceptable\nI want to make a complaint\nreal person please",
            TemplateReply = "I've passed your message to our team and someone will reply to you personally very soon.",
        },
        new IntentDefinition
        {
            Name = "Other", SortOrder = 99, Action = IntentAction.Reply, ReplyMode = ReplyMode.Text,
            Description = "Anything else.",
            Examples = "",
            Guidance = "Reply helpfully and briefly. Offer to book an appointment if relevant.",
            TemplateReply = "Thanks for your message! I can share our opening hours or book an appointment for you. What would you like to do?",
        },
    };
}
