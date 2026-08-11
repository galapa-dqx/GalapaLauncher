namespace Galapa.Core.Patcher.ZiPatch.Util;

/// <summary>
/// A SqPack index file: <c>data{mainId:D4}{subId:D4}.{platform}.idx{fileId}</c>
/// (the <c>fileId</c> suffix is omitted when 0).
///
/// DQX divergence from FFXIV: the index extension is <c>.idx</c>, not FFXIV's
/// <c>.index</c> — matching the real on-disk <c>data00010000.win32.idx</c> files.
/// </summary>
public sealed class SqpackIndexFile(BinaryReader reader) : SqpackFile(reader)
{
    protected override string GetFileName(ZiPatchConfig.PlatformId platform) =>
        $"{base.GetFileName(platform)}.idx{(FileId == 0 ? string.Empty : FileId.ToString())}";
}
