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
            var headerOffset = bytes.AsSpan().IndexOf(
                new byte[] { (byte)'T', (byte)'L', (byte)'N', (byte)'1' });
            Assert.True(headerOffset >= 0);
            Assert.Equal(0, bytes[headerOffset + 30]);
            Assert.Equal(0, bytes[headerOffset + 31]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WriterFaultDoesNotEscapeDispose()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"talon-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var writer = new PcapNgWriter(directory);

            writer.Dispose();
        }
        finally
        {
            Directory.Delete(directory);
        }
    }
}
