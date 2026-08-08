using Talon.Network;

namespace Talon.Tests;

public sealed class PcapNgWriterTests
{
    [Fact]
    public void WritesUserZeroInterfaceAndTalonPseudoHeader()
    {
        var path = Path.Combine(Path.GetTempPath(), $"talon-{Guid.NewGuid():N}.pcapng");
        try
        {
            using (var writer = new PcapNgWriter(path))
            {
                writer.Observe(new InboundPacket(
                    7,
                    0x1234,
                    2,
                    0x47,
                    0x3CA8,
                    new byte[] { 0x47, 0xA8, 0x3C }));
            }

            var bytes = File.ReadAllBytes(path);
            Assert.Equal(0x0A0D0D0Au, BitConverter.ToUInt32(bytes, 0));
            Assert.Equal((ushort)147, BitConverter.ToUInt16(bytes, 36));
            Assert.Contains(new byte[] { (byte)'T', (byte)'L', (byte)'N', (byte)'1' }, bytes);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
