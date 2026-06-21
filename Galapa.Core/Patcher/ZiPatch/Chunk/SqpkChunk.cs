using Galapa.Core.Patcher.ZiPatch.Chunk.SqpkCommand;
using Galapa.Core.Patcher.ZiPatch.Util;

namespace Galapa.Core.Patcher.ZiPatch.Chunk;

/// <summary>
/// SQPK — the workhorse chunk. Its payload restates the chunk size, then a 1-char
/// opcode selecting one of the SqPack sub-commands (AddData, DeleteData, ExpandData,
/// Header, File, Index, PatchInfo, TargetInfo), then that command's body.
/// </summary>
public abstract class SqpkChunk(BinaryReader reader, long offset, long size)
    : ZiPatchChunk(reader, offset, size)
{
    public const string TypeName = "SQPK";
    public override string ChunkType => TypeName;

    private static readonly Dictionary<string, Func<BinaryReader, long, long, SqpkChunk>> CommandTypes = new()
    {
        { SqpkAddData.CommandName, (r, o, s) => new SqpkAddData(r, o, s) },
        { SqpkDeleteData.CommandName, (r, o, s) => new SqpkDeleteData(r, o, s) },
        { SqpkExpandData.CommandName, (r, o, s) => new SqpkExpandData(r, o, s) },
        { SqpkHeader.CommandName, (r, o, s) => new SqpkHeader(r, o, s) },
        { SqpkFile.CommandName, (r, o, s) => new SqpkFile(r, o, s) },
        { SqpkIndex.CommandName, (r, o, s) => new SqpkIndex(r, o, s) },
        { SqpkTargetInfo.CommandName, (r, o, s) => new SqpkTargetInfo(r, o, s) },
        { SqpkPatchInfo.CommandName, (r, o, s) => new SqpkPatchInfo(r, o, s) },
    };

    public static ZiPatchChunk GetCommand(BinaryReader reader, long offset, long size)
    {
        try
        {
            // Have not seen this differ from the outer chunk size.
            var innerSize = reader.ReadInt32BE();
            if (size != innerSize)
                throw new ZiPatchException("SQPK inner size does not match chunk size.");

            var command = reader.ReadFixedLengthString(1u);
            if (!CommandTypes.TryGetValue(command, out var constructor))
                throw new ZiPatchException($"Unknown SQPK command '{command}'.");

            // innerSize - 5 = body length (minus the u32 size and 1-char opcode).
            return constructor(reader, offset, innerSize - 5);
        }
        catch (EndOfStreamException e)
        {
            throw new ZiPatchException("Could not get SQPK command", e);
        }
    }

    public override string ToString() => TypeName;
}
