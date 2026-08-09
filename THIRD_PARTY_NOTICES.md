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
Talon's PE32 decoding, signature matching, batch scanning, and backend adapters
are project-specific implementations.

## .NET native-hosting headers

The files under `Talon.Boot/dotnet/` are copied from the .NET 10.0.1
`Microsoft.NETCore.App.Host.win-x86` pack. They are distributed by the .NET
Foundation under the MIT license included in that directory.
