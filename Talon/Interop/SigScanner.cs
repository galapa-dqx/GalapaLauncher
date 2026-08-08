using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Talon.Interop;

/// <summary>Scans the live 32-bit game image for byte signatures.</summary>
public sealed class SigScanner : ISigScanner
{
    /// <summary>Creates a scanner for the current process's main module.</summary>
    public SigScanner()
    {
        Module = Process.GetCurrentProcess().MainModule
            ?? throw new InvalidOperationException("The current process has no main module.");
        SearchBase = Module.BaseAddress;

        unsafe
        {
            var image = (byte*)Module.BaseAddress;
            if (*(ushort*)image != 0x5A4D)
                throw new BadImageFormatException("The game module has no DOS header.");
            var nt = image + *(int*)(image + 0x3C);
            if (*(uint*)nt != 0x00004550 || *(ushort*)(nt + 24) != 0x010B)
                throw new BadImageFormatException("The game module is not PE32.");

            var sectionCount = *(ushort*)(nt + 6);
            var optionalSize = *(ushort*)(nt + 20);
            var section = nt + 24 + optionalSize;
            for (var i = 0; i < sectionCount; i++, section += 40)
            {
                var name = Marshal.PtrToStringAnsi((nint)section, 8)?.TrimEnd('\0');
                var virtualSize = *(uint*)(section + 8);
                var virtualAddress = *(uint*)(section + 12);
                AssignSection(name, virtualAddress, checked((int)virtualSize));
            }
        }

        if (TextSectionSize == 0)
            throw new BadImageFormatException("The game module has no .text section.");
    }

    public bool IsCopy => false;
    public nint SearchBase { get; }
    public nint TextSectionBase { get; private set; }
    public long TextSectionOffset { get; private set; }
    public int TextSectionSize { get; private set; }
    public nint DataSectionBase { get; private set; }
    public long DataSectionOffset { get; private set; }
    public int DataSectionSize { get; private set; }
    public nint RDataSectionBase { get; private set; }
    public long RDataSectionOffset { get; private set; }
    public int RDataSectionSize { get; private set; }
    public ProcessModule Module { get; }

    public nint GetStaticAddressFromSig(string signature, int offset = 0)
    {
        var instruction = ScanText(signature) + offset;
        var opcode = Marshal.ReadByte(instruction);
        return opcode switch
        {
            0xA1 or 0xA3 => Marshal.ReadInt32(instruction + 1),
            0x8B when Marshal.ReadByte(instruction + 1) is 0x0D or 0x15 or 0x1D or 0x35 or 0x3D
                => Marshal.ReadInt32(instruction + 2),
            _ => throw new KeyNotFoundException(
                $"Signature '{signature}' did not point at a supported x86 static-address instruction."),
        };
    }

    public bool TryGetStaticAddressFromSig(string signature, out nint result, int offset = 0) =>
        Try(() => GetStaticAddressFromSig(signature, offset), out result);

    public nint ScanData(string signature) => Scan(DataSectionBase, DataSectionSize, signature);
    public bool TryScanData(string signature, out nint result) =>
        Try(() => ScanData(signature), out result);
    public nint ScanModule(string signature) =>
        Scan(SearchBase, Module.ModuleMemorySize, signature);
    public bool TryScanModule(string signature, out nint result) =>
        Try(() => ScanModule(signature), out result);
    public nint ResolveRelativeAddress(nint nextInstAddr, int relOffset) =>
        nextInstAddr + relOffset;

    public nint ScanText(string signature)
    {
        var result = Scan(TextSectionBase, TextSectionSize, signature);
        var opcode = Marshal.ReadByte(result);
        if (opcode is 0xE8 or 0xE9)
            result = result + 5 + Marshal.ReadInt32(result + 1);
        return result;
    }

    public bool TryScanText(string signature, out nint result) =>
        Try(() => ScanText(signature), out result);
    public nint[] ScanAllText(string signature) =>
        ScanAllText(signature, CancellationToken.None).ToArray();

    public IEnumerable<nint> ScanAllText(
        string signature,
        CancellationToken cancellationToken)
    {
        var pattern = Parse(signature);
        for (var i = 0; i <= TextSectionSize - pattern.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Matches(TextSectionBase + i, pattern))
                yield return TextSectionBase + i;
        }
    }

    private void AssignSection(string? name, uint offset, int size)
    {
        switch (name)
        {
            case ".text":
                TextSectionOffset = offset;
                TextSectionBase = SearchBase + checked((int)offset);
                TextSectionSize = size;
                break;
            case ".data":
                DataSectionOffset = offset;
                DataSectionBase = SearchBase + checked((int)offset);
                DataSectionSize = size;
                break;
            case ".rdata":
                RDataSectionOffset = offset;
                RDataSectionBase = SearchBase + checked((int)offset);
                RDataSectionSize = size;
                break;
        }
    }

    private static nint Scan(nint start, int length, string signature)
    {
        var pattern = Parse(signature);
        unsafe
        {
            var haystack = (byte*)start;
            for (var i = 0; i <= length - pattern.Length; i++)
                if (Matches(haystack + i, pattern))
                    return start + i;
        }
        throw new KeyNotFoundException($"Signature '{signature}' was not found.");
    }

    private static byte?[] Parse(string signature)
    {
        var tokens = signature.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) throw new FormatException("Signature is empty.");
        var result = new byte?[tokens.Length];
        for (var i = 0; i < tokens.Length; i++)
            result[i] = tokens[i] is "?" or "??" ? null : Convert.ToByte(tokens[i], 16);
        return result;
    }

    private static unsafe bool Matches(byte* candidate, byte?[] pattern)
    {
        for (var i = 0; i < pattern.Length; i++)
            if (pattern[i] is { } value && candidate[i] != value)
                return false;
        return true;
    }

    private static bool Matches(nint candidate, byte?[] pattern)
    {
        for (var i = 0; i < pattern.Length; i++)
            if (pattern[i] is { } value && Marshal.ReadByte(candidate + i) != value)
                return false;
        return true;
    }

    private static bool Try(Func<nint> action, out nint result)
    {
        try
        {
            result = action();
            return true;
        }
        catch (KeyNotFoundException)
        {
            result = 0;
            return false;
        }
    }
}
