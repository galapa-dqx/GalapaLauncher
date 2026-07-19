# Talon.Boot

A native x86 payload DLL injected into the 32-bit Dragon Quest X game process. Its
job is to serve **loose files from an override directory in place of the assets DQX
would otherwise decompress out of its packed `.dat` archives** — the foundation for
asset mods, texture swaps, and data overrides.

It does this by inline-hooking the game's *own* resource loader, `Vfs_LoadResource`,
so the game keeps doing its own archive I/O and we only intercept the
`(path → resource)` call.

## Why hook the VFS layer

The predecessor tool, **dragonhook**, hooked the file-I/O layer (`CreateFile` /
`ReadFile`) and had to re-encode each loose file back into the game's on-disk block
format — IDX relocation plus type-2 deflate — so the game's decoder would accept it.

Talon hooks one layer higher, at the *semantic* loader:

| | dragonhook (file I/O) | Talon.Boot (VFS) |
|---|---|---|
| What's intercepted | `CreateFile`/`ReadFile` | `Vfs_LoadResource(path, …)` |
| Loose-file handling | re-encode into `.dat` block format (IDX + deflate) | read raw bytes, hand straight to the game |
| Needs `.dat` format knowledge | yes | no |
| Needs a `--data-dir` copy of the archives | yes | no — the game reads its own `Content\Data` |
| Binds to | stable OS APIs | a game-internal address |
| Version-specific | no | **yes** (re-anchor on patch day) |

