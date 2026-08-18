using System.Runtime.InteropServices;
using System.Text;
using Talon.Vfs;

namespace Talon.Tests;

public sealed class VfsHooksTests
{
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
