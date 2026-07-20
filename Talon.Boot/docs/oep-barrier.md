# Bulk post-unpack barrier

Status: **implemented and verified end to end on DQX 8.0 (2026-07-19).**

## Result

Talon no longer waits for the first `Vfs_LoadResource` call and does not need the
true game OEP. The reliable barrier is the packer's final page-protection request:

1. The Injector reads the suspended process's mapped PE entrypoint and arms DR0 there.
2. The early LoadLibrary APC installs Talon.Boot's VEH and scan/install worker.
3. After the APC returns, the entrypoint breakpoint fires before the first packed
   instruction; Boot retargets DR0 to `NtProtectVirtualMemory`.
4. The VEH accepts only `PAGE_EXECUTE_READ` over exactly the page-rounded `.text` range.
5. At that bulk transition it clears DR0 and parks the unpacker before the call executes.
6. The worker resolves and installs all hooks, then releases the unpacker.

The protection transition—not scanner success—defines unpack completion. The scanner
only resolves version-specific hook targets; zero or multiple matches mean an
unsupported build. This closes the lost-first-call gap and patches no system DLL bytes.

## The packer identification correction

The entry wrapper resembles a familiar ASPack `pushad`/computed-return shape, but the
actual loader is not the textbook ASPack layout assumed by the earlier investigation.
The packed executable's PE entrypoint is RVA `0x196C`, which calls a first-stage loader
at RVA `0x1000`. That loader decodes a private 32-byte record whose magic is `KONN`,
loads a second stage at RVA `0x02001000`, and transfers to its entry at RVA
`0x02023A00`.

Consequently, ASPack-specific static offsets and the classic ESP data-breakpoint trick
were the wrong abstraction. The runtime barrier ultimately needs neither: the useful
stable interface is the native bulk page-protection transition.

Background material that informed, but does not identify, the target:

- https://unprotect.it/technique/aspack/
- https://medium.com/@mjak/unpack-as-s-pack-890238c387dd
- https://gist.github.com/abhisek/3659931
- https://github.com/orcastor/unpack
- https://github.com/SabreTools/BinaryObjectScanner/issues/74

## Historical KONN record decoding

The current record is at raw file offset `0x1000`:

```
8DA03932 C3EE7679 518EB6C7 A11D7670
443AD83A 887121F8 12E4ED1C 23CBC48A
```

Decode eight little-endian dwords as follows:

```text
decoded[0] = raw[0]
previous = raw[0]
for i = 0..6:
    decoded[i + 1] = raw[i + 1] XOR previous
    previous = (raw[i + 1] - i + previous) XOR (i * i)   // uint32
```

Result:

```
8DA03932 4E4E4F4B 0000196C 02001000
000004E0 000494E0 02023A00 000000A0
```

`decoded[1]` is the bytes `KONN`, `decoded[2]` equals the PE entrypoint RVA,
`decoded[3]` is the second-stage base RVA, and `decoded[6]` is its entry RVA.

This record was useful for identifying and instrumenting the unpacker during research.
Production Talon no longer parses it or exposes packer-stage logic in the Injector.

## Why the protection predicate originally missed

The verified completion request was:

```
NtProtectVirtualMemory(
    BaseAddress = 0x005A1000,
    RegionSize  = 0x00F944CD,
    NewProtect  = PAGE_EXECUTE_READ)
```

The PE `.text` range was `[0x005A1000, 0x01536000)`. A raw byte comparison says the
request ends just short. `NtProtectVirtualMemory`, however, operates on pages: it
rounds the beginning down and the end up. The rounded request is exactly
`[0x005A1000, 0x01536000)`.

The completion predicate therefore uses:

```text
pageBegin = requestedBase & ~0xFFF
pageEnd   = (requestedBase + requestedSize + 0xFFF) & ~0xFFF
complete = NewProtect == PAGE_EXECUTE_READ
        && pageBegin == textPageBegin
        && pageEnd   == textPageEnd
```

Comparing unrounded values was the key bug in the abandoned strategy. Requiring the
exact range and final `RX` protection also avoids treating broader or writable staging
transitions as completion.

## Threading and exception details

DR0 changes made inside the early-bird APC are discarded when the APC dispatcher
restores its saved pre-APC context. The Injector instead sets DR0 while the primary
thread is still suspended, before queuing the APC. That debug state is part of the
saved context Windows restores afterward. The entrypoint trap is therefore a
deterministic post-APC rendezvous with no worker-scheduling race.

At every `NtProtectVirtualMemory` execute trap, the VEH sets x86 EFLAGS.RF before
continuing. Clearing DR6 alone causes a repeating `Single Step` exception. Non-matching
calls resume immediately. At the one exact bulk transition, the VEH clears DR0 and
waits while the worker scans and installs; MinHook never runs inside the VEH.

A 30-second timeout fails open for game startup but closed for hooking: debug registers
are cleared and no partially resolved hook is installed.

## Scanner-driven VFS resolution

After the barrier, Talon scans committed,
readable regions of executable PE sections. The signature includes the compiler
prologue, wildcarded stack/call/relocation values, the security-cookie sequence, and
VFS-specific object member loads. The unpacked 8.0 image initially produced ten matches
with the generic 28-byte compiler prologue; extending through the member loads reduced
that to the single verified target.

Talon refuses to hook if the final signature has zero or multiple matches. This makes a
game patch a logged, safe failure instead of a jump to a stale fixed address.

## Launch

No unpack-specific Injector option is required:

```powershell
$token = (& dotnet Galapa.Toolbox\bin\Debug\net8.0-windows10.0.19041.0\Galapa.Toolbox.dll token).Trim()
Talon.Injector\bin\Debug\net8.0-windows\Talon.Injector.exe `
  --boot-dll D:\Code\DQXLauncher\Talon.Boot\bin\x86\Debug\Talon.Boot.dll `
  --override-dir D:\path\to\override `
  -- `
  "D:\Program Files (x86)\SquareEnix\DRAGON QUEST X\Game\DQXGame.exe" `
  "-StartupToken=$token" -USE_APARTMENTTHREADED
```

The Injector's only barrier-adjacent responsibility is the generic mapped-entrypoint
rendezvous. It contains no KONN, packer-stage, `.text`, scanner, or hook knowledge;
all unpack semantics remain in Talon.Boot.

## Verification evidence

The end-to-end run produced:

```text
[talon] entry rendezvous: 0x006E196C (imageBase 0x006E0000 + RVA 0x196C)
[barrier] awaiting injector entrypoint rendezvous=006E196C; NtProtectVirtualMemory=...; .text=[006E1000,01676000)
[barrier] entrypoint rendezvous hit at 006E196C; DR0 -> NtProtectVirtualMemory
[barrier] exact .text -> PAGE_EXECUTE_READ: base=006E1000 size=00F944CD; parking unpacker
[boot] VFS scanner: signature match @007DD0E0 (RVA 000FD0E0)
[hookmgr] installed 'Vfs_LoadResource' @007DD0E0 via MinHook, trampoline=...
[barrier] bulk unpack complete; scanner resolved=1, hooks installed=1
```

The game process remained responsive after the parked-thread handoff and hook install.
