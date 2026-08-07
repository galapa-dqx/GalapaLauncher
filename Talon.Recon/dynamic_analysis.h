#pragma once

// Installs Recon's entrypoint-rendezvous VEH and starts any opt-in analysis worker.
// Safe to call from DLL_PROCESS_ATTACH; slow work is always delegated to a thread.
void StartDynamicAnalysis();
