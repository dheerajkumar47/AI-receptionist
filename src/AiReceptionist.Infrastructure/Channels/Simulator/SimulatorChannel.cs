using AiReceptionist.Core.Abstractions;
using ChannelIds = AiReceptionist.Core.Domain.Channels;

namespace AiReceptionist.Infrastructure.Channels.Simulator;

/// <summary>
/// Built-in test channel driven from the dashboard's Simulator page. Replies are stored (and voice clips
/// playable) exactly like real channels, which makes it ideal for rehearsing flows before going live.
/// </summary>
public sealed class SimulatorChannel : IChannelConnector
{
    public string ChannelId => ChannelIds.Simulator;
    public string DisplayName => "Dashboard simulator";
    public bool IsConfigured => true;
    public ChannelCapabilities Capabilities { get; } = new(SupportsAudio: true, AudioFormat.Mp3, MaxTextLength: 4000);

    public Task<SendResult> SendAsync(OutboundMessage message, CancellationToken ct) =>
        Task.FromResult(SendResult.Ok($"sim-{Guid.NewGuid():N}"));
}
