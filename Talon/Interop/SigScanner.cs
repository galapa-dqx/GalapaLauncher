using System.Diagnostics;
using System.Runtime.InteropServices;

// Public scanner shape adapted from Dalamud.Game.SigScanner. Matching, PE32
// decoding, and batch traversal are Talon implementations; see THIRD_PARTY_NOTICES.md.

namespace Talon.Interop;

/// <summary>Scans the live 32-bit game image for byte signatures.</summary>
public sealed partial class SigScanner : ISigScanner
{
    private readonly byte[] textCopy;
    /// <summary>Creates a scanner for the current process's main module.</summary>
    public SigScanner()
    {
        using var process = Process.GetCurrentProcess();
        Module = process.MainModule
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

        // The unpack barrier observes the page-rounded .text range becoming
        // executable before this constructor runs. Verify that every page is
        // still committed and readable, then snapshot it before hooks patch it.
        ValidateReadableRange(TextSectionBase, TextSectionSize);
        textCopy = new byte[TextSectionSize];
        Marshal.Copy(TextSectionBase, textCopy, 0, textCopy.Length);
    }

    public bool IsCopy => true;
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
        var match = ScanTextRaw(signature);
        return GetStaticAddressFromMatch(match, offset);
    }

    public nint GetStaticAddressFromMatch(nint match, int offset = 0)
    {
        var instruction = ResolveTextMatch(match) + offset;
        var opcode = ReadTextByte(instruction);
        return opcode switch
        {
            0xA1 or 0xA3 => ReadTextInt32(instruction + 1),
            0x8B or 0x89 when (ReadTextByte(instruction + 1) & 0xC7) == 0x05
                => ReadTextInt32(instruction + 2),
            _ => throw new KeyNotFoundException(
                $"Match at 0x{match:X8} did not point at a supported x86 static-address instruction."),
        };
    }

    public bool TryGetStaticAddressFromSig(string signature, out nint result, int offset = 0) =>
        Try(() => GetStaticAddressFromSig(signature, offset), out result);

    public nint ScanData(string signature) => Scan(DataSectionBase, DataSectionSize, signature);
    public bool TryScanData(string signature, out nint result) =>
        Try(() => ScanData(signature), out result);
    public nint ScanModule(string signature) =>
        ScanReadableRegions(SearchBase, Module.ModuleMemorySize, signature);
    public bool TryScanModule(string signature, out nint result) =>
        Try(() => ScanModule(signature), out result);
    public nint ResolveRelativeAddress(nint nextInstAddr, int relOffset) =>
        nextInstAddr + relOffset;

    public nint ScanText(string signature)
    {
        var result = ScanTextRaw(signature);
        return ResolveTextMatch(result);
    }

    public bool TryScanText(string signature, out nint result) =>
        Try(() => ScanText(signature), out result);
    public nint[] ScanAllText(string signature) =>
        ScanAllText(signature, CancellationToken.None).ToArray();

    public IEnumerable<nint> ScanAllText(
        string signature,
        CancellationToken cancellationToken)
    {
        var query = new SignatureQuery("single", signature);
        return ScanTextBatch([query], cancellationToken).GetMatches(query.Name);
    }

    public unsafe SignatureScanResult ScanTextBatch(
        IReadOnlyCollection<SignatureQuery> queries,
        CancellationToken cancellationToken = default)
    {
        fixed (byte* copy = textCopy)
            return ScanTextBatchCore(
                copy,
                TextSectionBase,
                textCopy.Length,
                queries,
                cancellationToken);
    }

    public nint ResolveTextMatch(nint match)
    {
        var opcode = ReadTextByte(match);
        return opcode is 0xE8 or 0xE9
            ? match + 5 + ReadTextInt32(match + 1)
            : match;
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

    private static nint Scan(nint start, int length, string signature) =>
        ScanRaw(start, length, signature, start);

    private static nint ScanRaw(nint start, int length, string signature, nint resultBase)
    {
        var pattern = Parse(signature);
        unsafe
        {
            var haystack = (byte*)start;
            for (var i = 0; i <= length - pattern.Length; i++)
                if (Matches(haystack + i, pattern))
                    return resultBase + i;
        }
        throw new KeyNotFoundException($"Signature '{signature}' was not found.");
    }

    internal static unsafe SignatureScanResult ScanTextBatch(
        nint start,
        int length,
        IReadOnlyCollection<SignatureQuery> queries,
        CancellationToken cancellationToken = default)
    {
        return ScanTextBatchCore(
            (byte*)start,
            start,
            length,
            queries,
            cancellationToken);
    }

    private static unsafe SignatureScanResult ScanTextBatchCore(
        byte* start,
        nint resultBase,
        int length,
        IReadOnlyCollection<SignatureQuery> queries,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentNullException.ThrowIfNull(queries);

        var names = new HashSet<string>(StringComparer.Ordinal);
        var compiled = new List<(SignatureQuery Query, byte?[] Pattern, List<nint> Matches)>();
        foreach (var query in queries)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(query.Name);
            if (!names.Add(query.Name))
                throw new ArgumentException(
                    $"Signature batch contains duplicate name '{query.Name}'.",
                    nameof(queries));
            compiled.Add((query, Parse(query.Pattern), []));
        }

        // Keep the image offset as the outer loop. All registered patterns inspect
        // each candidate while that part of .text is hot in the CPU cache.
        for (var offset = 0; offset < length; offset++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var entry in compiled)
            {
                if (offset <= length - entry.Pattern.Length &&
                    Matches(start + offset, entry.Pattern))
                    entry.Matches.Add(resultBase + offset);
            }
        }

        var results = compiled.ToDictionary(
            entry => entry.Query.Name,
            entry => entry.Matches.ToArray(),
            StringComparer.Ordinal);
        return new SignatureScanResult(results);
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

    private unsafe nint ScanTextRaw(string signature)
    {
        fixed (byte* copy = textCopy)
            return ScanRaw((nint)copy, textCopy.Length, signature, TextSectionBase);
    }

    private byte ReadTextByte(nint address)
    {
        var offset = checked((int)(address - TextSectionBase));
        if ((uint)offset >= (uint)textCopy.Length)
            return Marshal.ReadByte(address);
        return textCopy[offset];
    }

    private int ReadTextInt32(nint address)
    {
        var offset = checked((int)(address - TextSectionBase));
        if ((uint)offset <= (uint)(textCopy.Length - sizeof(int)))
            return BitConverter.ToInt32(textCopy, offset);
        return Marshal.ReadInt32(address);
    }

    private static nint ScanReadableRegions(nint start, int length, string signature)
    {
        var pattern = Parse(signature);
        var current = (nuint)start;
        var end = checked(current + (nuint)length);
        while (current < end)
        {
            if (VirtualQuery((nint)current, out var info, (nuint)Marshal.SizeOf<MemoryBasicInformation>()) == 0)
                break;
            var regionEnd = checked((nuint)info.BaseAddress + info.RegionSize);
            if (regionEnd <= current) break;
            var scanEnd = regionEnd < end ? regionEnd : end;
            if (IsReadable(info) && scanEnd - current >= (nuint)pattern.Length)
            {
                unsafe
                {
                    var candidate = (byte*)current;
                    var count = checked((int)(scanEnd - current));
                    for (var offset = 0; offset <= count - pattern.Length; offset++)
                        if (Matches(candidate + offset, pattern))
                            return (nint)(current + (nuint)offset);
                }
            }
            current = regionEnd;
        }
        throw new KeyNotFoundException($"Signature '{signature}' was not found.");
    }

    private static void ValidateReadableRange(nint start, int length)
    {
        var current = (nuint)start;
        var end = checked(current + (nuint)length);
        while (current < end)
        {
            if (VirtualQuery((nint)current, out var info, (nuint)Marshal.SizeOf<MemoryBasicInformation>()) == 0 ||
                !IsReadable(info))
                throw new BadImageFormatException("The game .text section is not fully committed and readable.");
            var regionEnd = checked((nuint)info.BaseAddress + info.RegionSize);
            if (regionEnd <= current)
                throw new BadImageFormatException("The game .text memory map is invalid.");
            current = regionEnd < end ? regionEnd : end;
        }
    }

    private static bool IsReadable(MemoryBasicInformation info) =>
        info.State == MemoryCommit &&
        (info.Protect & (PageNoAccess | PageGuard)) == 0;

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

    private const uint MemoryCommit = 0x1000;
    private const uint PageNoAccess = 0x01;
    private const uint PageGuard = 0x100;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public nint BaseAddress;
        public nint AllocationBase;
        public uint AllocationProtect;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nuint VirtualQuery(
        nint address,
        out MemoryBasicInformation information,
        nuint length);
}
