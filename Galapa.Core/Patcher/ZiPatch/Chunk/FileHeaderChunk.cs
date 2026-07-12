using Galapa.Core.Patcher.ZiPatch.Util;

namespace Galapa.Core.Patcher.ZiPatch.Chunk;

/// <summary>
/// FHDR — the file header. Carries the patch type ("DIFF"/"HIST") and, in V3, the
/// per-command counts the updater uses to size its progress bar. None of it affects
/// how the patch applies; we parse it for tooling/inspection.
/// </summary>
public sealed class FileHeaderChunk(BinaryReader reader, long offset, long size)
    : ZiPatchChunk(reader, offset, size)
{
    public const string TypeName = "FHDR";
    public override string ChunkType => TypeName;

    // V1?/2
    public byte Version { get; private set; }
    public string PatchType { get; private set; } = string.Empty;
    public uint EntryFiles { get; private set; }

    // V3
    public uint AddDirectories { get; private set; }
    public uint DeleteDirectories { get; private set; }
    public long DeleteDataSize { get; private set; } // Split in 2 DWORD: Low, High
    public uint MinorVersion { get; private set; }
    public uint RepositoryName { get; private set; }
    public uint Commands { get; private set; }
    public uint SqpkAddCommands { get; private set; }
    public uint SqpkDeleteCommands { get; private set; }
    public uint SqpkExpandCommands { get; private set; }
    public uint SqpkHeaderCommands { get; private set; }
    public uint SqpkFileCommands { get; private set; }

    protected override void ReadChunk()
    {
        using var advanceAfter = GetAdvanceOnDispose();

        Version = (byte)(Reader.ReadUInt32() >> 16);
        PatchType = Reader.ReadFixedLengthString(4u);
        EntryFiles = Reader.ReadUInt32BE();

        if (Version == 3)
        {
            AddDirectories = Reader.ReadUInt32BE();
            DeleteDirectories = Reader.ReadUInt32BE();
            DeleteDataSize = Reader.ReadUInt32BE() | ((long)Reader.ReadUInt32BE() << 32);
            MinorVersion = Reader.ReadUInt32BE();
            RepositoryName = Reader.ReadUInt32BE();
            Commands = Reader.ReadUInt32BE();
            SqpkAddCommands = Reader.ReadUInt32BE();
            SqpkDeleteCommands = Reader.ReadUInt32BE();
            SqpkExpandCommands = Reader.ReadUInt32BE();
            SqpkHeaderCommands = Reader.ReadUInt32BE();
            SqpkFileCommands = Reader.ReadUInt32BE();
        }

        // Remaining payload (0xB8 for V3, 0x08 of zeros for V2) is reserved.
    }

    public override string ToString() => $"{TypeName}:V{Version}:{RepositoryName}";
}
