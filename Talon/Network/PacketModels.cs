namespace Talon.Network;

/// <summary>Selects inbound packets by opcode and an optional 16-bit marker at a fixed byte offset.</summary>
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

/// <summary>Selects the bytes to use when a held packet is reinjected.</summary>
public readonly record struct PacketDecision(bool Replace, ReadOnlyMemory<byte> Data)
{
    /// <summary>Reinjects the original packet bytes.</summary>
    public static PacketDecision Original => new(false, ReadOnlyMemory<byte>.Empty);
    /// <summary>Reinjects <paramref name="data"/> instead of the original bytes.</summary>
    public static PacketDecision Replacement(ReadOnlyMemory<byte> data) => new(true, data);
}

/// <summary>Asynchronously transforms selected inbound packets.</summary>
public interface IInboundPacketInterceptor
{
    /// <summary>Gets the packets handled by this interceptor.</summary>
    PacketSelector Selector { get; }

    /// <summary>Handles one copied packet while the game continues processing other traffic.</summary>
    ValueTask<PacketDecision> InterceptAsync(
        InboundPacket packet,
        CancellationToken cancellationToken);
}

/// <summary>Observes copied inbound packets without holding them.</summary>
public interface IInboundPacketObserver
{
    /// <summary>Observes one inbound packet before any asynchronous transformation.</summary>
    void Observe(InboundPacket packet);
}
