# Talon.Boot

Talon.Boot is the small native x86 DLL loaded into the 32-bit DQX process. It
starts Talon's co-located managed runtime and holds the game at the unpack
boundary until managed hooks are ready. VFS and network hooks live in C#.

See [Talon architecture](../TALON.md) for the complete runtime design.

## Boot sequence

1. `Talon.Injector` creates DQX suspended. It queues an early APC that loads
   `Talon.Boot.dll` and calls the exported `TalonInitialize` function with the
   remote JSON start-info pointer.
2. Boot installs its vectored exception handler and starts a worker thread. The
   Injector resumes DQX with DR0 armed at the packed entry point.
3. At the entry point, Boot moves DR0 to `NtProtectVirtualMemory`. It waits for
   the exact page-rounded `.text -> PAGE_EXECUTE_READ` transition that completes
   the bulk unpack.
4. Boot parks the unpacking thread and calls `Talon.EntryPoint.Initialize` on the
   worker. Managed code scans and installs the game hooks.
5. Managed initialization signals the continue event. Boot releases the
   unpacking thread. The wait fails open after 30 seconds so a Talon failure does
   not leave DQX suspended.

The barrier is based on PE metadata and the protection transition, not on a game
function signature. Game hooks can change after a DQX update without changing
the unpack trigger. See [OEP barrier](docs/oep-barrier.md) for its invariants.

## Hosting CoreCLR

`coreclr_host.cpp` uses the standard .NET native-hosting sequence:

1. Load the `hostfxr.dll` distributed beside Boot. Use `nethost.dll` only as a
   fallback to locate a compatible host.
2. Initialize the runtime from `Talon.runtimeconfig.json`.
3. Request the `load_assembly_and_get_function_pointer` delegate.
4. Load `Talon.dll` and resolve `Talon.EntryPoint.Initialize`.

Talon ships a self-contained `win-x86` runtime. The game machine does not need a
separate .NET installation.

## Bootstrap contract

The Injector supplies one versioned JSON object in its existing remote bootstrap
allocation. Boot forwards its pointer directly to managed code. There is no
Talon-specific environment-variable configuration and no native/managed hook
function table.

## Source layout

| File | Responsibility |
| --- | --- |
| `dllmain.cpp` | Export handling, worker startup, and fail-open coordination |
| `coreclr_host.*` | CoreCLR discovery and managed entry-point resolution |
| `unpack_trigger.*` | DR0/VEH unpack barrier and parked-thread handoff |
| `log.*` | `%TEMP%\talon-boot.log` and `OutputDebugString` diagnostics |

## Build

```powershell
msbuild Talon.Boot\Talon.Boot.vcxproj /p:Configuration=Release /p:Platform=Win32
```

Build `Talon.Injector` afterward to assemble Boot, Talon, and the self-contained
x86 runtime in one output directory.
