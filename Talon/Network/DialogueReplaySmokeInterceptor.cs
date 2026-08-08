namespace Talon.Network;

// Proves asynchronous hold and unchanged replay once per connection generation.
internal sealed class DialogueReplaySmokeInterceptor : IInboundPacketInterceptor
{
    private readonly object sync = new();
    private readonly HashSet<(nint Connection, long Generation)> testedConnections = [];

    public PacketSelector Selector => new(0x47, 0x3CA8);

    public async ValueTask<PacketDecision> InterceptAsync(
        InboundPacket packet,
        CancellationToken cancellationToken)
    {
        lock (sync)
        {
            if (!testedConnections.Add((packet.Connection, packet.ConnectionGeneration)))
                return PacketDecision.Original;
        }

        Log.Info($"network smoke: holding packet {packet.PacketId} for 250ms");
        await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        return PacketDecision.Original;
    }
}
