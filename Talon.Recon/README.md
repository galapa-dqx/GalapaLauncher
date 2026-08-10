# Talon.Recon

`Talon.Recon.dll` is the disposable dynamic-analysis payload for investigating game
patches. Inject it with `Talon.Injector --boot-dll` in place of `Talon.Boot.dll`.

With no analysis environment variables, Recon hooks the low-level file APIs and writes:

- `%TEMP%\talon-recon-opens.log` — file-open traffic.
- `%TEMP%\talon-recon-stacks.bin` — raw stacks for reads from `.dat`/`.idx` handles.

Recon also consumes the entrypoint hardware breakpoint armed by the current Injector, so
it can be used as a boot payload without causing an unhandled single-step exception.

## Optional dynamic-analysis modes

Set `TALON_RECON_ANALYSIS` before launching. Results are appended to
`%TEMP%\talon-recon-analysis.log`.

| Value | Probe |
|---|---|
| `trajectory` | Sample `.text` population during startup to distinguish bulk from incremental unpacking. |
| `protect` | Retarget the Injector's entrypoint rendezvous to `NtProtectVirtualMemory` and record protection changes through the executable `.text` transition. |
| `full` | Run the target write-breakpoint, unpack trajectory, debug-register persistence, code-byte integrity, and execute-breakpoint stack probes. |

`full` is intentionally invasive and should be used only in a disposable analysis session.

The target-oriented probes default to the historical `Vfs_LoadResource` RVA `0xFD0E0`.
After a patch moves it, set `TALON_RECON_TARGET_RVA` to the new RVA (decimal or `0x` hex).
When an explicit RVA is supplied, Recon waits for executable, non-zero code rather than
requiring the old function prologue signature.

PowerShell example:

```powershell
$env:TALON_RECON_ANALYSIS = 'trajectory'
Talon.Injector\bin\Debug\net8.0-windows\Talon.Injector.exe `
  --boot-dll Talon.Recon\bin\x86\Debug\Talon.Recon.dll `
  -- "D:\Program Files (x86)\SquareEnix\DRAGON QUEST X\Game\DQXGame.exe" ...
```
