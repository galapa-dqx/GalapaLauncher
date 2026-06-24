# DQX patch download/apply — design

Design for Galapa's patch **acquisition + orchestration** layer: how we discover,
download, verify, and apply DQX `.patch` files. Modelled on XIVLauncher's
`PatchManager` + elevated `PatchInstaller` subprocess, adapted to DQX's
stateless-oracle patch API.

The ZiPatch reader/applier already exists ([`Galapa.Core/Patcher`](../Galapa.Core/Patcher),
oracle-validated). This document covers the layer *above* it. **Status: design only —
no code written yet.**

Companion research: `dqx-patch-flow.md` (the protocol, reverse-engineered) and the
ZiPatch notes. Live facts in §2 were re-verified against the real servers on
2026-06-21.

---

## 1. What XIVLauncher does (the model we're adapting)

Two stages, cleanly separated — worth mirroring:

1. **Get the patch list.** One request to `patch-*ver.ffxiv.com` with the on-disk
   version; the response body *is the full list*. `PatchListParser` → `PatchListEntry[]`.
2. **`PatchManager`** (runs in the **main, unelevated process**) drives two queues:
   - **Download queue** — up to `MAX_DOWNLOADS_AT_ONCE` (4) patches in parallel, per-slot
     progress + speed. Downloads land in a **user-writable patch cache**.
   - **Apply queue** — installs downloaded patches **strictly in order**; patch N+1
     downloads while patch N applies (pipelined).
   - Per-patch verify before apply (SHA-1 blocks; for boot, ZiPatch chunk CRCs).
   - **Apply happens in a separate elevated child process** (`XIVLauncher.PatchInstaller.exe
     rpc <name>`), because the game is in Program Files. The parent talks to it over an
     RPC channel: `StartInstall(file)` per patch, then `Finish`. The child
     (`RemotePatchInstaller`) applies the ZiPatch and writes the `.ver`/`.bck` files
     (both privileged ops), reporting `Hello`/`InstallOk`/`InstallFailed`/`Finish` back.
   - Elevation is requested **once** (`startInfo.Verb = "runas"`, only when
     `IsElevationRequiredForWrite(gamePath)`), so a single UAC prompt covers the session.

We keep this shape. We change *how the list is obtained* (DQX oracle), make acquisition
*token-aware*, **stream apply progress** (DQX patches are multi-GB), and modernize the
IPC transport.

---

## 2. How DQX differs (and what's verified)

| Aspect | FFXIV | DQX |
|---|---|---|
| Patch list | one request → whole list | **stateless oracle, one node at a time**: `POST …/patch/version?…&version=V` → the *single next* patch in `X-*` headers; walk `X-NextVersion` until absent |
| Metadata transport | tab-separated body | response **headers** (`X-NextVersion`, `X-FileURL`, `X-Filesize`, `X-Signature`) |
| Download URL | mostly stable | **time-limited signed token** `?dqxpatch=<unixts>_<md5>` — per file, refresh as needed |
| Big patches | n/a | split into `…patch`, `…patch_0001`, `_0002` (version increments, same base name) → **follow `X-FileURL` literally** |
| Verify | SHA-1 blocks / ZiPatch CRC | `X-Filesize` + `X-Signature` (algorithm TBD) + ZiPatch CRC |
| Repos | boot + ffxiv + ex1..5 | **boot + game** only (expansions live inside game data) |
| Apply target | `sqpack/{ffxiv,exN}/…` | boot → `<install>/Boot`; game → `<install>/Game/Content[/Data]` (see §8) |
| Privilege | elevated apply child + IPC | **same** — default install is `C:\Program Files (x86)\SQUARE ENIX\DRAGON QUEST X\`, so apply needs elevation (see §4) |

### Verified live (2026-06-21)

`POST http://game.dqx.jp/smgame/patch/version?platform=win32&repository=release&type=boot&version=1.6.0.0`
(empty body, `User-Agent: DQX PATCH CLIENT`) → `201` with `X-NextVersion: 7.6.338946.1`,
`X-FileURL: http://download.dqx.jp/patch/windows/Boot/1.6.0.0-7.6.338946.1.patch?dqxpatch=1782100996_…`,
`X-Filesize: 24095730`, `X-Signature: 8d4dbb665124553eb9dca84bfcf7f0cebfdede`.

**Resume works** on `download.dqx.jp` (AkamaiNetStorage): `GET` with `Range: bytes=0-11`
→ `206 Partial Content`, `Accept-Ranges: bytes`, `Content-Range: bytes 0-11/24095730`
(total == `X-Filesize`), body = ZiPatch magic. A second `GET` on the **same token** with
`Range: bytes=12000000-12000099` → `206`. Arbitrary-offset resume works; one token serves
multiple sequential ranged GETs within its TTL.