The single trade-off is that hooking a game-internal function binds us to an address
that moves when the game is patched. That's an explicit, documented cost (see
[Re-anchoring after a game patch](#re-anchoring-after-a-game-patch)).

## The full boot sequence

```
Talon.Injector (C#)            Talon.Boot (this DLL, native x86)
─────────────────              ─────────────────────────────────
CreateProcess(SUSPENDED)
QueueUserAPC(LoadLibraryW) ──▶  DllMain(DLL_PROCESS_ATTACH)
ResumeThread                      └─ talon_boot()
                                       ├─ read TALON_OVERRIDE_DIR
                                       └─ start_unpack_watcher()  ── spawns watcher thread
                                                                       │
game unpacks its own .text ◀───────────────────────────────────────── arms HW execute-BP
                                                                       │  at Vfs_LoadResource
game first CALLS Vfs_LoadResource ─▶ #DB → VEH signals the watcher
                                       └─ watcher installs the hook via
                                          MinHook (normal context); every
                                          call after this one is served
```

### 1. Injection (Talon.Injector)

The game is launched **suspended**, an APC that calls `LoadLibraryW(<boot dll path>)`
is queued onto its primary thread, and the thread is resumed. The APC drains during
the loader's early alertable wait, so **Talon.Boot is mapped before the game's own
entry point runs** ("early-bird" APC injection). Both the injector and this DLL must
be x86 to inject the 32-bit game. See `Talon.Injector/Injector.cs`.

### 2. Boot orchestration (`dllmain.cpp`)

`DllMain` (or the exported `TalonInit()` hand-off entrypoint) calls the idempotent
`talon_boot()`, which:

- opens the log (`%TEMP%\talon-boot.log`, mirrored to `OutputDebugString`),
- reads **`TALON_OVERRIDE_DIR`** — the directory of loose files to serve. If unset,
  Talon is a no-op for that launch,
- optionally enables the **VFS census** (`TALON_VFS_CENSUS`, see below),
- spawns the unpack watcher thread and returns immediately (no real work under the
  loader lock).

### 3. Waiting for the unpack (`unpack_trigger.cpp`)

DQX ships with a **packed `.text`**: at inject time the region holding
`Vfs_LoadResource` is committed but zero-filled, and the packer decompresses it in
blocks (non-linear writes), so we can't just watch one byte and know the function is
whole.

Instead the watcher arms a **hardware execute breakpoint** (`DR0`, 1-byte execute,
`DR7 = 0x1`) at the function's address on every thread. It sits harmlessly on the
zero-filled address until the game unpacks the function and *calls* it — and **a call
can't happen until the function is fully unpacked**. The resulting `#DB` is caught by
a Vectored Exception Handler, which:

1. clears `DR0`/`DR6`/`DR7` on that thread so it doesn't re-trap,
2. **signals the watcher thread** and resumes with `EIP` unchanged.

The VEH does *not* install the hook itself: the MinHook engine suspends threads and
takes locks while patching, which is unsafe from inside an exception handler. So the
watcher does the install in normal context (see step 4). The one consequence is that
*this* first triggering call runs the original function unhooked; every call after it
is served. (The planned OEP barrier will install before any game code runs, closing
even that one-call gap.)

This is a poll-free, exact signal. It works because DQX does not scan the debug
registers (verified 2026-07-17).

> **No fallback by design.** There is intentionally no polling or signature-scan
> fallback. If the trigger never fires (a patch moved the function or cleared the
> debug registers), Talon no-ops and logs a re-anchor notice. Per-patch testing
> before shipping the injector is expected to catch that.

### 4. Installing the hook (MinHook, `vendor/minhook`)

The hooking engine is **[MinHook](https://github.com/TsudaKageyu/minhook)** (vendored,
BSD-2). `install_vfs_hook` calls `MH_Initialize` → `MH_CreateHook(target, detour,
&original)` → `MH_EnableHook`. MinHook patches the first bytes of the target with an
`E9` `jmp` to our detour and builds a **trampoline** that runs the stolen original
bytes and jumps back — so the original `Vfs_LoadResource` remains callable for
pass-through. Critically, MinHook patches with **all other threads suspended** and
fixes up any thread whose instruction pointer lands inside the patched bytes.

That thread-suspension is exactly why the install runs on the **watcher thread**, not
in the VEH (step 3): suspending arbitrary game threads while handling an exception on
one of them risks a lock-order deadlock. MinHook owns the trampoline construction,
atomic patching, and thread safety, so Talon no longer hand-rolls any of it.

> The earlier hand-rolled inline hooker (`inline_hook.cpp` + the `disasm.cpp` length
> decoder) is superseded by MinHook and slated for removal.

### 5. Serving overrides (`vfs_hook.cpp`)

The hook (`hook_VfsLoadResource`) is the heart of Talon. For each requested resource
path it:

1. maps the archive-relative VFS `path` onto the override dir
   (`<TALON_OVERRIDE_DIR>\<path>`, `/` → `\`),
2. if a loose file exists there, allocates a buffer **with the game's own allocator**,
   reads the file's raw bytes into it, and calls the game's `construct` callback to
   build a resource from that buffer,
3. returns that resource — the game is none the wiser,
4. otherwise (no override file, or any step unavailable) tails into the original
   loader via the trampoline.

#### The reversed ABI (DQX 8.0)

`Vfs_LoadResource` is `__thiscall(this /*ecx*/, char* path, int expansion, int mount,
int mustBeZero)`, callee-cleans its 4 stack args. `path` is the archive-relative path
that gets hashed for the IDX lookup — exactly the layout a mod folder mirrors. Our
replacement is declared `__fastcall` to capture the incoming `this` in `ecx`; the
remaining args land on the stack identically, so the `0x10`-byte callee cleanup matches.

The VFS manager object (`this`) carries the `__cdecl` callbacks we reuse:

| Offset | Callback | Signature |
|---|---|---|
| `this+0x110` | `alloc` | `(tag=0, size, flag=1) -> buffer` |
| `this+0x114` | `free`  | `(tag=0, buffer)` |
| `this+0x11c` | `construct` | `(path, size, buffer, FILE*=0, offset=0) -> resource` |

**Buffer ownership** is the subtle part. The game's own multi-chunk path does
`buf = alloc(...); fill; res = construct(path, size, buf, 0, 0); free(staging)` and
**never frees `buf`** — the constructed resource takes ownership of it. So serving an
override is `alloc → read file into buf → construct → return`, with **no `free`**
(freeing `buf` here would be a use-after-free). We allocate with the game's allocator
precisely so the resource can later free it through the game's matching free path.

## Configuration (environment variables)

| Variable | Effect |
|---|---|
| `TALON_OVERRIDE_DIR` | Directory of loose override files. **Unset ⇒ Talon is a no-op.** |
| `TALON_VFS_CENSUS` | If set (any value), logs the path of every resource the game requests, capped at 400 entries — useful for discovering the archive-relative paths a mod would mirror. |

## Source layout

| File | Responsibility |
|---|---|
| `dllmain.cpp` | Boot orchestration and entrypoints (`DllMain`, `TalonInit`). |
| `unpack_trigger.*` | Execute-BP watcher that fires on the first `Vfs_LoadResource` call and installs the hook. |
| `vfs_hook.*` | The override hook, target location/signature, and MinHook install. |
| `vendor/minhook/` | Vendored MinHook (BSD-2) — the hooking engine. |
| `log.*` | Diagnostics to `%TEMP%\talon-boot.log` + `OutputDebugString`. |
| `inline_hook.*`, `disasm.*` | Superseded hand-rolled inline hooker + length decoder (pending removal). |

## Re-anchoring after a game patch

The hook binds to `Vfs_LoadResource` by RVA and confirms it with a prologue signature:

- **RVA** `0xFD0E0` — derived from the Binary Ninja DB address `0x5AD0E0` at image base
  `0x4B0000` (`0x5AD0E0 − 0x4B0000`). Runtime VA = `<exe load base> + RVA`.
- **Prologue signature** — `53 8B DC 83 ?? ?? 83 ?? ?? 83 ?? ?? 55 8B ?? ?? 89 ?? ?? ?? 8B EC B8 ?? ?? ?? ?? E8`
  (`push ebx; mov ebx,esp; sub/and/add esp,imm8 ×3; push ebp; …; call __chkstk`).

If a patch moves the function, the signature stops matching and the trigger won't fire
(logged as a re-anchor notice). To fix: re-find `Vfs_LoadResource` in Binary Ninja via
its error strings (e.g. `"ERROR: readerror0 %x"`), then update `VFS_LOADRESOURCE_RVA`
and `VFS_SIG` in `vfs_hook.cpp`.

## Diagnostics

All activity is logged to **`%TEMP%\talon-boot.log`** (absolute path, independent of
the game's working directory) and mirrored to `OutputDebugString`. Logging is a
compile-time toggle: `LOG_ENABLED` in `log.h`; when undefined, `dbg()` collapses to a
no-op that doesn't even evaluate its arguments. Key lines to look for:

- `[boot] override dir = …` — configuration read.
- `[boot] watcher: armed execute-BP trigger … waiting` — breakpoint set.
- `[boot] hooked Vfs_LoadResource via MinHook @… trampoline=…` — MinHook install succeeded.
- `[boot] watcher: execute-BP trigger FIRED; hook installed in watcher thread` — success.
- `[vfs] OVERRIDE <path> (<n> bytes) -> res=…` — an override was served.
- `*** EXECUTE-BP TRIGGER DID NOT FIRE …` — re-anchor needed after a patch.
