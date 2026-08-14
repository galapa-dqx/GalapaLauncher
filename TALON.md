# Talon architecture

Talon runs post-unpack game integration in managed C#. Native `Talon.Boot` is
limited to loading the co-located CoreCLR runtime and maintaining the existing
hardware-breakpoint unpack barrier.

## Runtime flow

1. `Talon.Injector` creates DQX suspended and allocates one remote bootstrap
   region. It contains the Boot path, versioned JSON start info, export name,
   and a small RX x86 APC thunk.
2. The thunk installs a minimal fallback VEH, calls `LoadLibraryW`, resolves
   `TalonInitialize`, and passes the JSON pointer. Boot's full barrier takes
   priority, and the APC removes the fallback after successful initialization.
   If bootstrap fails, the fallback clears Talon's entrypoint breakpoint so DQX
   can continue. Talon does not use environment variables for configuration.
3. Boot loads the Injector-owned self-contained x86 runtime beside
   `Talon.Boot.dll`, arms the
   entrypoint/`NtProtectVirtualMemory` barrier, and waits for the final
   `.text -> PAGE_EXECUTE_READ` transition.
4. After unpacking, Boot calls `Talon.EntryPoint.Initialize`. Managed code runs
   one batch scan for the built-in signatures and prepares the game hooks. Core
   startup commits as one transaction before its 30-second deadline. A timeout
   cancels preparation and rolls back its hooks before Boot releases DQX. VFS
   and network are independent first-party subsystems: either can fail while the
   other remains active. Every failure is fail-open to the game itself; Talon
   reports the first failure, then DQX continues with the unaffected hooks or
   with no Talon hooks.

Managed development builds do not require a native payload:

```powershell
dotnet build Talon\Talon.csproj -c Release
```

The Injector output directory is the runnable game distribution. Build Boot
first so the Injector can copy it beside Talon and the x86 runtime:

```powershell
msbuild Talon.Boot\Talon.Boot.vcxproj /p:Configuration=Release /p:Platform=Win32
dotnet build Talon.Injector\Talon.Injector.csproj -c Release
```

## Managed interop

`Talon.Interop.ISigScanner`, `Talon.Hooking.IGameInteropProvider`, and
`Talon.Hooking.Hook<T>` follow the familiar Dalamud service shape. Automatic
hooks use Reloaded.Hooks, Talon's managed patching backend. All DQX delegates
declare their x86 calling convention.

Built-in hooks submit named `SignatureQuery` values to `ScanTextBatch`, which
walks `.text` once and returns every raw candidate for structural validation.
Attributed members are also collected into a batch before initialization.
Individual scan methods remain available for dynamic discovery and unusual
plugin needs, but the batch path is the preferred extension surface.

Managed detours execute directly on native game threads. The current low-level
hook API does not synthesize an exception guard for arbitrary delegate shapes;
detour implementations must catch every managed exception and call their
original function when optional work fails. Talon's first-party detours enforce
that boundary explicitly. A future plugin layer should expose a guarded hook
surface before third-party code can register detours.

The exact Dalamud-derived API areas and source revision are recorded in
[third-party notices](THIRD_PARTY_NOTICES.md).

The VFS hook uses the game's allocator and constructor callbacks. A successfully
constructed resource owns its buffer. Talon opens a loose file once, resolves
the final handle path, verifies it remains below the final override root, and
reads from that same handle. This rejects junction and symbolic-link escapes as
well as lexical traversal, alternate data streams, and reserved DOS device
names before Win32 opens them. Containment comparisons are ordinal so a
case-sensitive directory cannot redirect `root` to a distinct `ROOT` sibling.
Overrides are keyed by logical VFS path rather than
expansion/mount, so one loose translation replaces that path in every archive
layer.

## Network interception

VCE is DQX's bundled network library. A VCE session is its native
per-connection object. The parser hook always calls `Vce_iSession_ParseFrame`
and records the current VCE frame type in thread-local state. After Talon
observes a verified session vtable, it hooks the `ProcessPayload` slot (`+0x5c`)
and destructor. Only normal type-0 payloads can be held. Control frames continue
unchanged.

