#pragma once

// ── Logging ────────────────────────────────────────────────────────────────
// Diagnostics land in %TEMP%\talon-boot.log (absolute path, independent of the
// target's working directory) and are mirrored to OutputDebugString.
//
// LOG_ENABLED is a compile-time toggle: when undefined, dbg() collapses to a
// no-op that does not evaluate its arguments and open_log() is empty.

#define LOG_ENABLED

#ifdef LOG_ENABLED
void dbg(const char* fmt, ...);
void open_log();
#else
#define dbg(...) ((void)0)
inline void open_log() {}
#endif
