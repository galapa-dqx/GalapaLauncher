using System.Diagnostics;
using System.Runtime.InteropServices;
using Talon.Hooking;
using Talon.Interop;

namespace Talon.Tests;

public sealed class SignatureAttributeTests
{
    [Fact]
    public void OffsetUseReadsPrimitiveAtConfiguredOffset()
    {
        var memory = Marshal.AllocHGlobal(8);
        try
        {
            Marshal.WriteInt32(memory + 2, 0x12345678);
            var target = new OffsetTarget();
            new GameInteropProvider(new FixedScanner(memory)).InitializeFromAttributes(target);

            Assert.Equal(0x12345678, target.Value);
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    private sealed class OffsetTarget
    {
        [Signature("AA", UseFlags = SignatureUseFlags.Offset, Offset = 2)]
        public int Value { get; private set; }
    }

    private sealed class FixedScanner(nint address) : ISigScanner
    {
        public bool IsCopy => false;
        public nint SearchBase => address;
        public nint TextSectionBase => address;
        public long TextSectionOffset => 0;
        public int TextSectionSize => 8;
        public nint DataSectionBase => address;
        public long DataSectionOffset => 0;
        public int DataSectionSize => 8;
        public nint RDataSectionBase => address;
        public long RDataSectionOffset => 0;
        public int RDataSectionSize => 8;
        public ProcessModule Module =>
            Process.GetCurrentProcess().MainModule ?? throw new InvalidOperationException();

        public nint GetStaticAddressFromSig(string signature, int offset = 0) =>
            address + offset;
        public bool TryGetStaticAddressFromSig(string signature, out nint result, int offset = 0)
        {
            result = address + offset;
            return true;
        }
        public nint ScanData(string signature) => address;
        public bool TryScanData(string signature, out nint result)
        {
            result = address;
            return true;
        }
        public nint ScanModule(string signature) => address;
        public bool TryScanModule(string signature, out nint result)
        {
            result = address;
            return true;
        }
        public nint ResolveRelativeAddress(nint nextInstAddr, int relOffset) =>
            nextInstAddr + relOffset;
        public nint ScanText(string signature) => address;
        public bool TryScanText(string signature, out nint result)
        {
            result = address;
            return true;
        }
        public nint[] ScanAllText(string signature) => [address];
        public IEnumerable<nint> ScanAllText(
            string signature,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return address;
        }
    }
}