Managed interceptors register a `PacketSelector` with an opcode and optional
marker plus byte offset. Marker-specific registrations take precedence over opcode-only
registrations. Duplicate selectors are rejected. A matching registration copies
and holds the packet in a managed `HeldInboundPacket` lease while its asynchronous
handler runs. The handler explicitly reinjects original or replacement bytes.
Returning, failing, or reaching the deadline without doing so reinjects the
original bytes. Passive observers do not hold traffic.

Completed translations enter a completion queue. The VCE zero-timeout select
poller drains at most 32 packets or one millisecond per call, in completion order
instead of arrival order. There is no head-of-line wait: a later translation can
be reinjected before an earlier one. Hold only opcodes whose processing tolerates
that reordering. Stateful protocol work will need an ordered lane rather than the
default completion queue. Limits are 256 held packets, 8 MiB total, and 60 seconds
per pending lease. They bound Talon's managed packet copies; traffic that cannot
be held passes through synchronously. At the deadline Talon reinjects the original,
clears the lease payload, and releases capacity when the replay queue drains. A
non-cooperative extension task can continue running, but it no longer retains
Talon's admission reservation.

Every held packet is reinjected with translated or original bytes while its
connection remains valid. Handler failure and timeout replay the original. VCE
session destruction invalidates its generation and waits for any active replay.
A later completion for the destroyed connection is recorded as
`ConnectionClosed`; calling its native payload method would use freed memory.
Pointer reuse cannot send that stale packet to a new connection.

DQX 8.0 uses one continuous encrypted TCP session and one observed dynamic VCE
vtable. Talon hooks that vtable. If a later connection uses a different vtable,
Talon logs one warning and lets that connection pass through; a future
per-vtable registry can extend interception without guessing object liveness.

For the current DQX payload contract, the opcode is byte zero. An optional marker
matches its little-endian 16-bit representation at a fixed byte offset, which
defaults to byte one. This keeps selection policy in managed handlers while packet
capture preserves each payload accepted by its bounded writer for further
protocol work. Capture records can be dropped if disk I/O falls behind the
1,024-record diagnostics queue; packet handling itself is unaffected.

## Diagnostics

- `--override-dir <dir>` enables loose VFS replacements.
- `--vfs-census` adds observed paths to the TexTools-compatible catalog at
  `%LocalAppData%\Galapa\DQX\dat_db.db`. Talon keeps per-session request counts,
  first/last observation times, expansion/mount pairs, and original, override,
  fallback, miss, or error outcomes in `talon_sessions` and
  `talon_vfs_observations`; `talon_vfs_totals` provides a cumulative view.
  Batched SQLite writes run outside game VFS threads and accumulate across
  launches.
- `--packet-capture <path>` writes passive PCAPNG with `LINKTYPE_USER0` (147).
- `--network-smoke-test` registers a one-shot selector for opcode `0x47`, marker
  `0x3CA8`. The first match per connection generation is held for 250 ms and
  replayed unchanged.

PCAPNG capture contains raw decrypted game payloads. It can include account,
character, chat, and activity data. Treat capture files as sensitive and redact
or encrypt them before sharing.

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

Event values are `1` observed, `2` held, `3` reinjected, and `4` connection
closed before replay.

## Distribution

Keep the Injector-owned x86 runtime self-contained and co-located inside the
Velopack package. `Talon.dll` is a managed component loaded into that one runtime,
so runtime files are never flattened from two independent publishes. The
launcher's x64 runtime does not satisfy CoreCLR injection into the 32-bit game,
and a machine-wide runtime would make Proton hosting less deterministic.
Velopack can manage launcher prerequisites independently; its
[Windows framework bootstrap](https://docs.velopack.io/packaging/bootstrapping)
does not replace Talon's architecture-specific payload.
