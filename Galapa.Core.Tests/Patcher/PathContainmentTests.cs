using Galapa.Core.Patcher.ZiPatch;
using Galapa.Core.Patcher.ZiPatch.Util;

namespace Galapa.Core.Tests.Patcher;

/// <summary>
/// Path-containment guard: a patch must not be able to address files outside the game root
/// via ".." segments or an absolute path (a "zip-slip"). Every patch-supplied path — the
/// SqpkFile 'F' verbatim path and the ADIR/DELD directory names — is routed through
/// <see cref="SqexFile.ResolveUnderBase"/>, which is what this exercises directly.
/// </summary>
public class PathContainmentTests
{
    private static string Root => Path.Combine(Path.GetTempPath(), "galapa-root");

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("a/../../escape.txt")]
    [InlineData("Content/../../escape.txt")]
    [InlineData("../../../../../../etc/passwd")]
    public void ResolveUnderBase_RejectsTraversal(string relative) =>
        Assert.Throws<ZiPatchException>(() => SqexFile.ResolveUnderBase(Root, relative));

    [Theory]
    [InlineData("Content/Data/data00000000.win32.dat0")]
    [InlineData("Bin/BurakqOnn!pcs--!qca")]
    [InlineData("/Content/Data/x.idx")] // a leading slash is trimmed, so it stays contained
    public void ResolveUnderBase_AllowsContainedPaths(string relative)
    {
        var resolved = SqexFile.ResolveUnderBase(Root, relative);
        Assert.StartsWith(Path.GetFullPath(Root), resolved);
    }
}
