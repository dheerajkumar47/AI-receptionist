using System.Text;
using AiReceptionist.Core.Abstractions;
using AiReceptionist.Core.Conversations;
using AiReceptionist.Core.Data;
using AiReceptionist.Core.Domain;
using AiReceptionist.Core.Scheduling;
using AiReceptionist.Infrastructure.AI;
using AiReceptionist.Infrastructure.Channels.Meta;
using AiReceptionist.Infrastructure.Channels.Twitter;
using AiReceptionist.Infrastructure.Voice;

namespace AiReceptionist.Tests;

public class BusinessHoursTests
{
    [Fact]
    public void Parses_ranges_split_hours_and_wraparound_days()
    {
        var h = BusinessHours.Parse("Mon-Fri 09:00-12:30,13:30-17:00; Sat-Sun 10:00-13:00");

        Assert.Equal(2, h.For(DayOfWeek.Wednesday).Count);
        Assert.Equal(new TimeSpan(13, 30, 0), h.For(DayOfWeek.Friday)[1].Open);
        Assert.Single(h.For(DayOfWeek.Sunday));
        Assert.Single(h.For(DayOfWeek.Saturday));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Mon-Fri")]
    [InlineData("Funday 09:00-17:00")]
    [InlineData("Mon 17:00-09:00")]
    public void Rejects_invalid_input(string text) => Assert.False(BusinessHours.TryParse(text, out _, out _));

    [Fact]
    public void Daily_means_every_day() =>
        Assert.All(Enum.GetValues<DayOfWeek>(), d => Assert.Single(BusinessHours.Parse("Daily 08:00-20:00").For(d)));
}

public class SlotCalculatorTests
{
    private static readonly DateTime Monday8am = new(2026, 9, 28, 8, 0, 0, DateTimeKind.Utc);
    private static readonly BusinessHours Hours = BusinessHours.Parse("Mon-Fri 09:00-17:00");

    [Fact]
    public void Slots_respect_hours_lead_time_and_busy_periods()
    {
        var busy = new[] { new TimeSlot(Monday8am.AddHours(2), Monday8am.AddHours(3)) }; // 10:00-11:00
        var slots = SlotCalculator.GetOpenSlots(Hours, TimeZoneInfo.Utc, Monday8am.AddMinutes(30), TimeSpan.FromMinutes(30), 0, TimeSpan.FromMinutes(60), busy);

        Assert.Equal(Monday8am.AddMinutes(90), slots[0].StartUtc); // 09:30 (09:00 is inside the lead time)
        Assert.DoesNotContain(slots, s => s.StartUtc.Hour == 10);
        Assert.Equal(new DateTime(2026, 9, 28, 16, 30, 0, DateTimeKind.Utc), slots[^1].StartUtc);
    }

    [Fact]
    public void Weekend_has_no_slots()
    {
        var saturday = new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc);
        Assert.Empty(SlotCalculator.GetOpenSlots(Hours, TimeZoneInfo.Utc, saturday, TimeSpan.FromMinutes(30), 1, TimeSpan.Zero, Array.Empty<TimeSlot>()));
    }

    [Fact]
    public void Slots_are_computed_in_the_business_time_zone()
    {
        var tz = TimeZoneResolver.Resolve("America/New_York"); // UTC-4 in September
        var slots = SlotCalculator.GetOpenSlots(Hours, tz, Monday8am, TimeSpan.FromMinutes(60), 0, TimeSpan.Zero, Array.Empty<TimeSlot>());
        Assert.Equal(new DateTime(2026, 9, 28, 13, 0, 0, DateTimeKind.Utc), slots[0].StartUtc);
        Assert.Equal("2026-09-28T09:00:00-04:00", SlotFormatter.Iso(slots[0].StartUtc, tz));
    }

    [Theory]
    [InlineData("2026-09-29T14:00:00+00:00", true)]
    [InlineData("2026-09-29T16:45:00+00:00", false)] // would end after closing
    [InlineData("2026-10-03T10:00:00+00:00", false)] // Saturday
    [InlineData("2026-09-28T08:30:00+00:00", false)] // inside lead time
    public void IsBookable_checks_every_constraint(string iso, bool expected)
    {
        var start = DateTimeOffset.Parse(iso).UtcDateTime;
        Assert.Equal(expected, SlotCalculator.IsBookable(start, Hours, TimeZoneInfo.Utc, Monday8am, TimeSpan.FromMinutes(30), 14, TimeSpan.FromMinutes(60), Array.Empty<TimeSlot>()));
    }

