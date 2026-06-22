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
        // DQXUpdater's ZiPatch_WriteSqpackHeader writes only Dat headers (Index-file 'H' is a
        // no-op) and only when the .dat is already open — it never creates one. For incremental
        // patches the 'H' commands usually sit after the chunk that aborts the patch, so they're
        // never reached; when they ARE reached (e.g. a span-extension dat created via AddData,
        // whose SqPack header was never written) they DO write. So: apply to existing .dats,
        // skip missing ones.
        if (FileKind != TargetFileKind.Dat)
            return;

        TargetFile.ResolvePath(config.Platform);
        if (!TargetFile.Exists(config.GamePath))
            return;

        // Write the 0x400-byte header verbatim — the updater keeps the build stamp the patch
        // carries (it does NOT zero buildDate or recompute the SHA-1; verified against the
        // data00040000.dat1 version header in patch 6.0->6.1, buildDate 0x01348990).
        var file = config.Store == null
            ? TargetFile.OpenStream(config.GamePath, FileMode.Open)
            : TargetFile.OpenStream(config.Store, config.GamePath, FileMode.Open);

        file.WriteFromOffset(HeaderData, HeaderKind == TargetHeaderKind.Version ? 0 : HeaderSize);
    }

    public override string ToString() => $"{TypeName}:{CommandName}:{FileKind}:{HeaderKind}:{TargetFile}";
}
