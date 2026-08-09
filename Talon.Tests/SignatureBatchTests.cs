using System.Runtime.InteropServices;
using Talon.Interop;

namespace Talon.Tests;

public sealed class SignatureBatchTests
{
    [Fact]
    public void FindsEveryNamedPatternInOneBatch()
    {
        var memory = Marshal.AllocHGlobal(8);
        try
        {
            Marshal.Copy(
                new byte[] { 0xAA, 0x10, 0xCC, 0xAA, 0x20, 0xCC, 0xDD, 0x00 },
                0,
                memory,
                8);

            var result = SigScanner.ScanTextBatch(
                memory,
                8,
                [
                    new SignatureQuery("wildcard", "AA ?? CC"),
                    new SignatureQuery("suffix", "CC DD"),
                ]);

            Assert.Equal(new[] { memory, memory + 3 }, result.GetMatches("wildcard"));
            Assert.Equal(new[] { memory + 5 }, result.GetMatches("suffix"));
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    [Fact]
    public void RejectsDuplicateNames()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            SigScanner.ScanTextBatch(
                0,
                0,
                [new SignatureQuery("same", "AA"), new SignatureQuery("same", "BB")]));

        Assert.Contains("duplicate name", exception.Message);
    }
}
