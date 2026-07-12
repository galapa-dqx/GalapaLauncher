# Galapa patcher vs. XIVLauncher.PatchInstaller — architecture assessment

A side-by-side review of Galapa's ZiPatch patcher (the code on this branch under
[`Galapa.Core/Patcher`](../Galapa.Core/Patcher) + the [`Galapa.PatchInstaller`](../Galapa.PatchInstaller)
CLI) against XIVLauncher's patching stack
(`FFXIVQuickLauncher/src/XIVLauncher.Common/Patching` + `…/Game/Patch` +
`…/XIVLauncher.PatchInstaller`).

**Provenance.** Galapa's patcher is a *focused, modernized, DQX-corrected fork* of
XIVLauncher.Common's ZiPatch implementation. The file layout, class names, method
names, dispatch tables, and even a leftover FFXIV `RemoveAll` filter comment are
carried over verbatim. The divergences below are deliberate adaptations to (a) modern
C#, and (b) how the **DQX** updater (`DQXUpdater.exe`) actually behaves — which differs
from FFXIV's patcher in several format details and, critically, in failure handling.

> **Scope note.** This document covers the **ZiPatch reader/applier** ("the patcher").
> The layer *above* it — discovery, download, verification, orchestration — is covered
> separately in [`dqx-patch-download-design.md`](./dqx-patch-download-design.md), which
> mirrors XIVLauncher's `PatchManager`/`PatchInstaller`. Where that layer is relevant
> here, it's flagged as **out of scope / dropped**.

---

## 1. Executive summary

| | **Galapa** | **XIVLauncher** |
|---|---|---|
| What "the patcher" is | ZiPatch reader/applier + thin CLI | Full acquisition→verify→install→repair pipeline |
| Patcher LOC (apply path) | ~1,800 | ~9,600 (of which ~1,200 is the comparable core) |
| ZiPatch core | ✅ ported & modernized | ✅ origin |
| Indexed/partial patching (`IndexedZiPatch`) | ❌ dropped (~3,000 LOC) | ✅ |
| Out-of-process / elevated patcher (RPC/IPC) | ❌ dropped | ✅ SharedMemory IPC |
| Download/acquisition (aria2c) | ❌ dropped → separate design | ✅ |
| Integrity verify + repair (`PatchVerifier`) | ❌ dropped | ✅ |
| Patch-list parsing | ❌ dropped → separate design | ✅ |
| CLI surface | `install`, `list`, `verify` (hand-rolled) | 10 commands via `System.CommandLine` |
| Target framework | `net8.0-windows`, C# 14 (preview) | `net9.0` |
| Validation | **Byte-for-byte oracle differential tests** vs real `DQXUpdater.exe` | Runtime integrity check; matches documented FFXIV behavior |

