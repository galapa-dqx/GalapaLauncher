namespace Galapa.Core.Patcher.ZiPatch.Util;

/// <summary>
/// A SqPack file addressed by the (mainId, subId, fileId) triple that AddData,
/// Header and Index commands carry.
///
/// DQX divergence from FFXIV: the triple renders to a *decimal* filename,
/// <c>data{mainId:D4}{subId:D4}.{platform}</c> (e.g. <c>data00010000.win32</c>),
/// with no <c>sqpack/exN/</c> folder nesting. FFXIV instead uses a hex name under
/// <c>/sqpack/{ffxiv|exN}/</c>. The decimal form matches both the real on-disk
/// <c>data*.win32.dat*</c> filenames and DQXUpdater's <c>"%s/data%04d%04d%s.dat%u"</c>
/// builder.
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

    protected virtual string GetFileName(ZiPatchConfig.PlatformId platform) =>
        $"data{MainId:D4}{SubId:D4}.{platform.ToString().ToLowerInvariant()}";

    public void ResolvePath(ZiPatchConfig.PlatformId platform) =>
        RelativePath = GetFileName(platform);

    public override string ToString() =>
        // Default to Win32 for prints; PS4/Wii U builds are not something we install.
        GetFileName(ZiPatchConfig.PlatformId.Win32);
}
