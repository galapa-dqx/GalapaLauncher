using System.Runtime.InteropServices;
using System.Text;
using Talon.Vfs;

namespace Talon.Tests;

public sealed class VfsHooksTests
{
    [Theory]
    [InlineData(false, 1, false, (int)VfsResolutionOutcome.OriginalHit)]
    [InlineData(false, 0, false, (int)VfsResolutionOutcome.OriginalMiss)]
    [InlineData(false, 0, true, (int)VfsResolutionOutcome.OriginalError)]
    [InlineData(true, 1, false, (int)VfsResolutionOutcome.OverrideFallbackHit)]
    [InlineData(true, 0, false, (int)VfsResolutionOutcome.OverrideFallbackMiss)]
    [InlineData(true, 0, true, (int)VfsResolutionOutcome.OverrideFallbackError)]
    public void ClassifiesOriginalResolutionOutcome(
        bool overrideFallback,
        int resource,
        bool failed,
        int expected) =>
        Assert.Equal(
            expected,
            (int)VfsHooks.ClassifyOriginalResult(overrideFallback, resource, failed));

    [Fact]
    public void DecodesReadableNullTerminatedGamePath()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var expected = "ui/メッセージ.bin";
        var encoded = Encoding.GetEncoding(932).GetBytes(expected + "\0");
        var memory = Marshal.AllocHGlobal(encoded.Length);
        try
        {
            Marshal.Copy(encoded, 0, memory, encoded.Length);
            Assert.Equal(expected, VfsHooks.DecodeGamePath(memory));
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    [Fact]
    public void RejectsUnreadableGamePathPointer()
    {
        Assert.Throws<InvalidDataException>(() => VfsHooks.DecodeGamePath((nint)1));
    }
}
