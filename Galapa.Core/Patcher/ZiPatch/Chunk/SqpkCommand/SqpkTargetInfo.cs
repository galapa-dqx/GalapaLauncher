using Galapa.Core.Patcher.ZiPatch.Util;

namespace Galapa.Core.Patcher.ZiPatch.Chunk.SqpkCommand;

/// <summary>
/// SQPK 'T' — target platform/region/version metadata. On modern patchers only
/// <see cref="Platform"/> matters; we apply it so SqPack-triple filenames resolve to
/// the right platform extension.
/// </summary>
internal sealed class SqpkTargetInfo(BinaryReader reader, long offset, long size)
    : SqpkChunk(reader, offset, size)
{
    public const string CommandName = "T";

    // US/EU/JP are Global; ZH appears to be Global too; KR is unknown.
    public enum RegionId : short
    {
        Global = -1,
    }

    public ZiPatchConfig.PlatformId Platform { get; private set; }
    public RegionId Region { get; private set; }
    public bool IsDebug { get; private set; }
    public ushort Version { get; private set; }
    public ulong DeletedDataSize { get; private set; }
    public ulong SeekCount { get; private set; }

    protected override void ReadChunk()
    {
        using var advanceAfter = GetAdvanceOnDispose();
        Reader.ReadBytes(3); // Reserved

        Platform = (ZiPatchConfig.PlatformId)Reader.ReadUInt16BE();
        Region = (RegionId)Reader.ReadInt16BE();
        IsDebug = Reader.ReadInt16BE() != 0;
        Version = Reader.ReadUInt16BE();
        DeletedDataSize = Reader.ReadUInt64(); // little-endian
        SeekCount = Reader.ReadUInt64();       // little-endian

        // ~0x60 reserved bytes follow (skipped on dispose).
    }

    public override void ApplyChunk(ZiPatchConfig config) => config.Platform = Platform;

    public override string ToString() =>
        $"{TypeName}:{CommandName}:{Platform}:{Region}:{IsDebug}:{Version}:{DeletedDataSize}:{SeekCount}";
}
