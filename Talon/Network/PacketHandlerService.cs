using System.Buffers.Binary;
using System.Collections.Concurrent;

namespace Talon.Network;

// Selects packets, runs bounded asynchronous handlers, and queues completed replay.
internal sealed class PacketHandlerService
{
    internal const int MaximumHeldPackets = 256;
    internal const long MaximumHeldBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan DefaultHandlerTimeout = TimeSpan.FromSeconds(60);

    private readonly object registrationLock = new();
    private readonly Dictionary<byte, IInboundPacketInterceptor> opcodeHandlers = [];
    private readonly SortedDictionary<(byte Opcode, int Offset, ushort Marker), IInboundPacketInterceptor>
        markerHandlers = [];
    private readonly List<IInboundPacketObserver> observers = [];
    private volatile IInboundPacketObserver[] observerSnapshot = [];
    private volatile KeyValuePair<(byte Opcode, int Offset, ushort Marker), IInboundPacketInterceptor>[]
        markerSnapshot = [];
    private volatile IReadOnlyDictionary<byte, IInboundPacketInterceptor> opcodeSnapshot =
        new Dictionary<byte, IInboundPacketInterceptor>();
    private readonly ConcurrentQueue<CompletedPacket> completed = new();
    private readonly TimeSpan handlerTimeout;
    private long nextPacketId;
    private int heldPacketCount;
    private long heldByteCount;

    public PacketHandlerService() : this(DefaultHandlerTimeout) { }

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
                markerSnapshot = markerHandlers.ToArray();
            }
            else if (!opcodeHandlers.TryAdd(interceptor.Selector.Opcode, interceptor))
            {
                throw new InvalidOperationException(
                    $"Duplicate packet selector opcode=0x{interceptor.Selector.Opcode:X2}.");
            }
            else opcodeSnapshot = new Dictionary<byte, IInboundPacketInterceptor>(opcodeHandlers);
        }
    }

    public void Register(IInboundPacketObserver observer)
    {
        lock (registrationLock)
        {
            observers.Add(observer);
            observerSnapshot = observers.ToArray();
        }
    }

    public bool TryHold(nint session, long generation, ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty || generation == 0) return false;
        var opcode = data[0];
        var selection = FindHandler(opcode, data);
        var packetObservers = observerSnapshot;
        if (selection.Handler is null && packetObservers.Length == 0) return false;

        // Reject impossible holds before allocating the managed payload copy.
        if (selection.Handler is not null && data.Length > MaximumHeldBytes)
        {
            Log.Warning("packet exceeds the hold byte limit; passing it through");
            return false;
        }

        var marker = selection.Marker;
        var packetId = unchecked((ulong)Interlocked.Increment(ref nextPacketId));
        var bytes = data.ToArray();
        var packet = new InboundPacket(packetId, session, generation, opcode, marker, bytes);

        foreach (var observer in packetObservers)
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
        var pending = new PendingPacket(this, packet, bytes, reservation);
        var lease = new HeldInboundPacket(packet, bytes, pending.TryComplete);

        foreach (var observer in packetObservers)
        {
            if (observer is not IInboundPacketLifecycleObserver lifecycleObserver) continue;
            try { lifecycleObserver.Held(packet); }
            catch (Exception exception) { Log.Error("packet lifecycle observer failed", exception); }
        }

        // Invoke extension code wholly off the VCE thread, including synchronous
        // work performed before its first await.
        _ = Task.Run(() => RunHandlerAsync(selection.Handler, lease));
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
        foreach (var pair in markerSnapshot)
        {
            if (pair.Key.Opcode == opcode &&
                MatchesMarker(data, pair.Key.Marker, pair.Key.Offset))
                return (pair.Value, pair.Key.Marker);
        }
        return opcodeSnapshot.TryGetValue(opcode, out var handler)
            ? (handler, null)
            : (null, null);
    }

    private async Task RunHandlerAsync(
        IInboundPacketInterceptor handler,
        HeldInboundPacket packet)
    {
        var cancellation = new CancellationTokenSource();
        Task handlerTask;
        try
        {
            handlerTask = handler.InterceptAsync(packet, cancellation.Token).AsTask();
        }
        catch (Exception exception)
        {
            Log.Error($"packet {packet.PacketId} handler failed; replaying original", exception);
            packet.TryReinjectOriginal();
            cancellation.Dispose();
            return;
        }

        var timeoutTask = Task.Delay(handlerTimeout);
        var winner = await Task.WhenAny(handlerTask, packet.Completion, timeoutTask)
            .ConfigureAwait(false);
        if (winner == handlerTask)
        {
            try { await handlerTask.ConfigureAwait(false); }
            catch (Exception exception)
            {
                Log.Error($"packet {packet.PacketId} handler failed; replaying original", exception);
            }
            packet.TryReinjectOriginal();
            cancellation.Dispose();
            return;
        }

        TryCancel(cancellation, packet.PacketId);
        if (winner == timeoutTask && packet.TryReinjectOriginal())
            Log.Warning($"packet {packet.PacketId} handler timed out; replaying original");

        if (handlerTask.IsCompleted)
        {
            ObserveCompletedHandler(handlerTask, packet.PacketId);
            cancellation.Dispose();
        }
        else
        {
            // A non-cooperative task can outlive its lease, but it no longer owns
            // Talon's payload or admission reservation. Retain only its CTS until
            // the task settles so late token registrations remain valid.
            _ = ObserveLateHandlerAsync(handlerTask, cancellation, packet.PacketId);
        }
    }

    private static async Task ObserveLateHandlerAsync(
        Task handlerTask,
        CancellationTokenSource cancellation,
        ulong packetId)
    {
        try { await handlerTask.ConfigureAwait(false); }
        catch (Exception exception)
        {
            Log.Error($"packet {packetId} handler failed after replay", exception);
        }
        finally { cancellation.Dispose(); }
    }

    private static void ObserveCompletedHandler(Task task, ulong packetId)
    {
        if (task.Exception is { } exception)
            Log.Error($"packet {packetId} handler failed after replay", exception.GetBaseException());
    }

    private static void TryCancel(CancellationTokenSource cancellation, ulong packetId)
    {
        try { cancellation.Cancel(); }
        catch (Exception exception)
        {
            Log.Error($"packet {packetId} cancellation callback failed", exception);
        }
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

    private sealed class PendingPacket(
        PacketHandlerService owner,
        InboundPacket packet,
        byte[] original,
        HoldReservation reservation)
    {
        private int completed;

        public bool TryComplete(PacketDecision decision)
        {
            if (Interlocked.Exchange(ref completed, 1) != 0) return false;
            var replay = PrepareReplay(decision);
            owner.completed.Enqueue(new CompletedPacket(
                packet.PacketId,
                packet.Connection,
                packet.ConnectionGeneration,
                packet.Opcode,
                packet.Marker,
                reservation,
                replay));
            return true;
        }

        private byte[] PrepareReplay(PacketDecision decision)
        {
            byte[] replay;
            try { replay = decision.Replace ? decision.Data.ToArray() : original; }
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
            return replay;
        }
    }

    internal sealed class HoldReservation
    {
        private readonly PacketHandlerService owner;
        private int byteCount;
        private int released;

        public HoldReservation(PacketHandlerService owner, int byteCount)
        {
            this.owner = owner;
            this.byteCount = byteCount;
        }

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
            if (Interlocked.Exchange(ref released, 1) != 0) return;
            Interlocked.Decrement(ref owner.heldPacketCount);
            Interlocked.Add(ref owner.heldByteCount, -byteCount);
        }
    }
}
