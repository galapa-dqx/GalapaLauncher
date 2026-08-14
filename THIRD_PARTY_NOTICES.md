# Third-party notices

## Dalamud

Talon's hook and signature-service API shape is adapted from
[Dalamud](https://github.com/goatcorp/Dalamud) at commit
`9fa2038de4ed6b816ca4fabfe6c9e8977825d6ea`:

- `Talon/Hooking/Hook.cs`
- `Talon/Hooking/HookBackend.cs`
- `Talon/Hooking/HookManager.cs`
- `Talon/Hooking/IGameInteropProvider.cs`
- `Talon/Hooking/GameInteropProvider.cs`
- `Talon/Hooking/FunctionPointerVariableHook.cs`
- `Talon/Hooking/ReloadedHook.cs`
- `Talon/Interop/ISigScanner.cs`
- `Talon/Interop/SigScanner.cs`
- `Talon/Interop/SignatureAttribute.cs`

Dalamud is distributed under the GNU Affero General Public License version 3.
This repository is distributed under the same license. Talon's PE32 decoding,
signature matching, batch scanning, and backend adapters are project-specific
implementations.

## Reloaded.Hooks

Talon redistributes `goatcorp.Reloaded.Hooks` and its Reloaded transitive
dependencies, including Reloaded.Hooks.Definitions, Reloaded.Memory, and
Reloaded.Memory.Buffers. These packages are distributed under the GNU Lesser
General Public License version 3. The LGPL supplement and the GNU General Public
License version 3 that it incorporates are shipped as
`licenses/Reloaded.LGPL-3.0.txt` and `licenses/GPL-3.0.txt`.

Reloaded.Hooks also depends on Iced, which is distributed under the MIT license
shipped as `licenses/Iced.MIT.txt`.

## .NET native-hosting headers

The files under `Talon.Boot/dotnet/` are copied from the .NET 10.0.1
`Microsoft.NETCore.App.Host.win-x86` pack. They are distributed by the .NET
Foundation under the MIT license included in that directory.

Talon's self-contained package also redistributes the .NET x86 runtime. The
exact runtime pack's `LICENSE.TXT` and `THIRD-PARTY-NOTICES.TXT` are copied into
`licenses/dotnet-runtime/` during build and publish.

## Microsoft.Data.Sqlite and SQLitePCLRaw

Talon redistributes `Microsoft.Data.Sqlite.Core` under the MIT license shipped
as `licenses/Microsoft.Data.Sqlite.MIT.txt`.

Talon also redistributes SQLitePCLRaw, Copyright 2014-2026 SourceGear, LLC,
under the Apache License 2.0 shipped as
`licenses/SQLitePCLRaw.Apache-2.0.txt`, and its bundled native SQLite library.
SQLite is dedicated to the public domain; its package notice is shipped as
`licenses/SQLite.Public-Domain.txt`.

## MinHook

`Talon.Recon` contains a vendored copy of MinHook for the separate reverse-
engineering payload. MinHook is distributed under the 2-clause BSD license in
`Talon.Recon/vendor/minhook/LICENSE.txt`. It is not included in Talon's runtime
artifact.
