# Talon architecture

Talon runs post-unpack game integration in managed C#. Native `Talon.Boot` is
limited to loading the co-located CoreCLR runtime and maintaining the existing
hardware-breakpoint unpack barrier.

## Runtime flow

1. `Talon.Injector` creates DQX suspended and allocates one remote bootstrap
   region. It contains the Boot path, versioned JSON start info, export name,
   and a small RX x86 APC thunk.
2. The thunk calls `LoadLibraryW`, resolves `TalonInitialize`, and passes the
   JSON pointer. Talon does not use environment variables for configuration.
3. Boot loads the self-contained x86 runtime beside `Talon.Boot.dll`, arms the
   entrypoint/`NtProtectVirtualMemory` barrier, and waits for the final
   `.text -> PAGE_EXECUTE_READ` transition.
4. After unpacking, Boot calls `Talon.EntryPoint.Initialize`. Managed code scans
   and installs the game hooks, then signals the native continue event. The
   unpacking thread has a 30-second fail-open timeout.

The Injector output directory is the runnable distribution. Build Boot first
so the Injector can copy it beside Talon and the x86 runtime:

```powershell
msbuild Talon.Boot\Talon.Boot.vcxproj /p:Configuration=Release /p:Platform=Win32
dotnet build Talon.Injector\Talon.Injector.csproj -c Release
```

## Managed interop

`Talon.Interop.ISigScanner`, `Talon.Hooking.IGameInteropProvider`, and
`Talon.Hooking.Hook<T>` follow the familiar Dalamud service shape. Automatic
hooks use Reloaded.Hooks. MinHook remains an explicit backend for targets that
require it. All DQX delegates declare their x86 calling convention.

The VFS hook uses the game's allocator and constructor callbacks. A successfully
constructed resource owns its buffer. Loose-file paths are canonicalized and
must remain below the configured override root.

## Network interception

The parser hook always calls `Vce_iSession_ParseFrame` and records the current
VCE frame type in thread-local state. After Talon observes a verified session
vtable, it hooks the `ProcessPayload` slot (`+0x5c`) and destructor. Only normal
type-0 payloads can be held. Control frames continue unchanged.

Managed interceptors register a `PacketSelector` with an opcode and optional
marker. Marker-specific registrations take precedence over opcode-only
registrations. Duplicate selectors are rejected. A matching registration copies
and holds the packet while its asynchronous handler runs. Passive observers do
not hold traffic.

Completed translations enter a completion queue. The VCE zero-timeout select
poller drains at most 32 packets or one millisecond per call, in completion order
instead of arrival order. There is no head-of-line wait: a later translation can
be reinjected before an earlier one. Limits are 256 held packets, 8 MiB total,
and 60 seconds per handler. Failures replay the original bytes. Session
destruction increments a generation and prevents replay of stale completions.

For the current DQX payload contract, the opcode is byte zero. An optional marker
matches its little-endian 16-bit representation anywhere after the opcode. This
keeps selection policy in managed handlers while packet capture preserves the
complete payload for further protocol work.

## Diagnostics

- `--override-dir <dir>` enables loose VFS replacements.
- `--vfs-census` logs the first 400 VFS paths.
- `--packet-capture <path>` writes passive PCAPNG with `LINKTYPE_USER0` (147).
- `--network-smoke-test` registers a one-shot selector for opcode `0x47`, marker
  `0x3CA8`. The first match per connection generation is held for 250 ms and
  replayed unchanged.

The 32-byte little-endian Talon pseudo-header in each PCAPNG enhanced packet is:

| Offset | Size | Field |
| ---: | ---: | --- |
| 0 | 4 | magic `TLN1` |
| 4 | 2 | version (`1`) |
| 6 | 2 | header size (`32`) |
| 8 | 8 | packet ID |
| 16 | 8 | connection generation |
| 24 | 1 | direction (`1` = inbound) |
| 25 | 1 | event |
| 26 | 1 | opcode |
| 27 | 1 | marker-present flag |
| 28 | 2 | marker |
| 30 | 2 | reserved |