    [Fact]
    public void ParseToUtc_treats_offsetless_values_as_business_local_time()
    {
        var tz = TimeZoneResolver.Resolve("Europe/London"); // BST, UTC+1
        Assert.Equal(new DateTime(2026, 9, 29, 13, 0, 0), SlotFormatter.ParseToUtc("2026-09-29T14:00:00", tz));
        Assert.Equal(new DateTime(2026, 9, 29, 14, 0, 0), SlotFormatter.ParseToUtc("2026-09-29T14:00:00Z", tz));
        Assert.Null(SlotFormatter.ParseToUtc("null", tz));
    }
}

public class TemplateAndRuleTests
{
    [Theory]
    [InlineData("Hi {name}! Welcome to {business}.", "Jane", "Hi Jane! Welcome to Contoso.")]
    [InlineData("Hi {name}! Welcome to {business}.", null, "Hi! Welcome to Contoso.")]
    [InlineData("Thanks, {name}!", null, "Thanks!")]
    [InlineData("Keep {unknown}", null, "Keep {unknown}")]
    public void Templates_render_and_tidy_missing_values(string template, string? name, string expected) =>
        Assert.Equal(expected, TemplateRenderer.Render(template, new Dictionary<string, string?> { ["name"] = name, ["business"] = "Contoso" }));

    [Fact]
    public void Rules_match_by_priority_and_channel()
    {
        var rules = new List<ConversationRule>
        {
            new() { Id = 1, Name = "low", Priority = 20, MatchType = RuleMatchType.Contains, Pattern = "price", Action = RuleAction.FixedReply },
            new() { Id = 2, Name = "wa-only", Priority = 10, MatchType = RuleMatchType.Contains, Pattern = "price", ChannelFilter = "whatsapp", Action = RuleAction.HumanHandoff },
            new() { Id = 3, Name = "bad regex", Priority = 1, MatchType = RuleMatchType.Regex, Pattern = "([", Action = RuleAction.Ignore },
            new() { Id = 4, Name = "disabled", Priority = 0, MatchType = RuleMatchType.Contains, Pattern = "price", Action = RuleAction.Ignore, Enabled = false },
        };

        Assert.Equal("wa-only", RuleEvaluator.Match(rules, "What's the PRICE?", "whatsapp")!.Name);
        Assert.Equal("low", RuleEvaluator.Match(rules, "What's the price?", "facebook")!.Name);
        Assert.Null(RuleEvaluator.Match(rules, "hello", "facebook"));
        Assert.NotNull(RuleEvaluator.Validate(rules[2]));
    }

    [Fact]
    public void Ics_contains_utc_times_and_escaped_text()
    {
        var ics = IcsBuilder.Build("uid1", new DateTime(2026, 9, 29, 12, 0, 0), new DateTime(2026, 9, 29, 12, 30, 0),
            "Visit; Jane", "Line1\nLine2", "owner@x.com", "jane@x.com", new DateTime(2026, 9, 28));
        Assert.Contains("DTSTART:20260929T120000Z\r\n", ics);
        Assert.Contains("SUMMARY:Visit\\; Jane", ics);
        Assert.Contains("DESCRIPTION:Line1\\nLine2", ics);
    }
}

public class KeywordEngineTests
{
    private static IntentContext Context(string text, DateTime? pending = null)
    {
        var now = new DateTime(2026, 9, 28, 8, 0, 0, DateTimeKind.Utc);
        var settings = new BotSettings();
        var slots = SlotCalculator.GetOpenSlots(BusinessHours.Parse(settings.BusinessHours), TimeZoneInfo.Utc, now,
            TimeSpan.FromMinutes(30), 14, TimeSpan.FromHours(1), Array.Empty<TimeSlot>());
        return new IntentContext(settings, SeedData.DefaultIntents().ToList(), Array.Empty<ChatTurn>(), text, "simulator",
            null, null, slots, pending, now, TimeZoneInfo.Utc);
    }

