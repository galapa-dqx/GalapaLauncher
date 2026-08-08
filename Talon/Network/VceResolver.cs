using System.Runtime.InteropServices;
using Talon.Interop;

namespace Talon.Network;

// Resolves VCE functions from a signature plus stable control-flow anchors.
internal sealed class VceResolver(ISigScanner scanner)
{
    // Binary Ninja: Vce_iSession_ParseFrame.
    private const string FrameParserPrologue =
        "55 8B EC 83 ?? ?? A1 ?? ?? ?? ?? 33 C5 89 ?? ?? 83 ?? ?? ?? " +
        "56 57 8B ?? ?? 8B F1 73 ?? 5F 33 C0 5E 8B ?? ?? 33 CD " +
        "E8 ?? ?? ?? ?? 8B E5 5D C2 ?? ?? 53 E8 ?? ?? ?? ?? " +
        "89 ?? ?? 89 ?? ?? 0F";

    // Binary Ninja: Vce_NormalSelectPoller_Poll.
    private const string SelectPollerPrologue =
        "55 8B EC B8 ?? ?? ?? ?? E8 ?? ?? ?? ?? A1 ?? ?? ?? ?? " +
        "33 C5 89 ?? ?? 8B C1 C7 85 F4 FF FE FF ?? ?? ?? ?? " +
        "53 33 DB C7 85 F8 7F FF FF ?? ?? ?? ?? 89 9D ?? ?? ?? ?? " +
        "8B ?? ?? 89 85 ?? ?? ?? ?? 56";

    public nint ResolveFrameParser()
    {
        var candidates = new HashSet<nint>();
        foreach (var candidate in scanner.ScanAllText(FrameParserPrologue))
        {
            var windowStart = candidate;
            var windowEnd = Min(
                scanner.TextSectionBase + scanner.TextSectionSize,
                candidate + 0x380);
            // The parser dispatches through the session's 0x64 and 0x5C slots and
            // handles VCE control types 0x16 and 0x1B.
            if (!Contains(windowStart, windowEnd, p => IsIndirectCallWithDisp8(p, 0x64)) ||
                !Contains(windowStart, windowEnd, p => IsIndirectCallWithDisp8(p, 0x5C)) ||
                !ContainsBytes(windowStart, windowEnd, 0x6A, 0x16) ||
                !ContainsBytes(windowStart, windowEnd, 0x6A, 0x1B))
                continue;
            candidates.Add(candidate);
        }

        return RequireUnique(candidates, "VCE frame parser");
    }

    public nint ResolveSelectPoller()
    {
        var candidates = new HashSet<nint>();
        foreach (var candidate in scanner.ScanAllText(SelectPollerPrologue))
        {
            var windowEnd = Min(
                scanner.TextSectionBase + scanner.TextSectionSize,
                candidate + 0x400);
            // The normal poll path uses zero-timeout select and calls the same
            // queue helper three times in a compact block.
            if (!Contains(
                    candidate,
                    windowEnd,
                    address => Marshal.ReadByte(address) == 0x6A &&
                               Marshal.ReadByte(address + 1) == 0x00 &&
                               Marshal.ReadByte(address + 2) == 0xFF &&
                               Marshal.ReadByte(address + 3) == 0x15))
                continue;
            if (HasTripleDirectCallToSameTarget(candidate, windowEnd))
                candidates.Add(candidate);
        }

        return RequireUnique(candidates, "VCE normal-select poller");
    }

    private static bool HasTripleDirectCallToSameTarget(nint start, nint end)
    {
        var calls = new List<(nint Address, nint Target)>();
        for (var address = start; address + 5 < end; address++)
            if (Marshal.ReadByte(address) == 0xE8)
                calls.Add((address, address + 5 + Marshal.ReadInt32(address + 1)));

        for (var i = 0; i < calls.Count; i++)
            if (calls.Skip(i)
                .TakeWhile(call => call.Address - calls[i].Address <= 0x80)
                .Count(call => call.Target == calls[i].Target) >= 3)
                return true;
        return false;
    }

    private static bool IsIndirectCallWithDisp8(nint address, byte displacement)
    {
        if (Marshal.ReadByte(address) != 0xFF) return false;
        var modRm = Marshal.ReadByte(address + 1);
        return (modRm & 0xF8) == 0x50 && Marshal.ReadByte(address + 2) == displacement;
    }

    private static bool ContainsBytes(nint start, nint end, byte first, byte second) =>
        Contains(start, end, address =>
            Marshal.ReadByte(address) == first && Marshal.ReadByte(address + 1) == second);

    private static bool Contains(nint start, nint end, Func<nint, bool> predicate)
    {
        for (var address = start; address + 4 < end; address++)
            if (predicate(address)) return true;
        return false;
    }

    private static nint RequireUnique(HashSet<nint> candidates, string description)
    {
        if (candidates.Count != 1)
            throw new InvalidOperationException(
                $"{description} resolver expected one structural match but found {candidates.Count}.");
        return candidates.Single();
    }

    private static nint Min(nint left, nint right) => left < right ? left : right;
}
