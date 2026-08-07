# Vendored MinHook

Upstream: <https://github.com/TsudaKageyu/minhook>
Commit:   `d94c64d` ("Fix link to MinHook article in README")
License:  BSD-2-Clause — see `LICENSE.txt` (retained verbatim, as are `AUTHORS.txt` / `README.md`)

## Why vendored

`Talon.Boot` is a native payload DLL injected into a 32-bit game process. Vendoring
keeps it buildable from a plain `git clone` with no submodule init, which matters because the
C++ projects are excluded from `Galapa.slnf` and are built by msbuild out-of-band.

## Local modifications

**None.** The sources are byte-for-byte upstream.

## What was removed

Only files we don't build, to keep the diff reviewable:

- `build/` (VC9–VC18 + MinGW project files), `CMakeLists.txt`, `cmake/` — we compile the sources
  directly from `Talon.Boot.vcxproj` rather than building libMinHook separately.
- `dll_resources/` — we link MinHook statically, so the standalone DLL's `.def`/`.rc` are unused.
- `.github/`, `.editorconfig`, `.gitignore` — upstream repo scaffolding.

Kept: `include/MinHook.h` and `src/**` (`buffer`, `hook`, `trampoline`, and the `hde` length
disassembler, which MinHook requires to size instructions when building trampolines).

To re-sync: clone upstream at the desired tag, then copy `include/` and `src/` over this tree.
