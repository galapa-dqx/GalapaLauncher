#pragma once

// Start the universal KONN/NtProtectVirtualMemory unpack barrier. The injector must
// have supplied TALON_UNPACK_STAGE_RVA and pre-armed DR0 at that validated stage RVA.
// Installs the VEH synchronously, then spawns the scanner/install worker.
void start_unpack_watcher();
