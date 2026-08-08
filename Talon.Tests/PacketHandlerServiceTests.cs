using Talon.Network;

namespace Talon.Tests;

public sealed class PacketHandlerServiceTests
{
    [Fact]
    public void PassiveObserverDoesNotHoldPacket()
    {
        var service = new PacketHandlerService();
        var observer = new RecordingObserver();
        service.Register(observer);

        var held = service.TryHold(0x1234, 1, [0x47, 0x01]);

        Assert.False(held);
        Assert.Single(observer.Packets);
    }

    [Fact]
    public async Task MarkerSelectorTakesPrecedenceOverOpcodeSelector()
    {
        var service = new PacketHandlerService();
        service.Register(new ReplacementInterceptor(new(0x47), [0x01]));
        service.Register(new ReplacementInterceptor(new(0x47, 0x3CA8), [0x02]));

        Assert.True(service.TryHold(0x1234, 1, [0x47, 0xA8, 0x3C]));
        var completed = await WaitForPacket(service);

        Assert.Equal([0x02], completed.Data);
        Assert.Equal((ushort)0x3CA8, completed.Marker);
    }

    [Fact]
    public async Task CompletedPacketsAreDequeuedByCompletionNotArrival()
    {
        var service = new PacketHandlerService();
        service.Register(new VariableDelayInterceptor());

        Assert.True(service.TryHold(0x1234, 1, [0x50, 100]));
        Assert.True(service.TryHold(0x1234, 1, [0x50, 5]));

        var first = await WaitForPacket(service);
        var second = await WaitForPacket(service);

        Assert.Equal((byte)5, first.Data[1]);
        Assert.Equal((byte)100, second.Data[1]);
        Assert.True(first.PacketId > second.PacketId);
    }

    [Fact]
    public void DuplicateSelectorIsRejected()
    {
        var service = new PacketHandlerService();
        service.Register(new ReplacementInterceptor(new(0x47), [0x01]));

        Assert.Throws<InvalidOperationException>(() =>
            service.Register(new ReplacementInterceptor(new(0x47), [0x02])));
    }

    private static async Task<PacketHandlerService.CompletedPacket> WaitForPacket(
        PacketHandlerService service)
    {
        for (var i = 0; i < 200; i++)
        {
            if (service.TryDequeue(out var packet)) return packet;
            await Task.Delay(5);
        }
        throw new TimeoutException("No completed packet was queued.");
    }

    private sealed class RecordingObserver : IInboundPacketObserver
    {
        public List<InboundPacket> Packets { get; } = [];
        public void Observe(InboundPacket packet) => Packets.Add(packet);
    }

    private sealed class ReplacementInterceptor(
        PacketSelector selector,
        byte[] replacement) : IInboundPacketInterceptor
    {
        public PacketSelector Selector => selector;
        public ValueTask<PacketDecision> InterceptAsync(
            InboundPacket packet,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(PacketDecision.Replacement(replacement));
    }

    private sealed class VariableDelayInterceptor : IInboundPacketInterceptor
    {
        public PacketSelector Selector => new(0x50);

        public async ValueTask<PacketDecision> InterceptAsync(
            InboundPacket packet,
            CancellationToken cancellationToken)
        {
            await Task.Delay(packet.Data.Span[1], cancellationToken);
            return PacketDecision.Original;
        }
    }
}
