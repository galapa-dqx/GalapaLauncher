# Universal post-unpack barrier

Status: **implemented and verified end to end on DQX 8.0 (2026-07-18).**

## Result

Talon no longer waits for the first `Vfs_LoadResource` call and does not need the
true game OEP. The reliable barrier is the packer's final page-protection request:

1. `Talon.Injector` statically decodes the packed image's `KONN` metadata.
2. It arms a hardware execute breakpoint at the decoded second-stage entry while the
   primary thread is still suspended.
3. Talon.Boot's VEH catches that breakpoint and retargets DR0 to
   `ntdll!NtProtectVirtualMemory`.
4. An executable request whose page-rounded range covers `.text` becomes a candidate.
5. The VEH parks the unpacker and a worker scans the image. A zero/ambiguous match
   rejects the candidate; the syscall resumes with DR0 rearmed for later candidates.
6. A unique VFS match confirms completion. The worker registers and installs all hooks,
   then releases the unpacker with every hook already active.

This is scanner-driven, closes the lost-first-call gap, patches no system DLL bytes,
and does not use the hardware data breakpoints rejected by this packer.

## The packer identification correction

The entry wrapper resembles a familiar ASPack `pushad`/computed-return shape, but the
actual loader is not the textbook ASPack layout assumed by the earlier investigation.
The packed executable's PE entrypoint is RVA `0x196C`, which calls a first-stage loader
at RVA `0x1000`. That loader decodes a private 32-byte record whose magic is `KONN`,
loads a second stage at RVA `0x02001000`, and transfers to its entry at RVA
`0x02023A00`.

Consequently, ASPack-specific static offsets and the classic ESP data-breakpoint trick
were the wrong abstraction. The useful stable interface is the self-describing KONN
record plus the native page-protection transition.

Background material that informed, but does not identify, the target:

- https://unprotect.it/technique/aspack/
- https://medium.com/@mjak/unpack-as-s-pack-890238c387dd
- https://gist.github.com/abhisek/3659931
- https://github.com/orcastor/unpack
- https://github.com/SabreTools/BinaryObjectScanner/issues/74

## KONN record decoding

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

The injector does not hardcode file offset `0x1000` or the stage RVA. It scans aligned
32-byte candidates and accepts one only when the magic, PE entrypoint, stage ordering,
and `SizeOfImage` bounds all validate. Failure is fail-closed: no breakpoint is armed.

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

The candidate predicate therefore uses:

```text
pageBegin = requestedBase & ~0xFFF
pageEnd   = (requestedBase + requestedSize + 0xFFF) & ~0xFFF
candidate = executable(NewProtect)
         && pageBegin <= textPageBegin
         && pageEnd   >= textPageEnd
```

Comparing unrounded values was the key bug in the abandoned strategy.

## Threading and exception details

The injector arms DR0 before `ResumeThread`, because an in-process worker is too late
for the second-stage entry. Talon.Boot installs its VEH synchronously from the early
DLL initialization path before spawning the worker.

At every `NtProtectVirtualMemory` execute trap, the VEH sets x86 EFLAGS.RF before
continuing. Clearing DR6 alone is insufficient and caused the diagnostic probe's
repeating `Single Step` exception. For each candidate, the VEH temporarily clears DR0 and waits for the worker. A rejected
candidate resumes with DR0 rearmed; a confirmed candidate remains disarmed while the
worker installs. MinHook never runs inside the VEH.

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

Use the new injector switch:

```powershell
$token = (& dotnet Galapa.Toolbox\bin\Debug\net8.0-windows10.0.19041.0\Galapa.Toolbox.dll token).Trim()
Talon.Injector\bin\Debug\net8.0-windows\Talon.Injector.exe `
  --boot-dll D:\Code\DQXLauncher\Talon.Boot\bin\x86\Debug\Talon.Boot.dll `
  --override-dir D:\path\to\override `
  --unpack-barrier -- `
  "D:\Program Files (x86)\SquareEnix\DRAGON QUEST X\Game\DQXGame.exe" `
  "-StartupToken=$token" -USE_APARTMENTTHREADED
```

`--arm-exec-bp` remains available as a low-level diagnostic option. Production launches
should use `--unpack-barrier`, which derives and validates the RVA automatically and
sets `TALON_UNPACK_STAGE_RVA` for Talon.Boot.

## Verification evidence

The end-to-end run produced:

```text
[talon] KONN metadata @ file+0x1000: stage2 RVA 0x2023A00
[barrier] KONN stage2=025C3A00 -> NtProtectVirtualMemory=...; .text=[005A1000,01536000)
[boot] VFS scanner: signature match @0069D0E0 (RVA 000FD0E0)
[hookmgr] installed 'Vfs_LoadResource' @0069D0E0 via MinHook, trampoline=...
[barrier] CONFIRMED via page-rounded .text executable transition; scanner resolved=1, hooks installed=1
```

The game process remained responsive after the parked-thread handoff and hook install.
