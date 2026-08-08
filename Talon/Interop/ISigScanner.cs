using System.Diagnostics;

namespace Talon.Interop;

/// <summary>Finds byte signatures and addresses in the loaded game image.</summary>
public interface ISigScanner
{
    /// <summary>Gets whether the scanner reads a private module copy.</summary>
    bool IsCopy { get; }
    /// <summary>Gets the base address used for module scans.</summary>
    nint SearchBase { get; }
    /// <summary>Gets the loaded <c>.text</c> section address.</summary>
    nint TextSectionBase { get; }
    /// <summary>Gets the <c>.text</c> section offset from the image base.</summary>
    long TextSectionOffset { get; }
    /// <summary>Gets the <c>.text</c> section size.</summary>
    int TextSectionSize { get; }
    /// <summary>Gets the loaded <c>.data</c> section address.</summary>
    nint DataSectionBase { get; }
    /// <summary>Gets the <c>.data</c> section offset from the image base.</summary>
    long DataSectionOffset { get; }
    /// <summary>Gets the <c>.data</c> section size.</summary>
    int DataSectionSize { get; }
    /// <summary>Gets the loaded <c>.rdata</c> section address.</summary>
    nint RDataSectionBase { get; }
    /// <summary>Gets the <c>.rdata</c> section offset from the image base.</summary>
    long RDataSectionOffset { get; }
    /// <summary>Gets the <c>.rdata</c> section size.</summary>
    int RDataSectionSize { get; }
    /// <summary>Gets the scanned process module.</summary>
    ProcessModule Module { get; }

    /// <summary>Resolves the static address referenced by a matching x86 instruction. Direct calls and jumps are followed before <paramref name="offset"/> is applied.</summary>
    nint GetStaticAddressFromSig(string signature, int offset = 0);
    /// <summary>Tries to resolve the static address referenced by a matching instruction.</summary>
    bool TryGetStaticAddressFromSig(string signature, out nint result, int offset = 0);
    /// <summary>Finds a signature in <c>.data</c>.</summary>
    nint ScanData(string signature);
    /// <summary>Tries to find a signature in <c>.data</c>.</summary>
    bool TryScanData(string signature, out nint result);
    /// <summary>Finds a signature in the complete module.</summary>
    nint ScanModule(string signature);
    /// <summary>Tries to find a signature in the complete module.</summary>
    bool TryScanModule(string signature, out nint result);
    /// <summary>Applies a relative displacement to the next-instruction address.</summary>
    nint ResolveRelativeAddress(nint nextInstAddr, int relOffset);
    /// <summary>Finds a signature in <c>.text</c>. A match beginning with a direct <c>call</c> or <c>jmp</c> resolves to its branch target.</summary>
    nint ScanText(string signature);
    /// <summary>Tries to find a signature in <c>.text</c> and applies the same direct-branch resolution as <see cref="ScanText"/>.</summary>
    bool TryScanText(string signature, out nint result);
    /// <summary>Finds every matching address in <c>.text</c>.</summary>
    nint[] ScanAllText(string signature);
    /// <summary>Enumerates every matching address in <c>.text</c>.</summary>
    IEnumerable<nint> ScanAllText(string signature, CancellationToken cancellationToken);
}
