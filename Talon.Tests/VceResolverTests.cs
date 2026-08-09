using System.Runtime.InteropServices;
using Talon.Network;

namespace Talon.Tests;

public sealed class VceResolverTests
{
    [Fact]
    public void RecognizesDisp8IndirectCallWithoutSib()
    {
        var memory = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.Copy(new byte[] { 0xFF, 0x56, 0x5C, 0x90 }, 0, memory, 4);
            Assert.True(VceResolver.IsIndirectCallWithDisp8(memory, 0x5C));

            Marshal.Copy(new byte[] { 0xFF, 0x54, 0x24, 0x5C }, 0, memory, 4);
            Assert.False(VceResolver.IsIndirectCallWithDisp8(memory, 0x5C));
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    [Fact]
    public void RecognizesThreeNearbyCallsToSameTarget()
    {
        var memory = Marshal.AllocHGlobal(0x40);
        try
        {
            var bytes = Enumerable.Repeat((byte)0x90, 0x40).ToArray();
            foreach (var offset in new[] { 0, 8, 16 })
            {
                bytes[offset] = 0xE8;
                var target = memory + 0x30;
                BitConverter.GetBytes(checked((int)(target - (memory + offset + 5))))
                    .CopyTo(bytes, offset + 1);
            }
            Marshal.Copy(bytes, 0, memory, bytes.Length);

            Assert.True(VceResolver.HasTripleDirectCallToSameTarget(memory, memory + 0x40));
        }
        finally { Marshal.FreeHGlobal(memory); }
    }
}
