using Galapa.Core.Patcher.ZiPatch.Util;

namespace Galapa.Core.Patcher.ZiPatch.Chunk.SqpkCommand;

/// <summary>SQPK 'H' — overwrite a 0x400-byte header region of a .dat or .idx.</summary>
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
        TargetFile.ResolvePath(config.Platform);

        var file = config.Store == null
            ? TargetFile.OpenStream(config.GamePath, FileMode.OpenOrCreate)
            : TargetFile.OpenStream(config.Store, config.GamePath, FileMode.OpenOrCreate);

        file.WriteFromOffset(HeaderData, HeaderKind == TargetHeaderKind.Version ? 0 : HeaderSize);
    }

    public override string ToString() => $"{TypeName}:{CommandName}:{FileKind}:{HeaderKind}:{TargetFile}";
}
