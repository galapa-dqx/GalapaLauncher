using System.Buffers.Binary;
using Talon.Network;

namespace Talon.Tests;

public sealed class PcapNgWriterTests
{
    [Fact]
    public void AcceptsBareFileName()
    {
        var fileName = $"talon-{Guid.NewGuid():N}.pcapng";
        var fullPath = Path.GetFullPath(fileName);
        try
        {
            using (new PcapNgWriter(fileName))
            {
            }

            Assert.True(File.Exists(fullPath));
        }
        finally
        {
            File.Delete(fullPath);
        }
    }

    [Fact]
    public void WritesUserZeroInterfaceAndTalonPseudoHeader()
    {
        var path = Path.Combine(Path.GetTempPath(), $"talon-{Guid.NewGuid():N}.pcapng");
        try
        {
            using (var writer = new PcapNgWriter(path))
            {
                writer.Observe(new InboundPacket(
                    7,
                    0x1234,
                    2,
                    0x47,
                    0x3CA8,
                    new byte[] { 0x47, 0xA8, 0x3C }));
            }

            var bytes = File.ReadAllBytes(path);
            Assert.Equal(0x0A0D0D0Au, BitConverter.ToUInt32(bytes, 0));
            Assert.Equal((ushort)147, BitConverter.ToUInt16(bytes, 36));
            var headerOffset = bytes.AsSpan().IndexOf(
                new byte[] { (byte)'T', (byte)'L', (byte)'N', (byte)'1' });
            Assert.True(headerOffset >= 0);
            Assert.Equal(0, bytes[headerOffset + 30]);
            Assert.Equal(0, bytes[headerOffset + 31]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WriterFaultDoesNotEscapeDispose()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"talon-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var writer = new PcapNgWriter(directory);

            writer.Dispose();
        }
        finally
        {
            Directory.Delete(directory);
        }
    }

    [Fact]
    public async Task HeldPacketWritesObservedAndHeldLifecycleRecords()
    {
        var path = Path.Combine(Path.GetTempPath(), $"talon-{Guid.NewGuid():N}.pcapng");
        try
        {
            var service = new PacketHandlerService();
            service.Register(new OriginalInterceptor());
            using (var writer = new PcapNgWriter(path))
            {
                service.Register((IInboundPacketObserver)writer);
                Assert.True(service.TryHold(0x1234, 2, [0x47, 0xA8, 0x3C]));
                _ = await WaitForPacket(service);
            }

            var records = ReadLifecycleRecords(File.ReadAllBytes(path));
            Assert.Equal(
                [PacketCaptureEvent.Observed, PacketCaptureEvent.Held],
                records.Select(record => record.Event));
            Assert.Equal(records[0].PacketId, records[1].PacketId);
        }
        finally
        {
            File.Delete(path);
        }
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

    private static List<(ulong PacketId, PacketCaptureEvent Event)> ReadLifecycleRecords(
        byte[] bytes)
    {
        var records = new List<(ulong, PacketCaptureEvent)>();
        for (var offset = 48; offset < bytes.Length;)
        {
            var blockLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4)));
            Assert.True(blockLength >= 12 && offset + blockLength <= bytes.Length);
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset)) == 6)
            {
                const int packetDataOffset = 28;
                const int packetIdOffset = packetDataOffset + 8;
                const int eventOffset = packetDataOffset + 25;
                records.Add((
                    BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset + packetIdOffset)),
                    (PacketCaptureEvent)bytes[offset + eventOffset]));
            }
            offset += blockLength;
        }
        return records;
    }

    private sealed class OriginalInterceptor : IInboundPacketInterceptor
    {
        public PacketSelector Selector => new(0x47);

        public ValueTask InterceptAsync(
            HeldInboundPacket packet,
            CancellationToken cancellationToken)
        {
            packet.TryReinjectOriginal();
            return ValueTask.CompletedTask;
        }
    }
}
