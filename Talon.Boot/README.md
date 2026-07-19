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

```text
Injector (suspended process)                   Talon.Boot
-----------------------------------------      --------------------------------
decode validated KONN stage metadata
arm DR0 at decoded stage-two entry
queue early LoadLibrary APC                ->  install VEH synchronously
resume primary thread                          start normal worker

stage-two entry executes                   ->  #DB: retarget DR0 to NtProtect
packer requests executable .text           ->  #DB: page-rounded candidate covers .text
                                                park packer thread
                                                scan executable PE sections
                              false candidate <-  resume + rearm DR0
                              confirmed      ->  register unique VFS signature
                                                install all hooks with MinHook
                                            <-  release packer; startup continues
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
- installs the barrier VEH synchronously (so the injector-armed DR0 cannot race it),
- spawns the normal-context scanner/install worker and returns.

### 3. Waiting for the unpack (`unpack_trigger.cpp`)

DQX ships with a packed `.text`; target functions are zero-filled at injection time.
The injector decodes the file's self-describing `KONN` record and arms DR0 at the
second-stage entry before resuming the primary thread. Talon.Boot's synchronously
installed VEH catches that execute breakpoint and retargets DR0 to
`ntdll!NtProtectVirtualMemory`.

A Windows-page-rounded executable request covering `.text` is a barrier candidate. The
VEH temporarily disarms DR0, sets x86 EFLAGS.RF, and parks the unpacker while the
worker scans. If no unique VFS signature exists yet, the worker rejects the candidate
and the VEH resumes with DR0 rearmed. On confirmation, the worker installs in normal
context before releasing the unpacker. No VFS call is lost.

If `TALON_UNPACK_STAGE_RVA` is missing or invalid, Boot fails closed for hooking and logs
that the launch must use `Talon.Injector --unpack-barrier`.

### 4. Installing the hook (MinHook, `vendor/minhook`)

The hooking engine is **[MinHook](https://github.com/TsudaKageyu/minhook)** (vendored,
BSD-2). `hook_install_all` calls `MH_Initialize` → `MH_CreateHook(target, detour,
&original)` → `MH_EnableHook`. MinHook patches the first bytes of the target with an
`E9` `jmp` to our detour and builds a **trampoline** that runs the stolen original
bytes and jumps back — so the original `Vfs_LoadResource` remains callable for
pass-through. Critically, MinHook patches with **all other threads suspended** and
fixes up any thread whose instruction pointer lands inside the patched bytes.

That thread-suspension is exactly why the install runs on the **watcher thread**, not
in the VEH (step 3): suspending arbitrary game threads while handling an exception on
one of them risks a lock-order deadlock. MinHook owns the trampoline construction,
atomic patching, and thread safety, so Talon no longer hand-rolls any of it.


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
| `TALON_UNPACK_STAGE_RVA` | Internal: validated KONN stage RVA set by `Talon.Injector --unpack-barrier`. |
| `TALON_VFS_CENSUS` | If set (any value), logs the path of every resource the game requests, capped at 400 entries — useful for discovering the archive-relative paths a mod would mirror. |

## Source layout

| File | Responsibility |
|---|---|
| `dllmain.cpp` | Boot orchestration and entrypoints (`DllMain`, `TalonInit`). |
| `unpack_trigger.*` | KONN stage-two / `NtProtectVirtualMemory` barrier and parked-thread handoff. |
| `vfs_hook.*` | Override hook plus post-unpack executable-section signature scanner. |
| `vendor/minhook/` | Vendored MinHook (BSD-2) — the hooking engine. |
| `log.*` | Diagnostics to `%TEMP%\talon-boot.log` + `OutputDebugString`. |

## Re-anchoring after a game patch

Runtime binding is entirely scanner-driven. The signature wildcards stack sizes,
relocations, and call displacements while retaining the security-cookie sequence and
VFS-specific object member loads.

The scanner requires exactly one match across committed regions of executable PE
sections. Zero or multiple matches fail safely and are logged. On a patch, re-find
`Vfs_LoadResource` in Binary Ninja via strings such as `"ERROR: readerror0 %x"`, then
update `VFS_SIG` in `vfs_hook.cpp`.

The unpack anchor normally needs no re-anchoring: `--unpack-barrier` scans and validates
the packed file's KONN metadata against the PE entrypoint and `SizeOfImage` each launch.

## Diagnostics

All activity is logged to **`%TEMP%\talon-boot.log`** (absolute path, independent of
the game's working directory) and mirrored to `OutputDebugString`. Logging is a
compile-time toggle: `LOG_ENABLED` in `log.h`; when undefined, `dbg()` collapses to a
no-op that doesn't even evaluate its arguments. Key lines to look for:

- `[boot] override dir = …` — configuration read.
- `[barrier] KONN stage2=... -> NtProtectVirtualMemory=...` — universal barrier armed.
- `[boot] VFS scanner: signature match ...` — unique runtime VFS target found.
- `[hookmgr] installed 'Vfs_LoadResource' ...` — MinHook install succeeded.
- `[barrier] executable .text candidate rejected ...` — an early transition was skipped.
- `[barrier] CONFIRMED ... scanner resolved=1, hooks installed=1` — full barrier success.
- `[vfs] OVERRIDE ...` — an override was served.
