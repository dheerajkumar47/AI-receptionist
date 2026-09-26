using AiReceptionist.Core.Abstractions;
using AiReceptionist.Core.Conversations;
using AiReceptionist.Core.Data;
using AiReceptionist.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace AiReceptionist.Web.Workers;

/// <summary>Drains the inbound queue and runs each message through the <see cref="ConversationService"/>.
/// Messages are processed one at a time so replies within a conversation always stay in order.</summary>
public sealed class InboundProcessingWorker : BackgroundService
{
    private readonly IInboundQueue _queue;
    private readonly ConversationService _conversations;
    private readonly ILogger<InboundProcessingWorker> _log;

    public InboundProcessingWorker(IInboundQueue queue, ConversationService conversations, ILogger<InboundProcessingWorker> log)
    {
        _queue = queue;
        _conversations = conversations;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var message in _queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await _conversations.ProcessInboundAsync(message, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "Failed to process {Channel} message {Id}.", message.Channel, message.ExternalMessageId);
            }
        }
    }
}

/// <summary>Runs one polling loop per configured <see cref="IPollingChannel"/> (e.g. X DMs), persisting the cursor.</summary>
public sealed class ChannelPollingWorker : BackgroundService
{
    private readonly IEnumerable<IChannelConnector> _channels;
    private readonly IInboundQueue _queue;
    private readonly IDbContextFactory<ReceptionistDbContext> _dbFactory;
    private readonly ILogger<ChannelPollingWorker> _log;

    public ChannelPollingWorker(IEnumerable<IChannelConnector> channels, IInboundQueue queue,
        IDbContextFactory<ReceptionistDbContext> dbFactory, ILogger<ChannelPollingWorker> log)
    {
        _channels = channels;
        _queue = queue;
        _dbFactory = dbFactory;
        _log = log;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(_channels.OfType<IPollingChannel>().Where(c => c.IsConfigured).Select(c => PollLoopAsync(c, stoppingToken)));

    private async Task PollLoopAsync(IPollingChannel channel, CancellationToken ct)
    {
        _log.LogInformation("Polling {Channel} every {Interval}.", channel.ChannelId, channel.PollInterval);
        var delay = channel.PollInterval;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync(ct);
                var cursor = await db.ChannelCursors.FindAsync(new object[] { channel.ChannelId }, ct);
                var result = await channel.PollAsync(cursor?.Cursor, ct);

                foreach (var m in result.Messages) await _queue.EnqueueAsync(m, ct);

                if (cursor is null) db.ChannelCursors.Add(cursor = new ChannelCursor { Channel = channel.ChannelId });
                cursor.Cursor = result.NextCursor;
                cursor.UpdatedUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                delay = channel.PollInterval;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Back off on errors (rate limits, network) up to 15 minutes.
                delay = TimeSpan.FromSeconds(Math.Min(900, Math.Max(delay.TotalSeconds * 2, channel.PollInterval.TotalSeconds)));
                _log.LogWarning(ex, "Polling {Channel} failed; retrying in {Delay}.", channel.ChannelId, delay);
            }

            try { await Task.Delay(delay, ct); }
            catch (OperationCanceledException) { break; }
        }
    }
}
