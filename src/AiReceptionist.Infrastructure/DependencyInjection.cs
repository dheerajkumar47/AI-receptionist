using AiReceptionist.Core.Abstractions;
using AiReceptionist.Core.Conversations;
using AiReceptionist.Core.Data;
using AiReceptionist.Core.Scheduling;
using AiReceptionist.Infrastructure.AI;
using AiReceptionist.Infrastructure.Calendar;
using AiReceptionist.Infrastructure.Channels.Meta;
using AiReceptionist.Infrastructure.Channels.Simulator;
using AiReceptionist.Infrastructure.Channels.Twilio;
using AiReceptionist.Infrastructure.Channels.Twitter;
using AiReceptionist.Infrastructure.Email;
using AiReceptionist.Infrastructure.Voice;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AiReceptionist.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the database, conversation engine, AI/voice/calendar/email services and all channel connectors.
    /// Services whose credentials are missing are replaced by "not configured" fallbacks so the app always starts.
    /// </summary>
    public static IServiceCollection AddAiReceptionist(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<AppOptions>(config.GetSection(AppOptions.SectionName));
        services.Configure<OpenAiOptions>(config.GetSection(OpenAiOptions.SectionName));
        services.Configure<SpeechOptions>(config.GetSection(SpeechOptions.SectionName));
        services.Configure<MediaOptions>(config.GetSection(MediaOptions.SectionName));
        services.Configure<GraphCalendarOptions>(config.GetSection(GraphCalendarOptions.SectionName));
        services.Configure<SmtpOptions>(config.GetSection(SmtpOptions.SectionName));
        services.Configure<MetaOptions>(config.GetSection(MetaOptions.SectionName));
        services.Configure<TwitterOptions>(config.GetSection(TwitterOptions.SectionName));
        services.Configure<TwilioOptions>(config.GetSection(TwilioOptions.SectionName));

        var connectionString = config.GetConnectionString("Receptionist") ?? "Data Source=data/receptionist.db";
        services.AddDbContextFactory<ReceptionistDbContext>(o => o.UseSqlite(connectionString));

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IActivityNotifier, ActivityNotifier>();
        services.AddSingleton<IInboundQueue, InMemoryInboundQueue>();

        services.AddHttpClient(MetaChannelBase.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient(TwitterDmChannel.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient(TwilioWhatsAppChannel.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient(AzureSpeechSynthesizer.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(30));

        // Language understanding: Azure OpenAI / OpenAI when configured, otherwise the offline keyword engine.
        services.AddSingleton<IIntentEngine>(sp =>
            sp.GetRequiredService<IOptions<OpenAiOptions>>().Value.IsConfigured
                ? ActivatorUtilities.CreateInstance<OpenAiIntentEngine>(sp)
                : new KeywordIntentEngine());

        services.AddSingleton<IVoiceSynthesizer>(sp =>
            sp.GetRequiredService<IOptions<SpeechOptions>>().Value.IsConfigured
                ? ActivatorUtilities.CreateInstance<AzureSpeechSynthesizer>(sp)
                : new NullVoiceSynthesizer());
        services.AddSingleton<IAudioTranscriber>(sp =>
            sp.GetRequiredService<IOptions<SpeechOptions>>().Value.IsConfigured
                ? ActivatorUtilities.CreateInstance<AzureSpeechTranscriber>(sp)
                : new NullAudioTranscriber());
        services.AddSingleton<FileMediaStore>();
        services.AddSingleton<IMediaStore>(sp => sp.GetRequiredService<FileMediaStore>());

        services.AddSingleton<GraphCalendarProvider>();
        services.AddSingleton<ICalendarProvider>(sp =>
            sp.GetRequiredService<IOptions<GraphCalendarOptions>>().Value.IsConfigured
                ? sp.GetRequiredService<GraphCalendarProvider>()
                : new NullCalendarProvider());
        services.AddSingleton<IEmailSender>(sp =>
            sp.GetRequiredService<IOptions<SmtpOptions>>().Value.IsConfigured
                ? ActivatorUtilities.CreateInstance<SmtpEmailSender>(sp)
                : new NullEmailSender());

        // Channels. To add a new network: implement IChannelConnector (+ IWebhookChannel or IPollingChannel)
        // and register it here. The webhook route, poller and dashboard pick it up automatically.
        services.AddSingleton<IChannelConnector, FacebookMessengerChannel>();
        services.AddSingleton<IChannelConnector, InstagramChannel>();
        // WhatsApp: Twilio (no Meta business verification needed) when configured, otherwise Meta's Cloud API directly.
        services.AddSingleton<IChannelConnector>(sp =>
            sp.GetRequiredService<IOptions<TwilioOptions>>().Value.IsConfigured
                ? ActivatorUtilities.CreateInstance<TwilioWhatsAppChannel>(sp)
                : ActivatorUtilities.CreateInstance<WhatsAppChannel>(sp));
        services.AddSingleton<IChannelConnector, TwitterDmChannel>();
        services.AddSingleton<IChannelConnector, SimulatorChannel>();

        services.AddSingleton<AvailabilityService>();
        services.AddSingleton<AppointmentService>();
        services.AddSingleton<ConversationService>();
        return services;
    }
}
