namespace Talon.Network;

/// <summary>
/// Selects inbound packets by opcode and an optional 16-bit marker at a fixed byte offset.
/// Matching marker selectors use the lowest byte offset first.
/// </summary>
public readonly record struct PacketSelector(
    byte Opcode,
    ushort? Marker = null,
    int MarkerOffset = 1);

/// <summary>Contains a copied inbound payload and its connection identity.</summary>
public sealed record InboundPacket(
    ulong PacketId,
    nint Connection,
    long ConnectionGeneration,
    byte Opcode,
    ushort? Marker,
    ReadOnlyMemory<byte> Data);

/// <summary>
/// Gives an interceptor temporary ownership of one held inbound packet. Complete the
/// lease with exactly one reinjection method. Talon reinjects the original bytes if
/// the handler returns or reaches its deadline without completing the lease.
/// </summary>
public sealed class HeldInboundPacket
{
    private readonly TaskCompletionSource completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Func<PacketDecision, bool>? complete;
    private byte[]? data;

    internal HeldInboundPacket(
        InboundPacket packet,
        byte[] data,
        Func<PacketDecision, bool> complete)
    {
        PacketId = packet.PacketId;
        Connection = packet.Connection;
        ConnectionGeneration = packet.ConnectionGeneration;
        Opcode = packet.Opcode;
        Marker = packet.Marker;
        this.data = data;
        this.complete = complete;
    }

    /// <summary>Gets the packet's process-wide identifier.</summary>
    public ulong PacketId { get; }
    /// <summary>Gets the native VCE connection pointer.</summary>
    public nint Connection { get; }
    /// <summary>Gets the connection generation used to reject replay after destruction.</summary>
    public long ConnectionGeneration { get; }
    /// <summary>Gets the selected packet opcode.</summary>
    public byte Opcode { get; }
    /// <summary>Gets the marker that selected the packet, if any.</summary>
    public ushort? Marker { get; }
    /// <summary>Gets the original bytes while this lease is pending.</summary>
    public ReadOnlyMemory<byte> Data => Volatile.Read(ref data) ?? ReadOnlyMemory<byte>.Empty;

    /// <summary>Attempts to reinject the original packet bytes.</summary>
    public bool TryReinjectOriginal() => TryComplete(PacketDecision.Original);

    /// <summary>
    /// Attempts to reinject replacement bytes. Empty or oversized replacements cause
    /// Talon to reinject the original packet instead.
    /// </summary>
    public bool TryReinject(ReadOnlyMemory<byte> replacement) =>
        TryComplete(PacketDecision.Replacement(replacement));

    internal Task Completion => completion.Task;

    private bool TryComplete(PacketDecision decision)
    {
        var callback = Interlocked.Exchange(ref complete, null);
        if (callback is null) return false;
        try { return callback(decision); }
        finally
        {
            Volatile.Write(ref data, null);
            completion.TrySetResult();
        }
    }
}

internal readonly record struct PacketDecision(bool Replace, ReadOnlyMemory<byte> Data)
{
    public static PacketDecision Original => new(false, ReadOnlyMemory<byte>.Empty);
    public static PacketDecision Replacement(ReadOnlyMemory<byte> data) => new(true, data);
}

/// <summary>Asynchronously handles selected inbound packets.</summary>
public interface IInboundPacketInterceptor
{
    /// <summary>Gets the packets handled by this interceptor.</summary>
    PacketSelector Selector { get; }

    /// <summary>
    /// Handles one managed packet lease while the game processes other traffic.
    /// Call a reinjection method before returning to replace the original payload.
    /// </summary>
    ValueTask InterceptAsync(
        HeldInboundPacket packet,
        CancellationToken cancellationToken);
}

/// <summary>Observes copied inbound packets without holding them.</summary>
public interface IInboundPacketObserver
{
    /// <summary>Observes one inbound packet before any asynchronous transformation.</summary>
    void Observe(InboundPacket packet);
}

internal interface IInboundPacketLifecycleObserver : IInboundPacketObserver
{
    void Held(InboundPacket packet);
}