> ⚠️ **GET only, never HEAD.** `HEAD` against the signed URL → `403 Forbidden`
> (AkamaiGHost) and poisons the token for follow-up requests. Get the total from
> `X-Filesize` (or the first ranged GET's `Content-Range`). Treat a mid-download `403`
> as token-expiry → re-query the oracle for a fresh `X-FileURL`, resume `Range: bytes=<have>-`.

---

## 3. Strategy: enumerate-then-download

Because the oracle is stateless, cheaply walk the whole chain up front (≈46 tiny POSTs
for boot+game) to build a plan with an accurate total (progress bar) and enable parallel
downloads, then download + apply. **Enumeration URLs are for planning only; the actual
download token is re-fetched per file, just-in-time** (tokens go stale long before a
52 GiB chain finishes).

```
1. Read on-disk versions:  <install>/boot/Boot.ver, <install>/game/Game.ver   (fresh ⇒ 1.6.0.0)
2. ENUMERATE each repo (boot, then game): walk X-NextVersion → ordered PatchListEntry[]
3. PLAN: per-repo list with a known grand total (POST /patch/count for the headline number)
4. SPAWN the elevated apply child once (single UAC prompt) and handshake.
5. DOWNLOAD (parallel, user-cache) + APPLY (in order, via the child) — pipelined:
     - per file: re-POST /patch/version?version=<from> → fresh X-FileURL
     - download with Range/resume; on 403 → refresh token, resume from offset
     - verify (size + ZiPatch CRC [+ signature when decoded])
     - hand the cached file to the child → child applies into the (privileged) install dir
6. After a repo's chain applies, the child writes the new version into Boot.ver / Game.ver.
   Boot must reach current before Game.
```

---

## 4. Process & privilege model  ← the key revision

DQX installs to Program Files by default, so applying into the SqPack `Data` files needs
admin rights. We mirror XIVLauncher: **one long-lived elevated child process applies;
the unelevated UI process orchestrates + downloads + displays.** Download/network code
stays unelevated (smaller privileged surface, user-writable cache); only the apply +
`.ver` writes run elevated.

```
┌─────────────────────────────────────┐         ┌──────────────────────────────────────┐
│  Galapa.Launcher  (UI, unelevated)   │         │ Galapa.PatchInstaller.exe  (ELEVATED) │
│                                      │  named  │  "serve --pipe … --token … --game …"  │
│  DqxPatchClient   (enumerate/oracle) │  pipe   │                                       │
│  PatchManager     (download queue,   │ ◄─────► │  PatchApplyService (loop):            │
│                    user cache)       │  JSON   │   StartInstall → ZiPatchInstaller     │
│  PatchInstallerHost (spawn+drive ▲)  │  msgs   │     .InstallPatch(file, target(repo)) │
│  UI: progress for DL + apply         │         │     stream ApplyProgress per chunk    │
└─────────────────────────────────────┘         │   FinalizeRepo → write Boot/Game.ver  │
                                                 └──────────────────────────────────────┘
   downloads → %LOCALAPPDATA%\GalapaLauncher\patches\<repo>\<from>-<to>.patch
   child reads that cache (admin can read user files) and applies into <install>\…
```

**Spawn + elevate (once per session).** `PatchInstallerHost.StartIfNeeded`:
`ProcessStartInfo("Galapa.PatchInstaller.exe")` with `UseShellExecute = true`,
`CreateNoWindow`/`Hidden`, args `serve --pipe <name> --token <secret> --parent-pid <pid>
--game <install>`. Add `Verb = "runas"` **only when** `IsElevationRequiredForWrite(install)`
returns true (probe by trying to open a temp file for write in the install dir). One UAC
prompt covers boot + game. Under Proton/Wine the prefix is user-writable, so no `runas`
(UAC is a no-op there) — same code path, just no elevation.

**Transport: named pipes** (`NamedPipeServerStream`/`NamedPipeClientStream`), a single
duplex pipe carrying length-prefixed `System.Text.Json` envelopes, one reader loop each
side. Chosen over XIVLauncher's `SharedMemory` RPC: built into the BCL (no package), works
cleanly across the UAC integrity boundary, and avoids `BinaryFormatter`-style
serialization. (SharedMemory remains a fallback if pipe perf ever matters — it won't for
a few messages/sec of progress.)

**Auth / safety across integrity levels.** Parent generates a random pipe name
(`galapa-patch-<guid>`) and a 256-bit token, passes both as args. Handshake: child
connects, both exchange the token in `Hello`; mismatch ⇒ abort. The **child independently
re-verifies every patch** (ZiPatch CRC + `X-Filesize` length + expected from/to version)
*before* applying — defense-in-depth so a spoofed/poisoned command can't make the elevated
process apply attacker-controlled bytes. The child also monitors `--parent-pid` and exits
if the UI dies (no orphaned elevated applier). *Hardening TBD:* tighten the pipe DACL to
the user SID; consider making the elevated side the pipe server with that DACL.

**Message protocol** (envelope `{ opcode, payload }`, JSON):

| Dir | OpCode | Payload |
|---|---|---|
| C→P | `Hello` | `{ token }` — child ready |
| P→C | `StartInstall` | `{ patchFilePath, repo, fromVersion, toVersion, expectedLength, signature }` |
| C→P | `ApplyProgress` | `{ repo, toVersion, bytesApplied, totalBytes }` — streamed per chunk |
| C→P | `InstallOk` | `{ repo, toVersion }` |
| C→P | `InstallFailed` | `{ repo, toVersion, error }` |
| P→C | `FinalizeRepo` | `{ repo, finalVersion }` — write `Boot.ver`/`Game.ver` (+ `.bck`, TBD) |
| C→P | `Finished` | — |
| P→C | `Bye` | — child exits |

This is XIVLauncher's `PatcherIpc*` set plus a real `ApplyProgress` stream (their apply was
fast and local; ours can take minutes for a multi-GB game patch — and
`ZiPatchInstaller.InstallPatch` already exposes a per-chunk progress callback to drive it).