    [Theory]
    [InlineData("hello there", "Greeting")]
    [InlineData("What are your opening hours?", "BusinessInfo")]
    [InlineData("I'd like to schedule a visit", "BookAppointment")]
    [InlineData("can I speak to a human please", "HumanAgent")]
    [InlineData("I need to cancel my appointment", "CancelAppointment")]
    [InlineData("zzzz", "Other")]
    public async Task Classifies_common_messages(string text, string intent) =>
        Assert.Equal(intent, (await new KeywordIntentEngine().AnalyzeAsync(Context(text), default)).Intent);

    [Theory]
    [InlineData("book me in for friday at 3pm", "2026-10-02T15:00:00")]
    [InlineData("can I book an appointment tomorrow morning", "2026-09-29T09:00:00")]
    [InlineData("book me in for thursday afternoon", "2026-10-01T12:00:00")]
    public async Task Understands_time_preferences(string text, string expected)
    {
        var r = await new KeywordIntentEngine().AnalyzeAsync(Context(text), default);
        Assert.Equal(DateTime.Parse(expected), r.ProposedSlotUtc);
    }

    [Fact]
    public async Task Closed_day_keeps_preferred_time_of_day()
    {
        var saturday = new DateTime(2026, 9, 26, 15, 0, 0, DateTimeKind.Utc);
        var ctx = Context("can I book an appointment tomorrow afternoon") with
        {
            NowUtc = saturday,
            OpenSlots = SlotCalculator.GetOpenSlots(BusinessHours.Parse("Mon-Fri 09:00-17:00"), TimeZoneInfo.Utc, saturday,
                TimeSpan.FromMinutes(30), 14, TimeSpan.FromHours(1), Array.Empty<TimeSlot>()),
        };
        var r = await new KeywordIntentEngine().AnalyzeAsync(ctx, default);
        Assert.Equal(new DateTime(2026, 9, 28, 12, 0, 0), r.ProposedSlotUtc); // Sunday is closed -> Monday afternoon
    }

    [Fact]
    public async Task Yes_confirms_only_when_a_slot_is_pending()
    {
        var engine = new KeywordIntentEngine();
        Assert.Equal("ConfirmAppointment", (await engine.AnalyzeAsync(Context("Yes please", DateTime.UtcNow), default)).Intent);
        Assert.NotEqual("ConfirmAppointment", (await engine.AnalyzeAsync(Context("Yes please"), default)).Intent);
    }

    [Fact]
    public async Task Extracts_name_and_email()
    {
        var r = await new KeywordIntentEngine().AnalyzeAsync(Context("Hi, My name is Priya Shah, priya@example.com"), default);
        Assert.Equal("Priya Shah", r.CustomerName);
        Assert.Equal("priya@example.com", r.CustomerEmail);
    }
}

public class OpenAiParsingTests
{
    [Fact]
    public void Parses_model_json_and_normalises_values()
    {
        var ctx = new IntentContext(new BotSettings(), SeedData.DefaultIntents().ToList(), Array.Empty<ChatTurn>(), "x", "facebook",
            null, null, Array.Empty<TimeSlot>(), null, DateTime.UtcNow, TimeZoneResolver.Resolve("Europe/London"));

        var r = OpenAiIntentEngine.Parse("""
            {"intent":"bookappointment","confidence":"0.8","reply":"How about Tue 2pm?","proposedSlotStart":"2026-09-29T14:00:00+01:00",
             "confirmsPendingSlot":false,"customerName":"Sam","customerEmail":null}
            """, ctx);

        Assert.Equal("BookAppointment", r.Intent);
        Assert.Equal(0.8, r.Confidence);
        Assert.Equal(new DateTime(2026, 9, 29, 13, 0, 0), r.ProposedSlotUtc);
        Assert.Equal("Sam", r.CustomerName);
        Assert.Null(r.CustomerEmail);

        Assert.Equal("Other", OpenAiIntentEngine.Parse("""{"intent":"Nonsense","confidence":5}""", ctx).Intent);
        Assert.Equal(1, OpenAiIntentEngine.Parse("""{"intent":"Nonsense","confidence":5}""", ctx).Confidence);
    }

