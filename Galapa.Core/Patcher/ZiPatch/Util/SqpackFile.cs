namespace Galapa.Core.Patcher.ZiPatch.Util;

/// <summary>
/// A SqPack file addressed by the (mainId, subId, fileId) triple that AddData,
/// Header and Index commands carry.
///
/// DQX divergence from FFXIV: the triple renders to a *decimal* filename under
/// <c>Content/Data/</c>, e.g. <c>Content/Data/data00010000.win32.dat0</c>. FFXIV
/// instead uses a hex name under <c>/sqpack/{ffxiv|exN}/</c>.
///
/// The path (resolved relative to the game repo root, <c>&lt;install&gt;/Game</c>) is
/// verified against real patches two ways: the on-disk <c>data*.win32.dat*</c>/<c>.idx</c>
/// filenames, and the fact that a patch's AddFile op and its AddData/Header/Index ops
/// target the *same* file — e.g. AddFile writes <c>Content/Data/data00000000.win32.idx</c>
/// while a Header op patches that file's header via the triple. <c>subId</c> is 0 across
/// every sampled patch, so there is no per-expansion subdirectory.
/// </summary>
public abstract class SqpackFile : SqexFile
{
    protected ushort MainId { get; }
    protected ushort SubId { get; }
    protected uint FileId { get; }

    protected byte ExpansionId => (byte)(SubId >> 8);

    protected SqpackFile(BinaryReader reader)
    {
        MainId = reader.ReadUInt16BE();
        SubId = reader.ReadUInt16BE();
        FileId = reader.ReadUInt32BE();

        RelativePath = GetFileName(ZiPatchConfig.PlatformId.Win32);
    }

    /// <summary>The folder DQX sqpack archives live in, relative to the game repo root.</summary>
    public const string DataFolder = "Content/Data";

    protected virtual string GetFileName(ZiPatchConfig.PlatformId platform) =>
        $"{DataFolder}/data{MainId:D4}{SubId:D4}.{platform.ToString().ToLowerInvariant()}";

    public void ResolvePath(ZiPatchConfig.PlatformId platform) =>
        RelativePath = GetFileName(platform);

    public override string ToString() =>
        // Default to Win32 for prints; PS4/Wii U builds are not something we install.
        GetFileName(ZiPatchConfig.PlatformId.Win32);
}
