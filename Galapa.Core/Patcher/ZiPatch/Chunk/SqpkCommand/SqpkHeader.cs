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
        HeaderData = Reader.ReadBytesRequired(HeaderSize);
    }

    public override void ApplyChunk(ZiPatchConfig config)
    {
        // DQXUpdater's ZiPatch_WriteSqpackHeader writes the Dat header VERBATIM whenever the 'H'
        // command is reached (Index-file 'H' is a no-op, and it never creates a missing .dat). It
        // does NOT condition the write on anything — no buildDate-zeroing, no SHA-1 recompute, no
        // "is it modified" check. The reason an 'H' sometimes appears NOT to apply is that the patch
        // ABORTED earlier (at an unresolvable data command — see SqpkAddData/DeleteData/ExpandData),
        // so the later 'H' chunks simply never run. Confirmed by oracle trace of the writer hook:
        // 6.0->6.1 writes all 16 dat headers; 7.0.303921.4->7.0.306094.1 writes NONE because it
        // aborts at data00130000.dat1 (its base dat0 doesn't exist) before the 'H' block.
        if (FileKind != TargetFileKind.Dat)
            return;

        TargetFile.ResolvePath(config.Platform);
        if (!TargetFile.Exists(config.GamePath))
            return;

        var file = config.Store == null
            ? TargetFile.OpenStream(config.GamePath, FileMode.Open)
            : TargetFile.OpenStream(config.Store, config.GamePath, FileMode.Open);

        file.WriteFromOffset(HeaderData, HeaderKind == TargetHeaderKind.Version ? 0 : HeaderSize);
    }

    public override string ToString() => $"{TypeName}:{CommandName}:{FileKind}:{HeaderKind}:{TargetFile}";
}
