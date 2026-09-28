using AiReceptionist.Core.Abstractions;
using AiReceptionist.Core.Conversations;
using AiReceptionist.Core.Data;
using AiReceptionist.Core.Domain;
using AiReceptionist.Core.Scheduling;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AiReceptionist.Tests;

/// <summary>Wires a real <see cref="ConversationService"/> to an in-memory SQLite database and fakes for all external services.</summary>
public sealed class TestHarness : IAsyncDisposable
{
    // Monday 28 Sep 2026, 08:00 UTC. Default settings: UTC, Mon-Fri 09:00-17:00, 30 min, 60 min lead.
    public static readonly DateTimeOffset Now = new(2026, 9, 28, 8, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;

    public TestHarness(IIntentEngine? engine = null)
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<ReceptionistDbContext>().UseSqlite(_connection).Options;
        DbFactory = new TestDbFactory(options);
        using (var db = DbFactory.CreateDbContext()) SeedData.InitializeAsync(db).GetAwaiter().GetResult();

        Time = new FakeTimeProvider(Now);
        Engine = engine ?? new KeywordIntentEngine();
        Service = new ConversationService(
            DbFactory, Engine,
            new IChannelConnector[] { Channel, TextOnlyChannel },
            Voice, new NullAudioTranscriber(), Media,
            new AvailabilityService(Calendar, DbFactory, Time, NullLogger<AvailabilityService>.Instance),
            new AppointmentService(Calendar, Email, Time, NullLogger<AppointmentService>.Instance),
            Notifier, Time, NullLogger<ConversationService>.Instance);
    }

    public TestDbFactory DbFactory { get; }
    public FakeTimeProvider Time { get; }
    public IIntentEngine Engine { get; }
    public ConversationService Service { get; }
    public FakeChannel Channel { get; } = new(Channels.Simulator, supportsAudio: true);
    public FakeChannel TextOnlyChannel { get; } = new(Channels.Twitter, supportsAudio: false);
    public FakeVoice Voice { get; } = new();
    public FakeMedia Media { get; } = new();
    public FakeCalendar Calendar { get; } = new();
    public FakeEmail Email { get; } = new();
    public ActivityNotifier Notifier { get; } = new();

    private int _counter;

    public Task SendAsync(string text, string sender = "user-1", string channel = Channels.Simulator, string? externalId = null) =>
        Service.ProcessInboundAsync(new InboundMessage(channel, sender, "Jane Doe", text, externalId ?? $"in-{++_counter}", Now.UtcDateTime), CancellationToken.None);

    public async Task UpdateSettingsAsync(Action<BotSettings> change)
    {
        await using var db = DbFactory.CreateDbContext();
        var s = await db.Settings.FirstAsync();
        change(s);
        await db.SaveChangesAsync();
    }

    public ReceptionistDbContext Db() => DbFactory.CreateDbContext();

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
}

public sealed class TestDbFactory : IDbContextFactory<ReceptionistDbContext>
{
    private readonly DbContextOptions<ReceptionistDbContext> _options;
    public TestDbFactory(DbContextOptions<ReceptionistDbContext> options) => _options = options;
    public ReceptionistDbContext CreateDbContext() => new(_options);
}

public sealed class FakeChannel : IChannelConnector
{
    public FakeChannel(string id, bool supportsAudio)
    {
        ChannelId = id;
        Capabilities = new ChannelCapabilities(supportsAudio, AudioFormat.Mp3, 2000);
    }

    public string ChannelId { get; }
    public string DisplayName => ChannelId;
    public bool IsConfigured => true;
    public ChannelCapabilities Capabilities { get; }
    public List<OutboundMessage> Sent { get; } = new();
    public bool Fail { get; set; }

    public Task<SendResult> SendAsync(OutboundMessage message, CancellationToken ct)
    {
        if (Fail) return Task.FromResult(SendResult.Fail("boom"));
        Sent.Add(message);
        return Task.FromResult(SendResult.Ok($"out-{Guid.NewGuid():N}"));
    }
}

public sealed class FakeVoice : IVoiceSynthesizer
{
    public bool IsConfigured { get; set; } = true;
    public List<string> Spoken { get; } = new();

    public Task<AudioClip> SynthesizeAsync(string text, string voiceName, AudioFormat format, CancellationToken ct)
    {
        Spoken.Add(text);
        return Task.FromResult(new AudioClip(new byte[] { 1, 2, 3 }, "audio/mpeg", "mp3"));
    }
}

public sealed class FakeMedia : IMediaStore
{
    public Task<string> SaveAsync(AudioClip clip, CancellationToken ct) => Task.FromResult($"https://bot.example.com/media/{Guid.NewGuid():N}.{clip.Extension}");
}

public sealed class FakeCalendar : ICalendarProvider
{
    public bool IsConfigured { get; set; } = true;
    public bool Throw { get; set; }
    public List<TimeSlot> Busy { get; } = new();
    public List<BookingRequest> Created { get; } = new();
    public List<string> Cancelled { get; } = new();

    public Task<IReadOnlyList<TimeSlot>> GetBusyAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct) =>
        Throw ? throw new HttpRequestException("Graph down") : Task.FromResult<IReadOnlyList<TimeSlot>>(Busy.ToList());

    public Task<CalendarEventResult> CreateEventAsync(BookingRequest request, CancellationToken ct)
    {
        if (Throw) throw new HttpRequestException("Graph down");
        Created.Add(request);
        return Task.FromResult(new CalendarEventResult($"evt-{Created.Count}", "https://outlook.office.com/evt"));
    }

    public Task CancelEventAsync(string eventId, CancellationToken ct)
    {
        Cancelled.Add(eventId);
        return Task.CompletedTask;
    }
}

public sealed class FakeEmail : IEmailSender
{
    public bool IsConfigured { get; set; } = true;
    public string? OwnerAddress => "owner@example.com";
    public List<EmailMessage> Sent { get; } = new();
    public bool Throw { get; set; }

    public Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        if (Throw) throw new InvalidOperationException("SMTP down");
        Sent.Add(message);
        return Task.CompletedTask;
    }
}

/// <summary>Intent engine returning scripted results, to simulate LLM behaviour deterministically.</summary>
public sealed class ScriptedEngine : IIntentEngine
{
    private readonly Queue<Func<IntentContext, IntentResult>> _script = new();
    public string Name => "scripted";
    public List<IntentContext> Contexts { get; } = new();

    public ScriptedEngine Then(Func<IntentContext, IntentResult> step)
    {
        _script.Enqueue(step);
        return this;
    }

    public Task<IntentResult> AnalyzeAsync(IntentContext context, CancellationToken ct)
    {
        Contexts.Add(context);
        return Task.FromResult(_script.Dequeue()(context));
    }
}