    [Fact]
    public void System_prompt_contains_intents_slots_and_rules()
    {
        var now = new DateTime(2026, 9, 28, 8, 0, 0, DateTimeKind.Utc);
        var ctx = new IntentContext(new BotSettings { BusinessName = "Acme Dental" }, SeedData.DefaultIntents().ToList(), Array.Empty<ChatTurn>(),
            "x", "whatsapp", "Sam", null,
            new[] { new TimeSlot(now.AddHours(2), now.AddHours(2.5)), new TimeSlot(now.AddHours(2.5), now.AddHours(3)), new TimeSlot(now.AddHours(5), now.AddHours(5.5)) },
            null, now, TimeZoneResolver.Resolve("Europe/London"));
        var prompt = PromptBuilder.BuildSystemPrompt(ctx);
        Assert.Contains("Acme Dental", prompt);
        Assert.Contains("- BookAppointment:", prompt);
        Assert.Contains("- Mon 28 Sep 2026 (UTC+01:00): 11:00-12:00, 14:00-14:30", prompt);
        Assert.Contains("Never say an appointment is booked", prompt);
    }

    [Fact]
    public void Ssml_escapes_text_and_derives_language()
    {
        var ssml = AzureSpeechSynthesizer.BuildSsml("Tom & Jerry <3", "en-GB-SoniaNeural");
        Assert.Contains("xml:lang='en-GB'", ssml);
        Assert.Contains("Tom &amp; Jerry &lt;3", ssml);
    }
}

public class MetaWebhookTests
{
    private const string Secret = "app-secret";

    [Fact]
    public void Signature_is_validated_in_constant_time()
    {
        var body = Encoding.UTF8.GetBytes("""{"object":"page"}""");
        var sig = MetaWebhook.Sign(body, Secret);
        Assert.True(MetaWebhook.ValidateSignature(sig, body, Secret));
        Assert.False(MetaWebhook.ValidateSignature(sig, Encoding.UTF8.GetBytes("tampered"), Secret));
        Assert.False(MetaWebhook.ValidateSignature("sha256=zz", body, Secret));
        Assert.False(MetaWebhook.ValidateSignature(null, body, Secret));
        Assert.False(MetaWebhook.ValidateSignature(sig, body, null));
    }

    [Fact]
    public void Verification_handshake_echoes_challenge_only_for_correct_token()
    {
        var q = new Dictionary<string, string?> { ["hub.mode"] = "subscribe", ["hub.verify_token"] = "tok", ["hub.challenge"] = "1234" };
        Assert.Equal("1234", MetaWebhook.Verify(q, "tok"));
        Assert.Null(MetaWebhook.Verify(q, "other"));
        Assert.Null(MetaWebhook.Verify(q, null));
    }

    [Fact]
    public void Parses_messenger_messages_and_skips_echoes_and_receipts()
    {
        var json = """
        {"object":"page","entry":[{"id":"PAGE","time":1,"messaging":[
          {"sender":{"id":"PSID1"},"recipient":{"id":"PAGE"},"timestamp":1790000000000,"message":{"mid":"m_1","text":"Hello"}},
          {"sender":{"id":"PAGE"},"recipient":{"id":"PSID1"},"timestamp":1790000000001,"message":{"mid":"m_2","text":"echo","is_echo":true}},
          {"sender":{"id":"PSID1"},"recipient":{"id":"PAGE"},"timestamp":1790000000002,"delivery":{"mids":["m_2"]}},
          {"sender":{"id":"PSID2"},"recipient":{"id":"PAGE"},"timestamp":1790000000003,"message":{"mid":"m_3","attachments":[{"type":"audio","payload":{"url":"https://cdn/a.mp4"}}]}}
        ]}]}
        """;
        var msgs = MetaWebhook.ParseMessaging(json, "page", Channels.Facebook);

        Assert.Equal(2, msgs.Count);
        Assert.Equal(("PSID1", "Hello", "m_1"), (msgs[0].SenderId, msgs[0].Text, msgs[0].ExternalMessageId));
        Assert.True(msgs[1].IsVoice);
        Assert.Equal("https://cdn/a.mp4", msgs[1].AudioReference);
        Assert.Empty(MetaWebhook.ParseMessaging(json, "instagram", Channels.Instagram)); // wrong object type
    }

    [Fact]
    public void Parses_instagram_messages()
    {
        var json = """{"object":"instagram","entry":[{"id":"IG","time":1,"messaging":[{"sender":{"id":"IGSID"},"recipient":{"id":"IG"},"timestamp":1790000000000,"message":{"mid":"ig_1","text":"Book me in"}}]}]}""";
        var m = Assert.Single(MetaWebhook.ParseMessaging(json, "instagram", Channels.Instagram));
        Assert.Equal(Channels.Instagram, m.Channel);
        Assert.Equal("Book me in", m.Text);
    }

