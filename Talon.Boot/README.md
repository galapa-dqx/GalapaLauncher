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
if an override is configured:
  read mapped PE entrypoint
  arm DR0 at packed entrypoint
queue early LoadLibrary APC                ->  install VEH synchronously
resume primary thread                          launch scan/install worker
packed entrypoint executes                 ->  #DB: retarget DR0 to NtProtect
packer requests exact .text -> RX          ->  #DB: bulk-unpack boundary
                                                park packer thread
                                                scan executable PE sections
                                                register unique VFS signature
                                                install all hooks with MinHook
                                            <-  release packer; startup continues
```

### 1. Injection (Talon.Injector)

The game is launched **suspended**, an APC that calls `LoadLibraryW(<boot dll path>)`
is queued onto its primary thread, and the thread is resumed. The APC drains during
the loader's early alertable wait, so **Talon.Boot is mapped before the game's own
entry point runs** ("early-bird" APC injection). Both the injector and this DLL must
be x86 to inject the 32-bit game. When `TALON_OVERRIDE_DIR` is set, the Injector reads
the mapped PE's validated `AddressOfEntryPoint` and arms DR0 there as a generic post-APC
rendezvous. With no override configured, it deliberately leaves DR0 untouched because
Boot installs no VEH on that documented no-op path. See `Talon.Injector/Injector.cs`.

### 2. Boot orchestration (`dllmain.cpp`)

`DllMain` (or the exported `TalonInit()` hand-off entrypoint) calls the idempotent
`talon_boot()`, which:

- opens the log (`%TEMP%\talon-boot.log`, mirrored to `OutputDebugString`),
- reads **`TALON_OVERRIDE_DIR`** — the directory of loose files to serve. If unset,
  Talon is a no-op for that launch,
- optionally enables the **VFS census** (`TALON_VFS_CENSUS`, see below),
- installs the barrier VEH synchronously,
- launches the normal-context scan/install worker.

### 3. Waiting for the unpack (`unpack_trigger.cpp`)

DQX ships with a packed `.text`; target functions are zero-filled at injection time.
When the injection APC returns, Windows restores the Injector-armed entrypoint DR0.
Boot's VEH catches that breakpoint before the first packed instruction executes and
retargets DR0 to `ntdll!NtProtectVirtualMemory`.

The completion predicate is the packer's single bulk transition: `PAGE_EXECUTE_READ`
over exactly the page-rounded `.text` range. The VEH clears DR0, sets x86 EFLAGS.RF,
and parks the unpacker before that call executes. The worker then scans and installs
hooks in normal context and releases the unpacker. Scanner success does not define the
barrier: a missing or ambiguous signature means the unpack completed but this build is
unsupported. No VFS call is lost.

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

1. canonicalizes the archive-relative VFS `path` under the override dir, rejects rooted,
   drive-qualified, or `..`-escaping paths, and maps accepted paths to
   `<TALON_OVERRIDE_DIR>\<path>` (`/` → `\`),
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
| `unpack_trigger.*` | Boot-owned `NtProtectVirtualMemory` bulk-unpack barrier and parked-thread handoff. |
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

The unpack anchor normally needs no re-anchoring: Boot derives `.text` from the mapped
PE headers and observes the exact page-rounded transition to `PAGE_EXECUTE_READ`.

## Diagnostics

All activity is logged to **`%TEMP%\talon-boot.log`** (absolute path, independent of
the game's working directory) and mirrored to `OutputDebugString`. Logging is a
compile-time toggle: `LOG_ENABLED` in `log.h`; when undefined, `dbg()` collapses to a
no-op that doesn't even evaluate its arguments. Key lines to look for:

- `[boot] override dir = …` — configuration read.
- `[barrier] entrypoint rendezvous hit ...` — Boot retargeted Injector's DR0 to `NtProtect`.
- `[barrier] exact .text -> PAGE_EXECUTE_READ ...` — bulk-unpack boundary reached.
- `[boot] VFS scanner: signature match ...` — unique runtime VFS target found.
- `[hookmgr] installed 'Vfs_LoadResource' ...` — MinHook install succeeded.
- `[barrier] bulk unpack complete; scanner resolved=1, hooks installed=1` — full success.
- `[vfs] OVERRIDE ...` — an override was served.
