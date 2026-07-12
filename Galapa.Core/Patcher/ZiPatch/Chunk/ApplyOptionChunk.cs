using Galapa.Core.Patcher.ZiPatch.Util;

namespace Galapa.Core.Patcher.ZiPatch.Chunk;

/// <summary>
/// APLY — toggles an apply option (ignore-missing / ignore-old-mismatch). Both are
/// observed false in practice, but we honour them in <see cref="ApplyChunk"/>.
/// </summary>
public sealed class ApplyOptionChunk(BinaryReader reader, long offset, long size)
    : ZiPatchChunk(reader, offset, size)
{
    public const string TypeName = "APLY";
    public override string ChunkType => TypeName;

    public enum ApplyOptionKind : uint
    {
        IgnoreMissing = 1,
        IgnoreOldMismatch = 2,
    }

    public ApplyOptionKind OptionKind { get; private set; }
    public bool OptionValue { get; private set; }

    protected override void ReadChunk()
    {
        using var advanceAfter = GetAdvanceOnDispose();

        OptionKind = (ApplyOptionKind)Reader.ReadUInt32BE();

        // Discarded padding, always 0x0000_0004 as far as observed.
        Reader.ReadBytes(4);

        var value = Reader.ReadUInt32BE() != 0;

        OptionValue = OptionKind is ApplyOptionKind.IgnoreMissing or ApplyOptionKind.IgnoreOldMismatch && value;
    }

    public override void ApplyChunk(ZiPatchConfig config)
    {
        switch (OptionKind)
        {
            case ApplyOptionKind.IgnoreMissing:
                config.IgnoreMissing = OptionValue;
                break;

            case ApplyOptionKind.IgnoreOldMismatch:
                config.IgnoreOldMismatch = OptionValue;
                break;
        }
    }

    public override string ToString() => $"{TypeName}:{OptionKind}:{OptionValue}";
}