> **Decision to confirm:** the elevated child does **apply only**; the UI process does the
> download. This keeps network code unelevated and matches XIVLauncher. The alternative —
> child does download+apply, UI is purely a display shell — is simpler for the UI but runs
> all network/token logic elevated. I'm recommending apply-only; say if you'd rather the
> child own the whole flow.

---

## 5. Proposed components

```
Galapa.Core/Patcher/Download/
  DqxRepository.cs            enum { Boot, Game } (+ ver-file path, type= string)
  PatchListEntry.cs          chain node: Repo, FromVersion, ToVersion, Length, Signature, PlanUrl, FileName
  IDqxPatchClient.cs         the game.dqx.jp oracle (interface, for testing)
  DqxPatchClient.cs            GetNextPatch(repo, ver) / EnumerateChain(repo, from) / GetFreshUrl(entry) / GetCount(...)
  IPatchAcquisition.cs       backend abstraction (start, MakeTask, speed-limit)
  PatchAcquisitionTask.cs    abstract: StartAsync/CancelAsync + ProgressChanged/Complete
  HttpClientPatchAcquisition.cs   default: GET+Range, resume, just-in-time token refresh (url provider, not static url)
  PatchVerifier.cs           length + ZiPatch CRC (+ X-Signature when decoded)
  PatchManager.cs            orchestrator: parallel download queue + in-order apply-via-child

Galapa.Core/Patcher/Ipc/
  PatchIpcEnvelope.cs / PatchIpcOpCode.cs / payload records   (shared by both processes)
  IPatchIpcChannel.cs        SendMessage + MessageReceived
  NamedPipePatchIpcChannel.cs   the duplex-pipe transport (+ an in-process impl for tests)
  PatchInstallerHost.cs      PARENT side: StartIfNeeded(spawn+elevate), WaitOnHello, StartInstall, FinalizeRepo, Bye

Galapa.PatchInstaller/   (the elevated CHILD; already exists as a CLI)
  + "serve" subcommand → PatchApplyService: connect pipe, Hello, loop StartInstall→apply→report, FinalizeRepo, Bye
```

`PatchManager` keeps XIVLauncher's slot/queue model; its apply step calls
`PatchInstallerHost.StartInstall(file, entry)` (IPC) instead of applying in-process, and
relays `ApplyProgress` to the UI. `DqxPatchClient`, `HttpClientPatchAcquisition`, and the
token-refresh `urlProvider` are the DQX-specific additions described in §3.

---

## 6. Flow

