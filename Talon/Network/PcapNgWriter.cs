using System.Buffers.Binary;
using System.Threading.Channels;

namespace Talon.Network;

internal enum PacketCaptureEvent : byte
{
    Observed = 1,
    Held = 2,
    Reinject = 3,
    Cancelled = 4,
}

// Writes packet lifecycle records without blocking the VCE thread on file I/O.
internal sealed class PcapNgWriter : IInboundPacketObserver, IDisposable
{
    private const ushort LinkTypeUser0 = 147;
    private const int TalonHeaderSize = 32;
    private readonly Channel<CaptureRecord> channel =
        Channel.CreateBounded<CaptureRecord>(new BoundedChannelOptions(1024)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task writerTask;

    public PcapNgWriter(string path)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        writerTask = Task.Run(() => RunAsync(fullPath, cancellation.Token));
        Log.Info($"packet capture enabled: {fullPath}");
    }

    public void Observe(InboundPacket packet) =>
        Write(packet, PacketCaptureEvent.Observed);

    public void Write(InboundPacket packet, PacketCaptureEvent captureEvent) =>
        channel.Writer.TryWrite(new CaptureRecord(
            packet.PacketId,
            packet.ConnectionGeneration,
            packet.Opcode,
            packet.Marker,
            captureEvent,
            packet.Data.ToArray(),
            DateTimeOffset.UtcNow));

    public void Write(PacketHandlerService.CompletedPacket packet, PacketCaptureEvent captureEvent) =>
        channel.Writer.TryWrite(new CaptureRecord(
            packet.PacketId,
            packet.Generation,
            packet.Opcode,
            packet.Marker,
            captureEvent,
            packet.Data,
            DateTimeOffset.UtcNow));

    public void Dispose()
    {
        channel.Writer.TryComplete();
        if (!writerTask.Wait(TimeSpan.FromSeconds(2)))
            cancellation.Cancel();
        cancellation.Dispose();
    }

    private async Task RunAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            64 * 1024,
            useAsync: true);
        using var writer = new BinaryWriter(stream);
        WriteSectionHeader(writer);
        WriteInterfaceDescription(writer);
        writer.Flush();

        await foreach (var record in channel.Reader.ReadAllAsync(cancellationToken))
        {
            WriteEnhancedPacket(writer, record);
            writer.Flush();
        }
    }

    private static void WriteSectionHeader(BinaryWriter writer)
    {
        writer.Write(0x0A0D0D0Au);
        writer.Write(28u);
        writer.Write(0x1A2B3C4Du);
        writer.Write((ushort)1);
        writer.Write((ushort)0);
        writer.Write(-1L);
        writer.Write(28u);
    }

    private static void WriteInterfaceDescription(BinaryWriter writer)
    {
        writer.Write(1u);
        writer.Write(20u);
        writer.Write(LinkTypeUser0);
        writer.Write((ushort)0);
        writer.Write(uint.MaxValue);
        writer.Write(20u);
    }

    private static void WriteEnhancedPacket(BinaryWriter writer, CaptureRecord record)
    {
        var capturedLength = TalonHeaderSize + record.Data.Length;
        var paddedLength = (capturedLength + 3) & ~3;
        var blockLength = 32 + paddedLength;
        var timestamp = checked(record.Timestamp.ToUnixTimeMilliseconds() * 1000);

        writer.Write(6u);
        writer.Write((uint)blockLength);
        writer.Write(0u);
        writer.Write((uint)((ulong)timestamp >> 32));
        writer.Write((uint)timestamp);
        writer.Write((uint)capturedLength);
        writer.Write((uint)capturedLength);

        Span<byte> header = stackalloc byte[TalonHeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x314E4C54); // TLN1
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], TalonHeaderSize);
        BinaryPrimitives.WriteUInt64LittleEndian(header[8..], record.PacketId);
        BinaryPrimitives.WriteInt64LittleEndian(header[16..], record.Generation);
        header[24] = 1; // inbound
        header[25] = (byte)record.Event;
        header[26] = record.Opcode;
        header[27] = record.Marker.HasValue ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16LittleEndian(header[28..], record.Marker ?? 0);
        writer.Write(header);
        writer.Write(record.Data);
        for (var i = capturedLength; i < paddedLength; i++) writer.Write((byte)0);
        writer.Write((uint)blockLength);
    }

    private sealed record CaptureRecord(
        ulong PacketId,
        long Generation,
        byte Opcode,
        ushort? Marker,
        PacketCaptureEvent Event,
        byte[] Data,
        DateTimeOffset Timestamp);
}
