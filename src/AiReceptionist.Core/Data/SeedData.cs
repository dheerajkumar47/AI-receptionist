using AiReceptionist.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace AiReceptionist.Core.Data;

/// <summary>Creates the database and a sensible default configuration on first start.
/// Everything seeded here can be changed from the dashboard afterwards.</summary>
public static class SeedData
{
    /// <summary>Bump when a release adds defaults that existing databases should receive (see <see cref="UpgradeDefaults"/>).</summary>
    public const int CurrentSeedVersion = 2;

    public static async Task InitializeAsync(ReceptionistDbContext db, CancellationToken ct = default)
    {
        if (!await db.Database.EnsureCreatedAsync(ct))
            await SchemaUpgrader.AddMissingColumnsAsync(db, ct);

        if (!await db.Settings.AnyAsync(ct))
        {
            db.Settings.Add(new BotSettings
            {
                SeedVersion = CurrentSeedVersion,
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
        await UpgradeDefaults(db, ct);
    }

    /// <summary>One-time additions for databases created by an earlier version. Anything added here can still be
    /// edited or deleted from the dashboard; it is never re-added afterwards.</summary>
    private static async Task UpgradeDefaults(ReceptionistDbContext db, CancellationToken ct)
    {
        var settings = await db.Settings.OrderBy(s => s.Id).FirstAsync(ct);
        if (settings.SeedVersion >= CurrentSeedVersion) return;

        if (settings.SeedVersion < 2 && !await db.Intents.AnyAsync(i => i.Name == ConfirmLaterIntent.Name, ct))
        {
            db.Intents.Add(ConfirmLaterIntent);
        }

        settings.SeedVersion = CurrentSeedVersion;
        await db.SaveChangesAsync(ct);
    }

    private static IntentDefinition ConfirmLaterIntent => new()
    {
        Name = "ConfirmLater",
        SortOrder = 8,
        Action = IntentAction.Reply,
        ReplyMode = ReplyMode.Text,
        Description = "The person needs time before confirming the offered slot (will check and confirm later).",
        Examples = "wait\nlet me check and confirm\nI'll confirm later\nI will let you know\ngive me some time\nlet me check my schedule\nI'll get back to you\nmaybe later",
        Guidance = "Say there is no rush and that they can simply reply here when they are ready. Do not book anything and do not claim the slot is reserved.",
        TemplateReply = "No problem, take your time! Just reply here whenever you're ready and I'll book it for you.",
    };

    /// <summary>Recommended intents for a software / IT services business offering free consultation calls.
    /// Also used by the dashboard's "Restore recommended intents" button.</summary>
    public static IEnumerable<IntentDefinition> DefaultIntents() => new[]
    {
        new IntentDefinition
        {
            Name = "Greeting", SortOrder = 1, Action = IntentAction.Reply, ReplyMode = ReplyMode.TextAndVoice,
            Description = "The person says hello or starts a conversation without a specific request.",
            Examples = "hi\nhello\nassalam o alaikum\nsalam\nhey there\ngood morning",
            Guidance = "Greet them warmly, introduce yourself as the virtual assistant of the business, mention that we build websites, mobile apps and AI solutions, and ask how you can help.",
            TemplateReply = "Hi {name}! Welcome to {business}. We build websites, mobile apps and AI solutions. How can I help you today?",
        },
        new IntentDefinition
        {
            Name = "BusinessInfo", SortOrder = 2, Action = IntentAction.Reply, ReplyMode = ReplyMode.Text,
            Description = "Questions about our services, technologies, portfolio, working hours, location or how we work.",
            Examples = "what services do you offer\ndo you make mobile apps\ncan you build an ecommerce store\nwhat technologies do you use\nshow me your portfolio\ndo you work with international clients\nwhat are your opening hours\nwhat are your working hours\nwhere are you located",
            Guidance = "Answer only from the business description. Keep it short and friendly, then invite them to a free 30-minute consultation call to discuss their project.",
            TemplateReply = "We build custom websites, mobile apps, web apps, AI chatbots and business automation. Our working hours are {hours}. Would you like to book a free 30-minute consultation call?",
        },
        new IntentDefinition
        {
            Name = "PricingQuote", SortOrder = 3, Action = IntentAction.Reply, ReplyMode = ReplyMode.Text,
            Description = "The person asks about price, cost, budget, rates or how long a project takes.",
            Examples = "how much does a website cost\nwhat is your price\nhow much for a mobile app\nwhat are your rates\nwhat is the budget for an ecommerce site\nhow long will it take",
            Guidance = "Never give a price or a deadline. Explain that every project is different and we share an exact quote after a free 30-minute consultation call, then offer a call time.",
            TemplateReply = "Every project is different, so we share an exact quote after a free 30-minute consultation call. I can offer {slot}. Shall I book it for you?",
        },
        new IntentDefinition
        {
            Name = "BookAppointment", SortOrder = 4, Action = IntentAction.ProposeAppointment, ReplyMode = ReplyMode.Text,
            Description = "The person wants to book, schedule or reschedule a consultation call or meeting, or asks when we are available.",
            Examples = "can I book an appointment\ncan we have a call\nI want to discuss my project\nschedule a meeting\nI'd like to schedule a visit\nbook a free consultation\nare you available tomorrow\nbook me in for friday\navailability next week\nI want to reschedule my call",
            Guidance = "Offer one specific free consultation slot that best matches their request (or 2-3 options if they have no preference) and ask them to confirm. Ask for their name and email if unknown, so we can send the meeting link.",
            TemplateReply = "I can offer a free consultation call on {slot}. Shall I book it for you?",
        },
        new IntentDefinition
        {
            Name = "ConfirmAppointment", SortOrder = 5, Action = IntentAction.ConfirmAppointment, ReplyMode = ReplyMode.TextAndVoice,
            Description = "The person accepts the consultation slot that was just offered.",
            Examples = "yes\nyes please\nthat works\nperfect, book it\nok done\nsounds good",
            Guidance = "Only use when a slot was just offered (or you are offering one in the same reply) and the person clearly accepts it.",
        },
        new IntentDefinition
        {
            Name = "CancelAppointment", SortOrder = 6, Action = IntentAction.CancelAppointment, ReplyMode = ReplyMode.TextAndVoice,
            Description = "The person wants to cancel their booked consultation call.",
            Examples = "cancel my call\ncancel my appointment\nI can't make it\nplease cancel the meeting",
        },
        new IntentDefinition
        {
            Name = "HumanAgent", SortOrder = 7, Action = IntentAction.HumanHandoff, ReplyMode = ReplyMode.Text,
            Description = "The person asks for a real person, has a complaint, an issue with an existing project, or a request the assistant cannot handle.",
            Examples = "can I speak to a human\ncan I talk to a real person\nI have a problem with my project\nthis is not working\nI want to make a complaint",
            TemplateReply = "I've passed your message to our team and someone will reply to you personally very soon.",
        },
        ConfirmLaterIntent,
        new IntentDefinition
        {
            Name = "Other", SortOrder = 99, Action = IntentAction.Reply, ReplyMode = ReplyMode.Text,
            Description = "Anything else.",
            Examples = "",
            Guidance = "Reply helpfully and briefly. If it relates to software, offer a free consultation call.",
            TemplateReply = "Thanks for your message! I can tell you about our services or book a free consultation call. What would you like to do?",
        },
    };
}
