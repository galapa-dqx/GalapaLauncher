using Galapa.Core.Patcher.ZiPatch;
using Galapa.Core.Patcher.ZiPatch.Util;

namespace Galapa.Core.Tests.Patcher;

/// <summary>
/// Locks in the DQX-specific SqPack filename mapping (decimal <c>data%04d%04d</c> under
/// <c>Content/Data/</c>, the <c>.idx</c> index extension, no <c>sqpack/exN/</c> nesting) —
/// the part of the format that diverges from FFXIV and that the boot patch alone doesn't
/// exercise. Verified against real patches (see <c>SqpackFile</c> remarks).
/// </summary>
public class SqpackFileNamingTests
{
    [Theory]
    [InlineData(0, 0, 0, "Content/Data/data00000000.win32.dat0")]
    [InlineData(1, 0, 0, "Content/Data/data00010000.win32.dat0")]
    [InlineData(1, 0, 7, "Content/Data/data00010000.win32.dat7")]
    [InlineData(13, 0, 2, "Content/Data/data00130000.win32.dat2")]
    public void DatFile_UsesDecimalNaming(ushort mainId, ushort subId, uint fileId, string expected)
    {
        var dat = new SqpackDatFile(Triple(mainId, subId, fileId));
        dat.ResolvePath(ZiPatchConfig.PlatformId.Win32);
        Assert.Equal(expected, dat.RelativePath);
    }

    [Theory]
    [InlineData(0, 0, 0, "Content/Data/data00000000.win32.idx")]   // fileId 0 => no numeric suffix
    [InlineData(2, 0, 1, "Content/Data/data00020000.win32.idx1")]
    public void IndexFile_UsesIdxExtension(ushort mainId, ushort subId, uint fileId, string expected)
    {
        var idx = new SqpackIndexFile(Triple(mainId, subId, fileId));
        idx.ResolvePath(ZiPatchConfig.PlatformId.Win32);
        Assert.Equal(expected, idx.RelativePath);
    }

    [Fact]
    public void DatFile_HonoursPlatformExtension()
    {
        var dat = new SqpackDatFile(Triple(1, 0, 0));
        dat.ResolvePath(ZiPatchConfig.PlatformId.Cafe);
        Assert.Equal("Content/Data/data00010000.cafe.dat0", dat.RelativePath);
    }

    private static BinaryReader Triple(ushort mainId, ushort subId, uint fileId)
    {
        byte[] bytes =
        [
            (byte)(mainId >> 8), (byte)mainId,
            (byte)(subId >> 8), (byte)subId,
            (byte)(fileId >> 24), (byte)(fileId >> 16), (byte)(fileId >> 8), (byte)fileId,
        ];
        return new BinaryReader(new MemoryStream(bytes));
    }
}
