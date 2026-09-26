using System.Threading.Channels;

namespace AiReceptionist.Core.Abstractions;

/// <summary>Unbounded in-memory <see cref="IInboundQueue"/>. Swap for Azure Service Bus / Storage Queues to scale out.</summary>
public sealed class InMemoryInboundQueue : IInboundQueue
{
    private readonly Channel<InboundMessage> _channel = Channel.CreateUnbounded<InboundMessage>(
        new UnboundedChannelOptions { SingleReader = true });

    public ValueTask EnqueueAsync(InboundMessage message, CancellationToken ct = default) => _channel.Writer.WriteAsync(message, ct);

    public IAsyncEnumerable<InboundMessage> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

public sealed class ActivityNotifier : IActivityNotifier
{
    public event Action<ActivityEvent>? Changed;

    public void Publish(ActivityEvent evt)
    {
        // Subscribers are Blazor circuits; one faulty circuit must not break message processing.
        foreach (var handler in Changed?.GetInvocationList() ?? Array.Empty<Delegate>())
        {
            try { ((Action<ActivityEvent>)handler)(evt); }
            catch { /* ignored */ }
        }
    }
}
