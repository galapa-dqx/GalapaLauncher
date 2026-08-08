using System.Buffers.Binary;
using System.Collections.Concurrent;

namespace Talon.Network;

// Selects packets, runs bounded asynchronous handlers, and queues completed replay.
internal sealed class PacketHandlerService
{
    private const int MaximumHeldPackets = 256;
    private const long MaximumHeldBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan HandlerTimeout = TimeSpan.FromSeconds(60);

    private readonly object registrationLock = new();
    private readonly Dictionary<byte, IInboundPacketInterceptor> opcodeHandlers = [];
    private readonly Dictionary<(byte Opcode, ushort Marker, int Offset), IInboundPacketInterceptor>
        markerHandlers = [];
    private readonly List<IInboundPacketObserver> observers = [];
    private readonly ConcurrentQueue<CompletedPacket> completed = new();
    private long nextPacketId;
    private int heldPacketCount;
    private long heldByteCount;

    public void Register(IInboundPacketInterceptor interceptor)
    {
        lock (registrationLock)
        {
            if (interceptor.Selector.Marker is { } marker)
            {
                if (interceptor.Selector.MarkerOffset < 0)
                    throw new ArgumentOutOfRangeException(
                        nameof(interceptor),
                        "Packet marker offsets cannot be negative.");
                if (!markerHandlers.TryAdd(
                        (interceptor.Selector.Opcode, marker, interceptor.Selector.MarkerOffset),
                        interceptor))
                    throw new InvalidOperationException(
                        $"Duplicate packet selector opcode=0x{interceptor.Selector.Opcode:X2}, " +
                        $"marker=0x{marker:X4}, offset={interceptor.Selector.MarkerOffset}.");
            }
            else if (!opcodeHandlers.TryAdd(interceptor.Selector.Opcode, interceptor))
            {
                throw new InvalidOperationException(
                    $"Duplicate packet selector opcode=0x{interceptor.Selector.Opcode:X2}.");
            }
        }
    }

    public void Register(IInboundPacketObserver observer)
    {
        lock (registrationLock) observers.Add(observer);
    }

    public bool TryHold(
        nint session,
        long generation,
        ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return false;
        var opcode = data[0];
        var selection = FindHandler(opcode, data);
        IInboundPacketObserver[] observerSnapshot;
        lock (registrationLock) observerSnapshot = observers.ToArray();
        if (selection.Handler is null && observerSnapshot.Length == 0)
            return false;

        var marker = selection.Marker;
        var packetId = unchecked((ulong)Interlocked.Increment(ref nextPacketId));
        var bytes = data.ToArray();
        var packet = new InboundPacket(packetId, session, generation, opcode, marker, bytes);

        foreach (var observer in observerSnapshot)
        {
            try { observer.Observe(packet); }
            catch (Exception exception) { Log.Error("packet observer failed", exception); }
        }

        if (selection.Handler is null) return false;
        if (Interlocked.Increment(ref heldPacketCount) > MaximumHeldPackets)
        {
            Interlocked.Decrement(ref heldPacketCount);
            Log.Warning("packet hold limit reached; passing packet through");
            return false;
        }
        if (Interlocked.Add(ref heldByteCount, bytes.Length) > MaximumHeldBytes)
        {
            Interlocked.Add(ref heldByteCount, -bytes.Length);
            Interlocked.Decrement(ref heldPacketCount);
            Log.Warning("packet hold byte limit reached; passing packet through");
            return false;
        }

        _ = CompleteAsync(session, packet, selection.Handler);
        return true;
    }

    public bool TryDequeue(out CompletedPacket packet)
    {
        if (!completed.TryDequeue(out packet)) return false;
        Interlocked.Decrement(ref heldPacketCount);
        Interlocked.Add(ref heldByteCount, -packet.ReservedBytes);
        return true;
    }

    private (IInboundPacketInterceptor? Handler, ushort? Marker) FindHandler(
        byte opcode,
        ReadOnlySpan<byte> data)
    {
        lock (registrationLock)
        {
            foreach (var pair in markerHandlers)
            {
                if (pair.Key.Opcode == opcode &&
                    MatchesMarker(data, pair.Key.Marker, pair.Key.Offset))
                    return (pair.Value, pair.Key.Marker);
            }
            return opcodeHandlers.TryGetValue(opcode, out var handler)
                ? (handler, null)
                : (null, null);
        }
    }

    private async Task CompleteAsync(
        nint session,
        InboundPacket packet,
        IInboundPacketInterceptor handler)
    {
        PacketDecision decision;
        try
        {
            using var timeout = new CancellationTokenSource(HandlerTimeout);
            decision = await handler.InterceptAsync(packet, timeout.Token)
                .AsTask()
                .WaitAsync(HandlerTimeout)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Log.Error($"packet {packet.PacketId} handler failed; replaying original", exception);
            decision = PacketDecision.Original;
        }
        var replay = decision.Replace ? decision.Data.ToArray() : packet.Data.ToArray();
        if (replay.Length == 0 || replay.Length > MaximumHeldBytes)
            replay = packet.Data.ToArray();
        var reservationDelta = replay.Length - packet.Data.Length;
        if (reservationDelta > 0 &&
            Interlocked.Add(ref heldByteCount, reservationDelta) > MaximumHeldBytes)
        {
            Interlocked.Add(ref heldByteCount, -reservationDelta);
            replay = packet.Data.ToArray();
        }
        else if (reservationDelta < 0)
        {
            Interlocked.Add(ref heldByteCount, reservationDelta);
        }
        completed.Enqueue(new CompletedPacket(
            packet.PacketId,
            session,
            packet.ConnectionGeneration,
            packet.Opcode,
            packet.Marker,
            replay.Length,
            replay));
    }

    private static bool MatchesMarker(ReadOnlySpan<byte> data, ushort marker, int offset) =>
        offset <= data.Length - sizeof(ushort) &&
        BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]) == marker;

    internal readonly record struct CompletedPacket(
        ulong PacketId,
        nint Session,
        long Generation,
        byte Opcode,
        ushort? Marker,
        int ReservedBytes,
        byte[] Data);
}
