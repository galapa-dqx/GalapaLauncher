using System.Buffers.Binary;
using System.Collections.Concurrent;

namespace Talon.Network;

// Selects packets, runs bounded asynchronous handlers, and queues completed replay.
internal sealed class PacketHandlerService
{
    // Each held packet owns a managed copy. These limits keep a stalled or broken
    // translator from growing the in-process backlog without bound. When a limit
    // is reached, the detour passes the packet to VCE synchronously instead.
    private const int MaximumHeldPackets = 256;
    private const long MaximumHeldBytes = 8 * 1024 * 1024;
    // A handler that does not finish must fail open so the original packet can
    // return to the live connection.
    private static readonly TimeSpan DefaultHandlerTimeout = TimeSpan.FromSeconds(60);

    private readonly object registrationLock = new();
    private readonly Dictionary<byte, IInboundPacketInterceptor> opcodeHandlers = [];
    private readonly SortedDictionary<(byte Opcode, int Offset, ushort Marker), IInboundPacketInterceptor>
        markerHandlers = [];
    private readonly List<IInboundPacketObserver> observers = [];
    private readonly ConcurrentQueue<CompletedPacket> completed = new();
    private readonly TimeSpan handlerTimeout;
    private long nextPacketId;
    private int heldPacketCount;
    private long heldByteCount;

    public PacketHandlerService() : this(DefaultHandlerTimeout)
    {
    }

    internal PacketHandlerService(TimeSpan handlerTimeout)
    {
        if (handlerTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(handlerTimeout));
        this.handlerTimeout = handlerTimeout;
    }

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
                        (interceptor.Selector.Opcode, interceptor.Selector.MarkerOffset, marker),
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
        var reservation = new HoldReservation(this, bytes.Length);

        foreach (var observer in observerSnapshot)
        {
            if (observer is not IInboundPacketLifecycleObserver lifecycleObserver) continue;
            try { lifecycleObserver.Held(packet); }
            catch (Exception exception) { Log.Error("packet lifecycle observer failed", exception); }
        }

        // CompleteAsync invokes extension code before its first await. Queue the
        // whole operation so that synchronous setup cannot block VCE's thread.
        _ = Task.Run(() => CompleteAsync(
            session,
            packet,
            bytes,
            selection.Handler,
            reservation));
        return true;
    }

    public bool TryDequeue(out CompletedPacket packet)
    {
        if (!completed.TryDequeue(out packet)) return false;
        packet.Reservation.Release();
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
        byte[] original,
        IInboundPacketInterceptor handler,
        HoldReservation reservation)
    {
        Task<PacketDecision>? handlerTask = null;
        PacketDecision decision;
        try
        {
            using var timeout = new CancellationTokenSource(handlerTimeout);
            handlerTask = handler.InterceptAsync(packet, timeout.Token).AsTask();
            decision = await handlerTask
                .WaitAsync(timeout.Token)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Log.Error($"packet {packet.PacketId} handler failed; replaying original", exception);
            decision = PacketDecision.Original;
            if (handlerTask is { IsCompleted: false })
            {
                // The replay queue and unfinished handler share the copied packet.
                // Keep one reservation reference for each owner so capacity is not
                // released until both have stopped retaining it.
                reservation.Retain();
                _ = handlerTask.ContinueWith(
                    static (task, state) =>
                    {
                        _ = task.Exception;
                        ((HoldReservation)state!).Release();
                    },
                    reservation,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        byte[] replay;
        try
        {
            // Reuse the copy retained by TryHold when no replacement is needed.
            // If replacement materialization fails, the original is still safe
            // to enqueue without another allocation.
            replay = decision.Replace ? decision.Data.ToArray() : original;
        }
        catch (Exception exception)
        {
            Log.Error(
                $"packet {packet.PacketId} replay preparation failed; replaying original",
                exception);
            replay = original;
        }
        if (replay.Length == 0 || replay.Length > MaximumHeldBytes)
            replay = original;
        if (!reservation.TryResize(replay.Length)) replay = original;
        completed.Enqueue(new CompletedPacket(
            packet.PacketId,
            session,
            packet.ConnectionGeneration,
            packet.Opcode,
            packet.Marker,
            reservation,
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
        HoldReservation Reservation,
        byte[] Data);

    internal sealed class HoldReservation
    {
        private readonly PacketHandlerService owner;
        private int byteCount;
        private int references = 1;

        public HoldReservation(PacketHandlerService owner, int byteCount)
        {
            this.owner = owner;
            this.byteCount = byteCount;
        }

        public void Retain() => Interlocked.Increment(ref references);

        public bool TryResize(int newByteCount)
        {
            var delta = newByteCount - byteCount;
            if (delta > 0 &&
                Interlocked.Add(ref owner.heldByteCount, delta) > MaximumHeldBytes)
            {
                Interlocked.Add(ref owner.heldByteCount, -delta);
                return false;
            }
            if (delta < 0) Interlocked.Add(ref owner.heldByteCount, delta);
            byteCount = newByteCount;
            return true;
        }

        public void Release()
        {
            if (Interlocked.Decrement(ref references) != 0) return;
            Interlocked.Decrement(ref owner.heldPacketCount);
            Interlocked.Add(ref owner.heldByteCount, -byteCount);
        }
    }
}