```
LauncherUpdateService
  ├─ read Boot.ver / Game.ver
  ├─ host = PatchInstallerHost(install); host.StartIfNeeded()  (UAC once); host.WaitOnHello()
  ├─ boot:  chain = client.EnumerateChain(Boot, bootVer)
  │         PatchManager(Boot, chain, host).RunAsync()   → host.FinalizeRepo(Boot, latest)
  └─ game:  chain = client.EnumerateChain(Game, gameVer)
            PatchManager(Game, chain, host).RunAsync()    → host.FinalizeRepo(Game, latest)
  └─ host.Bye()

PatchManager.RunAsync (per repo):
  parallel:
    downloadQueue ─(slot i)→ acquisition(urlProvider=client.GetFreshUrl(entry_i)) → <cache>\<repo>\<from>-<to>.patch
    applyQueue    ─(in order)→ verify(entry) → host.StartInstall(file, entry)  ──IPC──► child applies, streams ApplyProgress → UI
```

---

## 7. Verification

Cheap → strong, run before apply (and re-run by the child elevated, per §4):

1. **Length** — downloaded bytes == `X-Filesize` == `Content-Range` total. Always.
2. **ZiPatch CRC** — open with `needsChecksum: true`, assert every `chunk.IsChecksumValid`
   (already done in tests). Strong, self-contained, server-secret-free; primary gate.
3. **`X-Signature`** — algorithm not yet pinned. Sample `8d4dbb66…` is 38 hex chars
   (19 bytes) → looks like a 20-byte digest printed **without zero-padding** (likely
   SHA-1(file) formatted `%x`/byte). Confirm against `DQXUpdater.exe` `0x618e68`
   ("Patch checksum error") before relying on it; until then record but gate on (1)+(2).

Failed verify → discard cache file, re-download once, then surface an error.

---

## 8. Open questions / things to confirm

- **Game apply target dir.** Boot → `<install>/Boot` (oracle-validated). Game uses the
  SqPack triple → bare `dataNNNNNNNN.win32.datN`, so target is likely
  `<install>/Game/Content/Data` (or `…/Content`). **Not yet oracle-validated** — needs a
  base dir at a game patch's *from* version run through the DQXUpdater oracle. Resolve
  before shipping game patching. The child picks the target dir from `repo`.
- **`.ver` / `.bck` handling.** Child writes `Boot.ver`/`Game.ver` (plain version strings)
  on `FinalizeRepo`. Whether DQX keeps a `.bck` backup like FFXIV is unconfirmed — TBD.
- **`X-Signature` algorithm** — §7.
- **`IsElevationRequiredForWrite`** — implement via a write-probe in the install dir;
  confirm behaviour under Proton (expected: writable, no elevation).
- **Repository / platform** — we send `repository=release`, `platform=win32`; confirm no
  other channels we must support.

---

## 9. Testing

- **Unit** — `DqxPatchClient` header parsing vs canned `HttpResponseMessage`s (next
  present/absent, split `_000N` URLs, missing headers); chain enumeration vs a fake
  `IDqxPatchClient`.
- **Acquisition** — resume vs a local `HttpListener` that supports Range and injects a
  one-shot 403 (assert token-refresh + resume-from-offset; assert GET-only, no HEAD).
- **IPC** — `NamedPipePatchIpcChannel` round-trips envelopes; the `serve` loop applies a
  synthetic patch and streams `ApplyProgress`; token-mismatch handshake aborts. An
  in-process channel impl lets `PatchManager`↔`PatchApplyService` be tested without
  spawning/elevating.
- **Integration (opt-in, env-gated)** — live `DqxPatchClient` enumerate-boot vs the known
  manifest; one small ranged GET vs `download.dqx.jp`. Skipped by default like the oracle
  differential test.
- **Apply** — covered by the existing oracle differential test for boot; extend to a game
  patch once §8's target dir is confirmed.

---

## 10. Phased implementation

1. `DqxRepository`, `PatchListEntry`, `IDqxPatchClient` + `DqxPatchClient` (+ unit tests,
   + opt-in live enumeration test). *Proves the protocol in C#.*
2. `IPatchAcquisition` + `HttpClientPatchAcquisition` (Range/resume, token refresh) +
   `PatchVerifier` (length + ZiPatch CRC).
3. IPC layer: `PatchIpc*` envelopes, `NamedPipePatchIpcChannel` (+ in-process impl),
   `PatchInstallerHost`, and the `serve` subcommand / `PatchApplyService` in
   Galapa.PatchInstaller. Elevation + handshake + parent-PID watchdog.
4. `PatchManager` (parallel download + in-order apply-via-child + streamed progress) +
   disk-space checks.
5. Launcher wiring (boot-then-game), `FinalizeRepo` version writes, UAC/elevation UX.

Each phase builds on the oracle-validated reader and is independently testable; the
in-process IPC impl keeps phases 3–4 testable without a real elevated subprocess.
