using System.Buffers;
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
    public async Task MarkerSelectorOnlyMatchesItsConfiguredOffset()
    {
        var service = new PacketHandlerService();
        service.Register(new ReplacementInterceptor(new(0x47), [0x01]));
        service.Register(new ReplacementInterceptor(new(0x47, 0x3CA8, 1), [0x02]));

        Assert.True(service.TryHold(0x1234, 1, [0x47, 0x00, 0xA8, 0x3C]));
        var completed = await WaitForPacket(service);

        Assert.Equal([0x01], completed.Data);
        Assert.Null(completed.Marker);
    }

    [Fact]
    public async Task LowestMatchingMarkerOffsetWinsRegardlessOfRegistrationOrder()
    {
        var service = new PacketHandlerService();
        service.Register(new ReplacementInterceptor(new(0x47, 0x5678, 3), [0x03]));
        service.Register(new ReplacementInterceptor(new(0x47, 0x1234, 1), [0x01]));

        Assert.True(service.TryHold(0x1234, 1, [0x47, 0x34, 0x12, 0x78, 0x56]));
        var completed = await WaitForPacket(service);

        Assert.Equal([0x01], completed.Data);
        Assert.Equal((ushort)0x1234, completed.Marker);
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

    [Fact]
    public void CompletedPacketsRemainInsideHoldLimitUntilDequeued()
    {
        var service = new PacketHandlerService();
        service.Register(new ReplacementInterceptor(new(0x50), [0x50]));

        var held = Enumerable.Range(0, 300)
            .Count(_ => service.TryHold(0x1234, 1, [0x50]));

        Assert.Equal(256, held);
        for (var i = 0; i < held; i++)
            Assert.True(service.TryDequeue(out _));
        Assert.True(service.TryHold(0x1234, 1, [0x50]));
    }

    [Fact]
    public void PacketLargerThanHeldByteLimitPassesThroughWithoutConsumingCapacity()
    {
        var service = new PacketHandlerService();
        service.Register(new ReplacementInterceptor(new(0x50), [0x50]));
        var oversized = new byte[8 * 1024 * 1024 + 1];
        oversized[0] = 0x50;

        Assert.False(service.TryHold(0x1234, 1, oversized));
        Assert.True(service.TryHold(0x1234, 1, [0x50]));
    }

    [Fact]
    public async Task ReplayPreparationFailureQueuesOriginalPacket()
    {
        var service = new PacketHandlerService();
        service.Register(new FaultingReplacementInterceptor());
        byte[] original = [0x50, 0x01, 0x02];

        Assert.True(service.TryHold(0x1234, 1, original));
        var completed = await WaitForPacket(service);

        Assert.Equal(original, completed.Data);
        Assert.True(service.TryHold(0x1234, 1, original));
        _ = await WaitForPacket(service);
    }

    [Fact]
    public async Task TryHoldReturnsBeforeInterceptorSetupCompletes()
    {
        var service = new PacketHandlerService();
        var interceptor = new BlockingSetupInterceptor();
        service.Register(interceptor);
        using var returned = new ManualResetEventSlim();
        var held = false;
        var caller = new Thread(() =>
        {
            held = service.TryHold(0x1234, 1, [0x50]);
            returned.Set();
        })
        {
            IsBackground = true,
        };

        caller.Start();
        try
        {
            Assert.True(
                returned.Wait(TimeSpan.FromSeconds(2)),
                "TryHold blocked while the interceptor performed synchronous setup.");
            Assert.True(held);
            Assert.True(interceptor.Started.Wait(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            interceptor.Release.Set();
            Assert.True(caller.Join(TimeSpan.FromSeconds(2)));
        }

        _ = await WaitForPacket(service);
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

    private sealed class FaultingReplacementInterceptor : IInboundPacketInterceptor
    {
        private readonly FaultingMemoryManager memory = new();
        public PacketSelector Selector => new(0x50);

        public ValueTask<PacketDecision> InterceptAsync(
            InboundPacket packet,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(PacketDecision.Replacement(memory.CreateMemory()));
    }

    private sealed class BlockingSetupInterceptor : IInboundPacketInterceptor
    {
        public ManualResetEventSlim Started { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public PacketSelector Selector => new(0x50);

        public ValueTask<PacketDecision> InterceptAsync(
            InboundPacket packet,
            CancellationToken cancellationToken)
        {
            Started.Set();
            Release.Wait(cancellationToken);
            return ValueTask.FromResult(PacketDecision.Original);
        }
    }

    private sealed class FaultingMemoryManager : MemoryManager<byte>
    {
        public ReadOnlyMemory<byte> CreateMemory() => base.CreateMemory(1);
        public override Span<byte> GetSpan() => throw new InvalidOperationException("test fault");
        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
        public override void Unpin() { }
        protected override void Dispose(bool disposing) { }
    }
}
