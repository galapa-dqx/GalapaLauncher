# Bulk post-unpack barrier

Talon uses the packer's final page-protection request as its post-unpack barrier.
It does not need the game's original entry point or a game-function signature for
this step.

## Flow

1. The Injector reads the suspended process's mapped PE entry point and arms DR0
   there before it queues the early LoadLibrary APC.
2. The APC loads Talon.Boot. Boot installs its vectored exception handler and
   starts the managed-runtime worker.
3. Windows restores the pre-APC thread context. The entry-point breakpoint fires
   before the first packed instruction, and Boot moves DR0 to
   `NtProtectVirtualMemory`.
4. The handler accepts only `PAGE_EXECUTE_READ` over the exact page-rounded
   `.text` range.
5. At that transition, it clears DR0 and parks the unpacking thread before the
   protection call executes.
6. The worker starts managed Talon and waits for its initialization signal. It
   then releases the unpacking thread.

The protection transition defines unpack completion. Managed scanners resolve
version-specific game hooks only after the barrier.

## Protection predicate

`NtProtectVirtualMemory` rounds the requested address range to page boundaries.
The predicate applies the same rounding before it compares the request with the
mapped PE section:

```text
pageBegin = requestedBase & ~0xFFF
pageEnd   = (requestedBase + requestedSize + 0xFFF) & ~0xFFF
complete = NewProtect == PAGE_EXECUTE_READ
        && pageBegin == textPageBegin
        && pageEnd   == textPageEnd
```

The exact range and final RX protection reject writable staging transitions and
unrelated protection changes.

## Thread and exception rules

- The Injector sets DR0 while the primary thread is suspended. Debug-register
  changes made inside the APC would be lost when Windows restores the saved
  pre-APC context.
- The handler sets x86 EFLAGS.RF before it resumes each execute breakpoint.
  Clearing DR6 alone can repeat the single-step exception.
- Hook installation never runs in the exception handler. The handler parks the
  unpacking thread while the worker starts CoreCLR and installs managed hooks.
- A 30-second timeout fails open for game startup. Boot clears debug registers
  and releases the game without partially installing native game hooks.

## Update behavior

Boot derives both the entry point and `.text` range from the mapped PE headers.
A DQX update can require new managed hook signatures, but the barrier needs a
change only if the packer's protection behavior changes.