    [Fact]
    public void Parses_whatsapp_text_audio_and_ignores_statuses()
    {
        var json = """
        {"object":"whatsapp_business_account","entry":[{"id":"WABA","changes":[
          {"field":"messages","value":{"messaging_product":"whatsapp","metadata":{"phone_number_id":"PNID"},
            "contacts":[{"profile":{"name":"Ravi"},"wa_id":"447700900123"}],
            "messages":[
              {"from":"447700900123","id":"wamid.1","timestamp":"1790000000","type":"text","text":{"body":"Hi there"}},
              {"from":"447700900123","id":"wamid.2","timestamp":"1790000001","type":"audio","audio":{"id":"MEDIA1","mime_type":"audio/ogg; codecs=opus"}}
            ]}},
          {"field":"messages","value":{"statuses":[{"id":"wamid.X","status":"delivered"}]}}
        ]}]}
        """;
        var msgs = MetaWebhook.ParseWhatsApp(json, Channels.WhatsApp);

        Assert.Equal(2, msgs.Count);
        Assert.Equal("Ravi", msgs[0].SenderName);
        Assert.Equal("Hi there", msgs[0].Text);
        Assert.True(msgs[1].IsVoice);
        Assert.Equal("MEDIA1", msgs[1].AudioReference);
    }
}

public class TwitterTests
{
    [Fact]
    public void OAuth1_signature_matches_the_published_X_example()
    {
        // Example from https://developer.x.com/en/docs/authentication/oauth-1-0a/creating-a-signature
        var oauth = new Dictionary<string, string>
        {
            ["oauth_consumer_key"] = "xvz1evFS4wEEPTGEFPHBog",
            ["oauth_nonce"] = "kYjzVBB8Y0ZFabxSWbWovY3uYSQ2pTgmZeNu2VS4cg",
            ["oauth_signature_method"] = "HMAC-SHA1",
            ["oauth_timestamp"] = "1318622958",
            ["oauth_token"] = "370773112-GmHxMAgYyLbNEtIKZeRNFsMKPR9EyMZeS9weJAEb",
            ["oauth_version"] = "1.0",
        };
        var body = new Dictionary<string, string> { ["status"] = "Hello Ladies + Gentlemen, a signed OAuth request!" };

        var sig = OAuth1Signer.ComputeSignature("POST", new Uri("https://api.twitter.com/1.1/statuses/update.json?include_entities=true"),
            oauth, "kAcSOqF21Fu85e7zjz7ZN2U4ZRhfV3WpwPAoE3Z7kBw", "LswwdoUaIvS8ltyTt5jkRh4J50vUPVVHtR2YPi5kE", body);

        Assert.Equal("hCtSmYh+iHYCEqBWrE7C7hYmtUk=", sig);
    }

    [Fact]
    public void Authorization_header_contains_all_oauth_fields()
    {
        var header = OAuth1Signer.BuildAuthorizationHeader("GET", new Uri("https://api.x.com/2/dm_events?max_results=5"), "ck", "cs", "t", "ts", "nonce", 1);
        Assert.StartsWith("OAuth ", header);
        foreach (var f in new[] { "oauth_consumer_key=\"ck\"", "oauth_nonce=\"nonce\"", "oauth_signature=\"", "oauth_timestamp=\"1\"", "oauth_token=\"t\"" })
            Assert.Contains(f, header);
    }

    [Fact]
    public void Parses_dm_events()
    {
        var (events, next) = TwitterDmChannel.ParseEvents("""
            {"data":[{"id":"1800000000000000002","event_type":"MessageCreate","text":"hi","sender_id":"42","created_at":"2026-09-28T10:00:00.000Z"},
                     {"id":"1800000000000000001","event_type":"ParticipantsJoin","sender_id":"42"}],
             "meta":{"result_count":2,"next_token":"abc"}}
            """);
        var e = Assert.Single(events);
        Assert.Equal(("1800000000000000002", "42", "hi"), (e.Id, e.SenderId, e.Text));
        Assert.Equal(new DateTime(2026, 9, 28, 10, 0, 0, DateTimeKind.Utc), e.CreatedUtc);
        Assert.Equal("abc", next);
    }
}
