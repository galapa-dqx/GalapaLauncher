using Galapa.Core.Patcher.ZiPatch.Util;

namespace Galapa.Core.Patcher.ZiPatch.Chunk.SqpkCommand;

/// <summary>
/// SQPK 'H' — overwrite a 0x400-byte header region of a .dat.
///
/// DQX semantics (from DQXUpdater's ZiPatch_WriteSqpackHeader): only <b>Dat</b> headers
/// are written — Index-file header commands are a no-op. And when writing the version
/// header (<see cref="TargetHeaderKind.Version"/>, the SqPackHeader at offset 0), the
/// updater zeros the buildDate/buildTime fields (offsets 0x18/0x1C) rather than writing
/// the source build's stamp the patch carries. We replicate both so our output is
/// byte-identical to the updater.
/// </summary>
internal sealed class SqpkHeader(BinaryReader reader, long offset, long size)
    : SqpkChunk(reader, offset, size)
{
    public const string CommandName = "H";

    public const int HeaderSize = 1024;

    public enum TargetFileKind : byte
    {
        Dat = (byte)'D',
        Index = (byte)'I',
    }

    public enum TargetHeaderKind : byte
    {
        Version = (byte)'V', // write at offset 0
        Index = (byte)'I',   // write at offset 0x400
        Data = (byte)'D',    // write at offset 0x400
    }

    public TargetFileKind FileKind { get; private set; }
    public TargetHeaderKind HeaderKind { get; private set; }
    public SqpackFile TargetFile { get; private set; } = null!;

    public byte[] HeaderData { get; private set; } = [];
    public long HeaderDataSourceOffset { get; private set; }

    protected override void ReadChunk()
    {
        using var advanceAfter = GetAdvanceOnDispose();
        FileKind = (TargetFileKind)Reader.ReadByte();
        HeaderKind = (TargetHeaderKind)Reader.ReadByte();
        Reader.ReadByte(); // Alignment

        TargetFile = FileKind == TargetFileKind.Dat
            ? new SqpackDatFile(Reader)
            : new SqpackIndexFile(Reader);

        HeaderDataSourceOffset = Offset + Reader.BaseStream.Position;
        HeaderData = Reader.ReadBytes(HeaderSize);
    }

    public override void ApplyChunk(ZiPatchConfig config)
    {
        // No-op. Dynamic tracing of DQXUpdater (WriteFile/SetFilePointerEx hooks over a real
        // patch apply) shows it never writes a .dat's SqPack header region (offset 0 or 0x400)
        // for incremental patches — only AddFile whole-file writes touch offset 0. So the
        // updater simply ignores SQPK 'H' commands against existing .dat files; the headers
        // stay as the base (or the AddFile that created the file) left them. We do the same so
        // our output matches: the patch's header bytes (with their stale buildDate/dataSize)
        // are never written over the live headers.
    }

    public override string ToString() => $"{TypeName}:{CommandName}:{FileKind}:{HeaderKind}:{TargetFile}";
}