**One-line takeaway:** Galapa kept just the ZiPatch apply path (~1,200 of
XIVLauncher's ~9,600 patching LOC, ≈12%), rewrote it in modern C#, fixed a latent bug,
and re-derived every format/behavioral constant against a captured DQX oracle. Everything around it
(download, IPC, elevation, verify/repair, indexed patching) was dropped, either as
unnecessary for DQX or deferred to a separate, still-unbuilt orchestration layer.

---

## 2. Structural comparison: what each "patcher" contains

XIVLauncher's patcher is a large, multi-subsystem pipeline. Galapa kept only the green
box and bolted on a small CLI.

```
XIVLauncher patching stack                         Galapa
──────────────────────────────                     ──────────────────────────
Game/Patch/
  PatchManager        download orchestration   ✗
  PatchVerifier       integrity + repair       ✗
  PatchInstaller      spawn elevated patcher   ✗
  Acquisition/Aria*   aria2c download          ✗  → docs/dqx-patch-download-design.md
  PatchList/*         parse server response    ✗  → docs/dqx-patch-download-design.md
Patching/
  RemotePatchInstaller  IPC install loop       ✗
  Rpc/*                 SharedMemory IPC       ✗
  IndexedZiPatch/*      partial/indexed patch  ✗  (verify+repair+range-download, ~3k LOC)
  Util/* (Circular,
    Multipart, Aria…)   download plumbing      ✗
┌───────────────────────────────────────────┐    ┌───────────────────────────────────┐
│ Patching/ZiPatch/   the format core    ✅ │ →  │ Galapa.Core/Patcher/ZiPatch/  ✅  │
│   ZiPatchFile, ZiPatchChunk, Chunk/*,     │    │   (ported, modernized,            │
│   SqpkCommand/*, Util/*                   │    │    DQX-corrected)                 │
└───────────────────────────────────────────┘    │ Galapa.Core/Patcher/              │
                                                 │   ZiPatchInstaller  (static entry)│
                                                 │   DirectoryComparer  (oracle diff)│
                                                 │ Galapa.PatchInstaller/  (CLI)     │
                                                 └───────────────────────────────────┘
```

### What Galapa deliberately omits

These are present in XIVLauncher and absent in Galapa. None are blockers for *applying*
a `.patch`; they belong to the surrounding workflow.

- **`IndexedZiPatch/` (~3,000 LOC).** XIVLauncher's most sophisticated subsystem: it
  parses a patch chain into a byte-range index mapping *target file regions → source
  patch locations*, enabling part-level **verify**, **repair**, and HTTP **range-request
  downloads** (download only the missing pieces). Galapa has no equivalent — it applies
  whole patches start-to-finish.
- **RPC/IPC + elevated subprocess** (`Rpc/*`, `RemotePatchInstaller`,
  `Game/Patch/PatchInstaller`). XIVLauncher runs the actual file mutation in a separate,
  possibly UAC-elevated process and talks to it over a `SharedMemory` channel
  (`Hello`/`StartInstall`/`InstallOk`/`Finish` opcodes). Galapa applies in-process.
- **Acquisition** (`Acquisition/Aria*`). XIVLauncher shells out to `aria2c` for
  multi-connection, resumable downloads. Deferred in Galapa to
  [`dqx-patch-download-design.md`](./dqx-patch-download-design.md).
- **`PatchManager`** — the 4-slot concurrent download + sequential-install state machine,
  disk-space checks, and SHA-1 block validation. Deferred likewise.
- **`PatchVerifier`** — integrity scan + repair driver (fetches manifests from goatcorp
  GitHub, quarantines bad files). No Galapa equivalent.
- **`PatchList/PatchListParser`** — parses FFXIV's tab-delimited patch-list response.
  DQX's protocol is different and is handled in the separate design.
- **`VerToBck` finalizer** — XIVLauncher copies `.ver`→`.bck` per repository after a
  successful install. Galapa has no `.ver`/`.bck` concept in the patcher.

---

## 3. The shared ZiPatch core — file-by-file correspondence

Almost 1:1. The format parsing is the same format, so the same files exist:

| XIVLauncher (`Patching/ZiPatch/…`) | Galapa (`Patcher/ZiPatch/…`) | Notes |
|---|---|---|
| `ZiPatchFile` | `ZiPatchFile` | identical role |
| `ZiPatchConfig` | `ZiPatchConfig` | **platform enum differs** (§5) |
| `ZiPatchException` | `ZiPatchException` | + Galapa adds `ZiPatchApplyAbortedException` (§5) |
| `Chunk/ZiPatchChunk` | `Chunk/ZiPatchChunk` | near line-for-line; **Galapa fixes a read bug** (§4) |
| `Chunk/{FileHeader,ApplyOption,ApplyFreeSpace,AddDirectory,DeleteDirectory,EndOfFile}` | same | parity |
| `Chunk/XXXXChunk` ("Never happens") | *— dropped —* | Galapa doesn't register the stub |
| `Chunk/SqpkChunk` | `Chunk/SqpkChunk` | parity |
| `Chunk/SqpkCommand/{AddData,DeleteData,ExpandData,Header,Index,File,TargetInfo,PatchInfo}` | same | **AddData/DeleteData/ExpandData/Header behavior differs** (§5) |
| `Util/{SqexFile,SqpackFile,SqpackDatFile,SqpackIndexFile}` | same | **path scheme differs** (§5) |
| `Util/{SqexFileStream,SqexFileStreamStore,AdvanceOnDispose,SqpkCompressedBlock}` | same | **compressed sentinel differs** (§5) |
| `Patching/Util/ChecksumBinaryReader` | `ZiPatch/Util/ChecksumBinaryReader` | moved into ZiPatch namespace |
| `Patching/Util/Crc32` | `ZiPatch/Util/ZiPatchCrc32` | **renamed** to disambiguate from DQX's filename-CRC |
| `Patching/Util/BinaryReaderHelpers` | `ZiPatch/Util/BinaryReaderExtensions` | renamed; same big-endian helpers |
| *(install loop in `RemotePatchInstaller.InstallPatch`)* | `Patcher/ZiPatchInstaller` | promoted to a clean static entry point |
| *(none)* | `Patcher/DirectoryComparer` | **new** — byte-for-byte tree diff for oracle tests |

---

## 4. Modernization differences (idiom, not behavior)

Galapa's port is a straight C#-modernization of the same logic:

- **Primary constructors, file-scoped namespaces, `sealed`, collection expressions
  (`[...]`), target-typed `new`, switch expressions** throughout. XIVLauncher is older
  C# (block namespaces, explicit ctors, `protected set`).
- **`internal`/`sealed` visibility tightening.** Galapa's SQPK command classes are
  `internal sealed`; XIVLauncher's are loose `class … : SqpkChunk`.
- **Static entry point.** Galapa exposes
  [`ZiPatchInstaller.InstallPatch(patchPath, gamePath, progress)`](../Galapa.Core/Patcher/ZiPatchInstaller.cs)
  and `InstallPatches(…)` as the reusable API. In XIVLauncher the equivalent loop is a
  static method buried inside `RemotePatchInstaller` next to the IPC machinery.
- **A genuine bug fix in the chunk reader.** Both copy each chunk frame into an
  `AsyncLocal<MemoryStream>` scratch buffer, but XIVLauncher does a single
  `reader.BaseStream.Read(buffer, 0, readSize)` and **ignores the return value** —
  `Stream.Read` may legally return fewer bytes than requested, which would silently
  corrupt a chunk on a partial read. Galapa replaces it with a `ReadExactly` loop
  (`ZiPatchChunk.GetChunk`,
  [`Chunk/ZiPatchChunk.cs:125`](../Galapa.Core/Patcher/ZiPatch/Chunk/ZiPatchChunk.cs))
  that loops until `readSize` bytes are in hand or throws `EndOfStreamException`.
- **CRC disambiguation.** ZiPatch's per-chunk checksum is the reflected zlib CRC-32
  (poly `0xEDB88320`). Galapa renames the class to `ZiPatchCrc32` specifically because
  `Galapa.Core` *also* contains a **different** CRC-32 (`Galapa.Core.Utils.Crc32`, the
  MSB-first poly `0x04C11DB7` used by DQX's filename obfuscator). The two are not
  interchangeable; the rename prevents a footgun XIVLauncher never faced.

None of the above changes the bytes written for a valid patch (except the read-bug fix,
which only matters on a truncated/streamed source).

---

## 5. DQX-specific behavioral divergences (the substantive part)

This is where Galapa is *not* just a reskin. Each item was derived from and verified
against the real DQX updater.

### 5.1 SqPack path scheme — decimal `Content/Data/` vs hex `/sqpack/exN/`

The `(mainId, subId, fileId)` triple renders to completely different on-disk paths.

| | filename template | example |
|---|---|---|
| **Galapa** ([`SqpackFile.cs`](../Galapa.Core/Patcher/ZiPatch/Util/SqpackFile.cs)) | `Content/Data/data{main:D4}{sub:D4}.{platform}.dat{N}` | `Content/Data/data00010000.win32.dat0` |
| **XIVLauncher** (`SqpackFile.cs`) | `/sqpack/{ffxiv\|exN}/{main:x2}{sub:x4}.{platform}.dat{N}` | `/sqpack/ffxiv/0a0000.win32.dat0` |

DQX uses **decimal** `data########` names in a single flat `Content/Data` folder, with
no per-expansion subdirectory (`subId` is `0` across every sampled patch). FFXIV uses
**hex** names bucketed into `ffxiv`/`ex1`/`ex2`… folders. This propagates into the two
subclasses:

- **Index extension:** Galapa writes `.idx` / `.idxN`
  ([`SqpackIndexFile.cs`](../Galapa.Core/Patcher/ZiPatch/Util/SqpackIndexFile.cs));
  XIVLauncher writes `.index` / `.indexN`.
- **Dat extension:** both write `.dat{N}`.

### 5.2 Platform enum — DQX's actual platforms

[`ZiPatchConfig.PlatformId`](../Galapa.Core/Patcher/ZiPatch/ZiPatchConfig.cs):

| value | Galapa | XIVLauncher |
|---|---|---|
| 0 | `Win32` | `Win32` |
| 1 | `Cafe` (Wii U) | `Ps3` |
| 2 | `Orbis` (PS4) | `Ps4` |
| 3 | `Unknown` | `Unknown` |

DQX shipped on Wii U (`Cafe`) where FFXIV shipped on PS3; the `1`/`2` ids are
relabeled accordingly (ids unconfirmed but irrelevant — only `Win32` is installed).

### 5.3 Compressed-block "stored" sentinel — 0x1F400 vs 0x7D00

In `SqpkFile` AddFile blocks, an uncompressed ("stored") block is flagged by a sentinel
in the `compressedSize` field
([`SqpkCompressedBlock.cs`](../Galapa.Core/Patcher/ZiPatch/Util/SqpkCompressedBlock.cs)):

| | sentinel | block size |
|---|---|---|
| **Galapa** | `0x1F400` (128000) | 64000-byte blocks |
| **XIVLauncher** | `0x7D00` (32000) | 16000-byte blocks |

In both, the sentinel is exactly 2× the format's max uncompressed block size — DQX just
uses a 4× larger block. The `0x80`-alignment math (`(size + 143) & ~127`) is identical.
Decompression is raw DEFLATE (`System.IO.Compression.DeflateStream`) in both.

### 5.4 Missing-`.dat` handling — **abort the patch vs blindly create** ⭐

This is the most important difference and the subject of this branch's most recent
commits.

**XIVLauncher**: every SQPK data command opens its target with
`FileMode.OpenOrCreate` *unconditionally* — if the `.dat` doesn't exist, it's created
(`SqpkAddData.ApplyChunk`, and likewise DeleteData/ExpandData/Header).

**Galapa**: replicates `DQXUpdater.exe`'s `ResolveTargetFile`, which **fails to resolve**
a missing `.dat` unless it is a legitimate *span extension* (a `dat{N>0}` whose
predecessor `dat{N-1}` already exists). When resolution fails, the updater's apply loop
(`ZiPatchApply_DoApply` → `sub_423b00`) **aborts the remainder of the patch** rather than
skipping the one chunk. Galapa models this with a new
[`ZiPatchApplyAbortedException`](../Galapa.Core/Patcher/ZiPatch/ZiPatchApplyAbortedException.cs)
thrown from the guard in each data command:

```csharp
// SqpkAddData / SqpkDeleteData / SqpkExpandData
if (!TargetFile.Exists(config.GamePath) &&
    (TargetFile.FileId == 0 || TargetFile.PriorSpanFileMissing(config.GamePath, config.Platform)))
    throw new ZiPatchApplyAbortedException(TargetFile.RelativePath);
```

…where [`SqpackDatFile.PriorSpanFileMissing`](../Galapa.Core/Patcher/ZiPatch/Util/SqpackDatFile.cs)
checks whether `dat{FileId-1}` is on disk. The abort is caught in
[`ZiPatchInstaller.InstallPatch`](../Galapa.Core/Patcher/ZiPatchInstaller.cs), which
`break`s the chunk loop — leaving the patch **partially applied**, byte-for-byte as the
oracle leaves it. Two real cases drove this:

- `1.6.97629.3→1.6.101161.1` (game): a `DeleteData` on the absent `data00130000.dat0`
  (the span base) aborts, so the later `data00150000` chunks never run.
- `7.0.303921.4→7.0.306094.1` (game): an `AddData` on `data00130000.dat1` whose base
  `dat0` never existed aborts *before* the trailing `H` (header) block — which is why
  that patch leaves every `.dat` header untouched.

A naive `OpenOrCreate` (XIVLauncher's behavior) would instead create a bogus `.dat` and
keep going, diverging from the oracle and potentially corrupting the install.

> Note: `ApplyOptionChunk` still parses the `IgnoreMissing`/`IgnoreOldMismatch` APLY
> flags into `ZiPatchConfig` (as XIVLauncher does), but neither implementation enforces
> them in the data path. Galapa's abort is hardcoded to match the observed oracle
> regardless of those flags (both are `false` in every sampled patch).

### 5.5 SqpkHeader ('H') — Dat-only, existing-file-only

[`SqpkHeader.ApplyChunk`](../Galapa.Core/Patcher/ZiPatch/Chunk/SqpkCommand/SqpkHeader.cs):

| | which headers | missing file |
|---|---|---|
| **Galapa** | **Dat only** (Index `H` is a no-op) | **never created** — `FileMode.Open`, early-return if `!Exists` |
| **XIVLauncher** | Dat **and** Index | created via `FileMode.OpenOrCreate` |

This mirrors DQXUpdater's `ZiPatch_WriteSqpackHeader`, which writes the Dat header
*verbatim* whenever reached but never touches Index files and never creates a missing
`.dat`. (Combined with §5.4: an 'H' block "not applying" is always because an earlier
data command already aborted the patch — confirmed by oracle trace.)

### 5.6 Empty-block record width — 20 bytes vs 24 bytes

`SqpackDatFile.WriteEmptyFileBlockAt` (used by DeleteData/ExpandData) writes a
`FileBlockHeader` of five fields. The "additional blocks" count differs in width:

- **Galapa** writes it as a 4-byte `int` → **20-byte** record (matches DQXUpdater's
  `ZiPatch_Sqpk{Delete,Expand}Data_WriteEmptyBlocks`).
- **XIVLauncher** writes it as a `long` → **24-byte** record.

A subtle but real byte-level divergence Galapa had to correct to match the oracle.

### 5.7 SqpkFile ('F') — DQX uses 'D', verbatim scrambled paths

[`SqpkFile`](../Galapa.Core/Patcher/ZiPatch/Chunk/SqpkCommand/SqpkFile.cs) notes that
the DQX **boot** patch uses the `A` (AddFile) and `D` (DeleteFile) operations heavily —
including `D`, which XIVLauncher's comments say it *"never saw in the wild"* for FFXIV.
DQX's scrambled boot filenames (e.g. `Bin/BurakqOnn!pcs--!qca`) are carried and written
byte-for-byte.

Across **every** sampled patch (boot + game, 1.6→7.6, ~24k chunks) only `A` and `D`
appear — no `R` (RemoveAll), no `M` (MakeDirTree). The updater nonetheless *implements*
all four: `DQXUpdater.exe`'s SqpkFile worker (`sub_4281b0`, called from
`ZiPatchApply::onSqpkFile`) switches on the operation byte with explicit cases
`0x44`/'D', `0x52`/'R', `0x4d`/'M', and 'A' as fallthrough. See §9 for the RemoveAll
layout, which is verified — not guessed.

---

## 6. Apply loop & orchestration

| | **Galapa** | **XIVLauncher** |
|---|---|---|
| Entry | `ZiPatchInstaller.InstallPatch` (static, in-process) | `RemotePatchInstaller.InstallPatch` (static, called by IPC server) |
| Loop | `foreach chunk → ApplyChunk`, **catches `ZiPatchApplyAbortedException` and stops** | `foreach chunk → ApplyChunk`, no abort concept |
| Failure model | partial apply matching the oracle | runs through; relies on later verify to catch problems |
| Process model | in-process | separate, possibly UAC-elevated, SharedMemory IPC |
| Post-install | none | `VerToBck` (`.ver`→`.bck` per repo) |
| Progress | `Action<ZiPatchChunk>` callback per chunk | IPC progress messages |

Galapa's loop is intentionally simple because it has no untrusted/elevated boundary to
cross and no multi-patch download queue to coordinate — those concerns were dropped (§2).

---

## 7. CLI surface

| | **Galapa.PatchInstaller** | **XIVLauncher.PatchInstaller** |
|---|---|---|
| Parser | hand-rolled `switch` on `args[0]` | `System.CommandLine` |
| Commands | `install`, `list`, `verify` | `install`, `rpc`, `index-create`, `index-create-integrity`, `index-rpc`, `index-update`, `index-verify`, `index-repair`, `check-integrity`, `index-rpc-test` |
| Purpose | apply, inspect, **diff-against-oracle** | full toolchain incl. index build, RPC worker, integrity, repair |

Galapa's `verify` verb is bespoke: it applies a patch into a temp dir (optionally
seeded from a base) and diffs the result against an expected tree using
`DirectoryComparer`. There is no XIVLauncher analog — it exists specifically to drive
the oracle workflow below. The 9 index/RPC commands XIVLauncher ships have no Galapa
counterpart (no `IndexedZiPatch`, no IPC).

---

## 8. Validation methodology — the real differentiator

This is arguably the most important *non-code* difference.

- **Galapa: byte-for-byte oracle differential testing.**
  [`OracleDifferentialTests`](../Galapa.Core.Tests/Patcher/OracleDifferentialTests.cs)
  applies real DQX patches and compares the output **byte-for-byte** to captures from
  the genuine `DQXUpdater.exe::ZiPatchApply::DoApply`. Every behavioral constant in §5
  (the abort/span semantics, the 20-byte record, the Dat-only header rule, the
  sentinel, the paths) was *derived from and is regression-locked against* that oracle.
  A synthetic [`PatchBuilder`](../Galapa.Core.Tests/Patcher/PatchBuilder.cs) authors
  valid ZiPatch byte streams (with real zlib CRCs) for unit tests, and
  [`DirectoryComparer`](../Galapa.Core/Patcher/DirectoryComparer.cs) does the diffing
  (also reused by the `verify` CLI verb).

- **XIVLauncher: runtime integrity, not a captured oracle.** Correctness rests on
  matching FFXIV's documented patch behavior plus `PatchVerifier` checking installed
  files against server/GitHub hash manifests *after* the fact. There's no test that
  asserts the patcher reproduces the official patcher's output bit-for-bit — it doesn't
  need to, because FFXIV ships hash manifests Galapa's DQX does not have in the same
  form.

The oracle approach is *why* Galapa could safely diverge in §5: each divergence is a
test-locked fact, not a guess.

---

## 9. Known gaps & latent risks in Galapa's fork

Honest caveats, mostly self-documented in the code:

- **`SqpkFile` RemoveAll ('R') is never exercised by a real patch — but Galapa's
  FFXIV-style layout is actually *correct*, not a guess.** No sampled DQX patch (boot +
  game, 1.6→7.6, ~24k chunks) contains an 'F'/'R' or 'F'/'M' command — only 'A' and 'D'.
  The original concern was that
  [`SqexFile.GetAllExpansionFiles`](../Galapa.Core/Patcher/ZiPatch/Util/SqexFile.cs)
  globs `sqpack/{ffxiv|exN}` and `movie/{ffxiv|exN}` — FFXIV's structure, not DQX's
  `Content/Data` / `Ex2000…Ex7000`. **Disassembly of `DQXUpdater.exe` resolves it:** the
  RemoveAll branch in `sub_4281b0` (`op == 0x52`) builds its paths from the literal
  strings `"ffxiv"` / `"ex%lu"` / `"sqpack/"` / `"movie/"` and enumerates exactly those
  two trees — i.e. the real DQX updater *does* use the FFXIV layout for RemoveAll (it's
  the same Square-Enix ZiPatch engine). So Galapa's mirror is faithful; it's just never
  triggered. The one detail still unverified is whether Galapa's `.var`/`.bk2`
  spare-filter matches the updater's enumeration filter (`data_5d8570`).
- **`SqpkIndex` ('I') is a parsed no-op** in both implementations (modern patchers don't
  use it).
- **`subId > 0` is unobserved** — Galapa assumes no per-expansion subdirectory because
  every sampled patch has `subId == 0`. If DQX ever ships a non-zero `subId`, the path
  builder needs revisiting.
- **No verify/repair/resume.** Dropping `IndexedZiPatch` means Galapa cannot
  incrementally repair a corrupt install or resume a half-applied patch — it can only
  re-apply from a known-good base. Acceptable given the partial-apply-matches-oracle
  model, but worth knowing.

---

## 10. Bottom line

Galapa's patcher is a **deliberately minimal, modernized, oracle-validated reimplementation**
of XIVLauncher's ZiPatch *core*, with everything above the apply loop removed. The
shared format-parsing code is close enough to be recognizably the same lineage, but the
two diverge wherever DQX's updater diverges from FFXIV's:

1. **Format constants** — decimal `Content/Data` paths, `.idx` extension, `0x1F400`
   stored sentinel, `Cafe`/`Orbis` platforms.
2. **Failure semantics** — the big one: DQX **aborts the whole patch** on an
   unresolvable missing `.dat` (with span-extension rules), where FFXIV blindly creates
   the file. Galapa models this exactly, including the resulting partial applies.
3. **Byte-level record details** — 20-byte empty-block record, Dat-only/existing-only
   header writes.

The trade-off is scope: XIVLauncher's stack is production-grade for the *entire* update
lifecycle (parallel download, elevation, partial repair, integrity), whereas Galapa
currently does one thing — apply a `.patch` correctly — and does it with stronger
correctness evidence (the byte-for-byte oracle) than XIVLauncher has for its own apply
path. The dropped lifecycle pieces are tracked for DQX in
[`dqx-patch-download-design.md`](./dqx-patch-download-design.md).
