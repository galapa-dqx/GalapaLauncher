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

    /// <summary>SqPackHeader offset of the buildDate field (buildTime follows at 0x1C).</summary>
    private const int BuildStampOffset = 0x18;

    /// <summary>SqPackHeader offset of the 20-byte SHA-1 self-hash, computed over bytes [0, 0x3C0).</summary>
    private const int HeaderHashOffset = 0x3C0;

    public override void ApplyChunk(ZiPatchConfig config)
    {
        // DQXUpdater only writes Dat headers; Index header commands do nothing.
        if (FileKind != TargetFileKind.Dat)
            return;

        // The version header (the SqPackHeader at offset 0) carries the source build's
        // buildDate/buildTime; the updater zeros them (8 bytes at 0x18) and then recomputes
        // the header's SHA-1 self-hash (20 bytes at 0x3C0, over the preceding 0x3C0 bytes),
        // since zeroing the stamp invalidates the patch's stored hash. Match both.
        if (HeaderKind == TargetHeaderKind.Version)
        {
            Array.Clear(HeaderData, BuildStampOffset, 8);
            var hash = System.Security.Cryptography.SHA1.HashData(HeaderData.AsSpan(0, HeaderHashOffset));
            hash.CopyTo(HeaderData.AsSpan(HeaderHashOffset));
        }

        TargetFile.ResolvePath(config.Platform);

        var file = config.Store == null
            ? TargetFile.OpenStream(config.GamePath, FileMode.OpenOrCreate)
            : TargetFile.OpenStream(config.Store, config.GamePath, FileMode.OpenOrCreate);

        file.WriteFromOffset(HeaderData, HeaderKind == TargetHeaderKind.Version ? 0 : HeaderSize);
    }

    public override string ToString() => $"{TypeName}:{CommandName}:{FileKind}:{HeaderKind}:{TargetFile}";
}
