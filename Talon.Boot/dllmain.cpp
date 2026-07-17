// Talon.Boot — DQX loose-file override payload (VFS-semantic hook).
//
// Injected into the 32-bit DQX game process by Talon.Injector via an early-bird
// APC (see Talon.Injector/Injector.cs). Once the game has unpacked itself, this
// DLL inline-hooks the game's own resource loader, Vfs_LoadResource, so that a
// loose file living under an override directory is served in place of the asset
// the game would otherwise decompress out of its packed .dat archives.
//
// Why the VFS layer instead of dragonhook's file-I/O layer:
//   dragonhook hooks CreateFile/ReadFile and re-encodes loose files back into the
//   game's on-disk block format (IDX relocation + type-2 deflate). Hooking the
//   semantic loader instead means the game does its own archive I/O and we only
//   intercept the (path -> resource) call. No IDX parsing, no re-compression, no
//   knowledge of the .dat format, and no --data-dir: the game reads Content\Data
//   itself. The trade-off is that this binds to a game-internal address, so it is
//   version-specific (see the re-anchoring notes below) whereas dragonhook is not.
//
// The hook target (DQX 8.0), reversed in Binary Ninja:
//   Vfs_LoadResource is __thiscall(this /*ecx*/, char* path, int expansion,
//   int mount, int mustBeZero); callee-cleans its 4 stack args. `path` (the first
//   stack arg) is the archive-relative VFS path that gets hashed for the IDX
//   lookup — i.e. exactly the layout a dragonhook mod folder already mirrors.
//   The VFS manager object (`this`) carries __cdecl callbacks:
//     this+0x110  alloc(tag=0, size, flag=1)            -> buffer
//     this+0x114  free(tag=0, buffer)
//     this+0x11c  construct(path, size, buffer, FILE*=0, offset=0) -> resource
//   The game's own multi-chunk path does: buf = alloc(0, decompSize, 1); fill buf;
//   res = construct(path, decompSize, buf, 0, 0); free(staging) — and never frees
//   `buf`. So the constructed resource TAKES OWNERSHIP of buf. Serving an override
//   is therefore: buf = alloc; read our file into buf; construct(path, size, buf,
//   0, 0); return res — with NO free (freeing buf here would be a use-after-free).
//
// Timing: the game is packed, so Vfs_LoadResource is not present at its address
// until the packer has run — which is after our pre-entrypoint injection. We
// therefore defer hook installation to a watcher thread that polls the expected
// address (and falls back to a signature scan) until the function's prologue
// appears, then installs the inline hook. If it never appears (patch-day address
// churn, or the game self-restores its .text) the DLL degrades to a clean no-op.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <tlhelp32.h>
#include <string.h>
#include <stdio.h>
#include <stdarg.h>
#include <stdint.h>

// ── Logging ────────────────────────────────────────────────────────────────
// Diagnostics land in %TEMP%\talon-boot.log (absolute path, independent of the
// target's working directory) and are mirrored to OutputDebugString.

#define LOG_ENABLED

#ifdef LOG_ENABLED
static FILE* g_log = nullptr;
static void dbg(const char* fmt, ...) {
    char buf[1024];
    va_list ap; va_start(ap, fmt);
    vsnprintf(buf, sizeof(buf), fmt, ap);
    va_end(ap);
    OutputDebugStringA(buf);
    if (g_log) { fputs(buf, g_log); fflush(g_log); }
}
static void open_log() {
    wchar_t tempDir[MAX_PATH];
    DWORD len = GetTempPathW(MAX_PATH, tempDir);
    if (len == 0 || len > MAX_PATH) return;
    wchar_t path[MAX_PATH];
    if (swprintf(path, MAX_PATH, L"%stalon-boot.log", tempDir) < 0) return;
    g_log = _wfopen(path, L"a");
}
#else
#define dbg(...) ((void)0)
static void open_log() {}
#endif

// ── Minimal x86-32 instruction length decoder ───────────────────────────────
// Used by inline_hook to steal whole instructions for the trampoline. Handles
// the opcode shapes found in the Vfs_LoadResource prologue (push/mov/sub/and).
// Returns 1 on unknown opcode (safe fallback — caller accumulates until >= 5).

static int modrm_extra(const uint8_t *p) {
    uint8_t modrm = p[0];
    int mod = modrm >> 6;
    int rm  = modrm & 7;
    int n   = 1;                          // ModRM itself
    if (mod == 3) return n;               // register-register, no extras
    if (rm == 4)  n++;                    // SIB byte present
    if (mod == 1) n += 1;                 // disp8
    else if (mod == 2) n += 4;            // disp32
    else if (mod == 0 && rm == 5) n += 4; // [disp32]
    return n;
}

static int insn_len32(const uint8_t *p) {
    uint8_t op = p[0];

    if ((op >= 0x50 && op <= 0x5F) ||     // push/pop reg
        op == 0x90 || op == 0xC9 || op == 0xC3 || op == 0xCB || op == 0xCC ||
        op == 0x06 || op == 0x07 || op == 0x0E || op == 0x16 || op == 0x17 ||
        op == 0x1E || op == 0x1F || op == 0x9C || op == 0x9D ||
        op == 0x60 || op == 0x61 || op == 0xF8 || op == 0xF9 || op == 0xFA || op == 0xFB)
        return 1;

    if ((op >= 0x70 && op <= 0x7F) ||     // Jcc rel8
        op == 0xEB || op == 0xE0 || op == 0xE1 || op == 0xE2 || op == 0xE3 ||
        op == 0x6A || (op >= 0xB0 && op <= 0xB7))
        return 2;

    if (op == 0xC2 || op == 0xCA) return 3;

    if (op == 0xE8 || op == 0xE9 || op == 0x68 ||
        op == 0xA1 || op == 0xA3 || (op >= 0xB8 && op <= 0xBF))
        return 5;

    if (op == 0xA0 || op == 0xA2) return 2;

    if (op == 0x85 || op == 0x87 ||
        op == 0x01 || op == 0x03 || op == 0x09 || op == 0x0B ||
        op == 0x11 || op == 0x13 || op == 0x19 || op == 0x1B ||
        op == 0x21 || op == 0x23 || op == 0x29 || op == 0x2B ||
        op == 0x31 || op == 0x33 || op == 0x39 || op == 0x3B ||
        op == 0x89 || op == 0x8B || op == 0x8D || op == 0x8F ||
        op == 0xFF || op == 0xD3)
        return 1 + modrm_extra(p + 1);

    if (op == 0x83 || op == 0x80 || op == 0xC0 || op == 0xC1 ||
        op == 0xD0 || op == 0xD1 || op == 0x6B)
        return 1 + modrm_extra(p + 1) + 1;

    if (op == 0x81 || op == 0x69)
        return 1 + modrm_extra(p + 1) + 4;

    if (op == 0x0F) {
        uint8_t op2 = p[1];
        if (op2 >= 0x80 && op2 <= 0x8F) return 6;
        int n = 2 + modrm_extra(p + 2);
        if (op2 == 0xA4 || op2 == 0xAC || op2 == 0xBA ||
            op2 == 0xC2 || op2 == 0xC4 || op2 == 0xC5 || op2 == 0xC6)
            n += 1;
        return n;
    }

    return 1; // unknown — safe fallback
}

// ── Inline hooking ──────────────────────────────────────────────────────────
// Patch the first bytes of `target` with a jmp to `replacement`; return a
// trampoline that runs the stolen bytes then jumps back, so the caller can still
// invoke the original. Vfs_LoadResource is a normal function (not an export
// thunk) so the E9/FF25 follow below is a no-op for it, but it is harmless and
// kept for generality.

static void* inline_hook(void* target, void* replacement) {
    uint8_t* fn = (uint8_t*)target;

    int hops = 0;
    while (hops < 8) {
        if (fn[0] == 0xE9) {
            int32_t rel;
            memcpy(&rel, fn + 1, 4);
            fn = fn + 5 + rel;
        } else if (fn[0] == 0xFF && fn[1] == 0x25) {
            uint32_t mem_addr;
            memcpy(&mem_addr, fn + 2, 4);
            uint32_t dest;
            memcpy(&dest, (void*)(uintptr_t)mem_addr, 4);
            fn = (uint8_t*)(uintptr_t)dest;
        } else {
            break;
        }
        hops++;
    }

    dbg("[hook] target=%p bytes: %02X %02X %02X %02X %02X %02X %02X %02X (after %d hop(s))\n",
        fn, fn[0], fn[1], fn[2], fn[3], fn[4], fn[5], fn[6], fn[7], hops);

    int stolen = 0;
    while (stolen < 5) {
        int ilen = insn_len32(fn + stolen);
        if (ilen < 1) ilen = 1;
        stolen += ilen;
        if (stolen > 20) { dbg("[hook] ERROR: couldn't find 5-byte boundary\n"); return nullptr; }
    }
    dbg("[hook] stolen=%d bytes\n", stolen);

    uint8_t* tramp = (uint8_t*)VirtualAlloc(nullptr, 32,
        MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    if (!tramp) { dbg("[hook] VirtualAlloc failed\n"); return nullptr; }

    DWORD old;
    VirtualProtect(fn, stolen, PAGE_EXECUTE_READWRITE, &old);

    memcpy(tramp, fn, stolen);
    for (int i = 0; i < stolen; ) {
        int ilen = insn_len32(tramp + i);
        if ((tramp[i] == 0xE9 || tramp[i] == 0xE8) && ilen == 5) {
            int32_t orig_rel;
            memcpy(&orig_rel, tramp + i + 1, 4);
            uintptr_t abs = (uintptr_t)(fn + i + 5) + (uintptr_t)(intptr_t)orig_rel;
            int32_t new_rel = (int32_t)((intptr_t)abs - (intptr_t)(tramp + i + 5));
            memcpy(tramp + i + 1, &new_rel, 4);
        }
        i += ilen;
    }

    tramp[stolen] = 0xE9;
    int32_t back = (int32_t)((intptr_t)(fn + stolen) - (intptr_t)(tramp + stolen + 5));
    memcpy(tramp + stolen + 1, &back, 4);

    fn[0] = 0xE9;
    int32_t fwd = (int32_t)((intptr_t)(uint8_t*)replacement - (intptr_t)(fn + 5));
    memcpy(fn + 1, &fwd, 4);

    VirtualProtect(fn, stolen, old, &old);
    FlushInstructionCache(GetCurrentProcess(), fn, stolen);
    return tramp;
}

// ── Vfs_LoadResource hook ─────────────────────────────────────────────────────

// __thiscall: `this` in ecx, four stack args, callee-cleans.
typedef void* (__thiscall *PfnVfsLoadResource)(void* thisPtr, const char* path,
                                               int expansion, int mount, int mustBeZero);

// The VFS manager's __cdecl callbacks (offsets from `this`).
typedef void* (__cdecl *PfnVfsAlloc)(int tag, uint32_t size, int flag);       // this+0x110
typedef void  (__cdecl *PfnVfsFree)(int tag, void* buffer);                   // this+0x114
typedef void* (__cdecl *PfnVfsConstruct)(const char* path, uint32_t size,
                                         void* buffer, void* file, uint32_t offset); // this+0x11c

static PfnVfsLoadResource g_orig_VfsLoadResource = nullptr;
static char               g_override_dir[MAX_PATH] = {};

// Opt-in diagnostic (TALON_VFS_CENSUS): log the path of every resource the game
// requests through Vfs_LoadResource, capped so an asset-heavy boot can't flood
// the log. Useful for discovering the archive-relative paths a mod would mirror.
static bool               g_census = false;
static volatile LONG      g_census_count = 0;
static const LONG         kCensusCap = 400;

// Our replacement. Declared __fastcall so we capture the incoming ecx (`this`);
// edx is unused (the game's __thiscall never sets it) and the remaining four
// params land on the stack exactly as Vfs_LoadResource's stack args do, so the
// callee-cleanup of 0x10 bytes matches the original.
static void* __fastcall hook_VfsLoadResource(void* thisPtr, void* /*edx*/,
                                             const char* path, int expansion,
                                             int mount, int mustBeZero) {
    if (g_census && path) {
        LONG n = InterlockedIncrement(&g_census_count);
        if (n <= kCensusCap)
            dbg("[census] #%ld exp=%d mount=%d path=%s\n", n, expansion, mount, path);
    }

    if (thisPtr && path && g_override_dir[0]) {
        char fs[MAX_PATH];
        int n = snprintf(fs, sizeof(fs), "%s\\%s", g_override_dir, path);
        if (n > 0 && n < (int)sizeof(fs)) {
            for (char* p = fs; *p; ++p)
                if (*p == '/') *p = '\\';

            HANDLE h = CreateFileA(fs, GENERIC_READ, FILE_SHARE_READ, nullptr,
                                   OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
            if (h != INVALID_HANDLE_VALUE) {
                LARGE_INTEGER li;
                if (GetFileSizeEx(h, &li) && li.QuadPart > 0 && li.QuadPart <= 0x7FFFFFFF) {
                    uint32_t size = (uint32_t)li.QuadPart;
                    PfnVfsAlloc     vfsAlloc     = *(PfnVfsAlloc*)((uint8_t*)thisPtr + 0x110);
                    PfnVfsFree      vfsFree      = *(PfnVfsFree*)((uint8_t*)thisPtr + 0x114);
                    PfnVfsConstruct vfsConstruct = *(PfnVfsConstruct*)((uint8_t*)thisPtr + 0x11c);

                    // Allocate the resource buffer with the GAME's allocator so the
                    // constructed resource — which takes ownership — can free it
                    // later through the game's own matching free path.
                    void* buf = vfsAlloc ? vfsAlloc(0, size, 1) : nullptr;
                    if (buf) {
                        DWORD rd = 0;
                        BOOL ok = ReadFile(h, buf, size, &rd, nullptr);
                        CloseHandle(h);
                        if (ok && rd == size && vfsConstruct) {
                            void* res = vfsConstruct(path, size, buf, nullptr, 0);
                            dbg("[vfs] OVERRIDE %s (%u bytes) -> res=%p\n", path, size, res);
                            return res; // resource owns buf — do NOT free it here
                        }
                        // Read/construct unavailable: hand the game buffer back and
                        // fall through to the original loader.
                        if (vfsFree) vfsFree(0, buf);
                        return g_orig_VfsLoadResource(thisPtr, path, expansion, mount, mustBeZero);
                    }
                }
                CloseHandle(h);
            }
        }
    }
    return g_orig_VfsLoadResource(thisPtr, path, expansion, mount, mustBeZero);
}

// ── Locating Vfs_LoadResource at runtime ─────────────────────────────────────
// Binary Ninja DB address 0x5ad0e0 sits at image base 0x4b0000, so the function
// RVA is 0x5ad0e0 - 0x4b0000 = 0xfd0e0. At runtime VA = <exe load base> + RVA.
// The prologue signature is used both to confirm the expected address and, if the
// game has been patched and the RVA moved, to scan for it. Patch-day re-anchor:
// if this signature stops matching, re-find Vfs_LoadResource in Binary Ninja via
// its error strings ("ERROR: readerror0 %x") and update RVA + signature.

static const uint32_t VFS_LOADRESOURCE_RVA = 0xFD0E0;

// 53 8B DC 83 ?? ?? 83 ?? ?? 83 ?? ?? 55 8B ?? ?? 89 ?? ?? ?? 8B EC B8 ?? ?? ?? ?? E8
//   push ebx; mov ebx,esp; (sub/and/add esp,imm8)x3; push ebp; mov ebp,[ebx+4];
//   mov [esp+4],ebp; mov ebp,esp; mov eax,imm32; call __chkstk
static const int VFS_SIG[] = {
    0x53, 0x8B, 0xDC, 0x83, -1, -1, 0x83, -1, -1, 0x83, -1, -1,
    0x55, 0x8B, -1, -1, 0x89, -1, -1, -1, 0x8B, 0xEC, 0xB8, -1, -1, -1, -1, 0xE8
};
static const int VFS_SIG_LEN = (int)(sizeof(VFS_SIG) / sizeof(VFS_SIG[0]));

static bool sig_matches(const uint8_t* p) {
    for (int i = 0; i < VFS_SIG_LEN; i++)
        if (VFS_SIG[i] >= 0 && p[i] != (uint8_t)VFS_SIG[i]) return false;
    return true;
}

// True if [addr, addr+len) is committed, readable and executable — safe to read
// while the packer may still be mapping the image.
static bool region_readable(const uint8_t* addr, size_t len) {
    MEMORY_BASIC_INFORMATION mbi;
    const uint8_t* end = addr + len;
    while (addr < end) {
        if (VirtualQuery(addr, &mbi, sizeof(mbi)) == 0) return false;
        if (mbi.State != MEM_COMMIT) return false;
        DWORD prot = mbi.Protect & 0xFF;
        if (prot == PAGE_NOACCESS || (mbi.Protect & PAGE_GUARD)) return false;
        if (!(prot == PAGE_EXECUTE || prot == PAGE_EXECUTE_READ ||
              prot == PAGE_EXECUTE_READWRITE || prot == PAGE_EXECUTE_WRITECOPY))
            return false;
        addr = (const uint8_t*)mbi.BaseAddress + mbi.RegionSize;
    }
    return true;
}

// One guarded signature scan over the main module's image (fallback for when the
// expected RVA no longer matches, e.g. after a game patch).
static uint8_t* scan_for_vfs(uint8_t* base) {
    IMAGE_DOS_HEADER* dos = (IMAGE_DOS_HEADER*)base;
    if (dos->e_magic != IMAGE_DOS_SIGNATURE) return nullptr;
    IMAGE_NT_HEADERS* nt = (IMAGE_NT_HEADERS*)(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE) return nullptr;
    uint32_t imageSize = nt->OptionalHeader.SizeOfImage;

    MEMORY_BASIC_INFORMATION mbi;
    uint8_t* addr = base;
    uint8_t* end  = base + imageSize;
    while (addr < end) {
        if (VirtualQuery(addr, &mbi, sizeof(mbi)) == 0) break;
        uint8_t* regionEnd = (uint8_t*)mbi.BaseAddress + mbi.RegionSize;
        DWORD prot = mbi.Protect & 0xFF;
        bool exec = (prot == PAGE_EXECUTE || prot == PAGE_EXECUTE_READ ||
                     prot == PAGE_EXECUTE_READWRITE || prot == PAGE_EXECUTE_WRITECOPY);
        if (mbi.State == MEM_COMMIT && exec && !(mbi.Protect & PAGE_GUARD)) {
            uint8_t* scanEnd = (regionEnd < end ? regionEnd : end) - VFS_SIG_LEN;
            for (uint8_t* p = (uint8_t*)mbi.BaseAddress; p <= scanEnd; p++)
                if (sig_matches(p)) return p;
        }
        addr = regionEnd;
    }
    return nullptr;
}

// ── Unpack trigger ─────────────────────────────────────────────────────────────
// DQX's .text is packed: at inject the target region is committed but zero-filled;
// the packer writes the decrypted code in a few hundred ms. Rather than poll for
// the prologue to appear, we arm a HARDWARE WRITE breakpoint at target[0] before
// the packer runs; the resulting #DB (caught by a VEH) signals the exact moment
// the unpacker writes our function. This is event-driven and deterministic, and
// it survives because DQX does not scan debug registers (verified 2026-07-17).
//
// The write fires when byte 0 is written, so the rest of the prologue lands micro-
// seconds later — the watcher does a short bounded confirm before hooking. A poll
// fallback (loudly logged) covers the case where the trigger never fires, e.g. a
// future patch that clears DRs; per-patch testing is expected to catch that.

// DR7 for DR0 as a 1-byte WRITE breakpoint: L0=1 (bit 0), RW0=01 (write), LEN0=00.
static const DWORD    kDr7WriteDr0 = 0x00010001;
static volatile LONG  g_write_armed = 0;
static HANDLE         g_unpack_event = nullptr;
static PVOID          g_veh_handle   = nullptr;

static LONG CALLBACK unpack_veh(EXCEPTION_POINTERS* ep) {
    if (g_write_armed &&
        ep->ExceptionRecord->ExceptionCode == EXCEPTION_SINGLE_STEP &&
        (ep->ContextRecord->Dr6 & 0xF)) {
        // The unpacker just wrote target[0]. Disarm this thread's DR so we don't
        // re-trap, signal the watcher, and let the packer finish its copy.
        ep->ContextRecord->Dr0 = 0;
        ep->ContextRecord->Dr7 = 0;
        ep->ContextRecord->Dr6 = 0;
        if (g_unpack_event) SetEvent(g_unpack_event);
        return EXCEPTION_CONTINUE_EXECUTION;
    }
    return EXCEPTION_CONTINUE_SEARCH;
}

// Set DR0/DR7 on every thread in this process except `selfTid`.
static void set_dr_all_threads(uintptr_t addr, DWORD dr7, DWORD selfTid) {
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
    if (snap == INVALID_HANDLE_VALUE) return;
    DWORD pid = GetCurrentProcessId();
    THREADENTRY32 te; te.dwSize = sizeof(te);
    if (Thread32First(snap, &te)) {
        do {
            if (te.th32OwnerProcessID != pid || te.th32ThreadID == selfTid) continue;
            HANDLE th = OpenThread(THREAD_SUSPEND_RESUME | THREAD_GET_CONTEXT | THREAD_SET_CONTEXT,
                                   FALSE, te.th32ThreadID);
            if (!th) continue;
            if (SuspendThread(th) != (DWORD)-1) {
                CONTEXT c; c.ContextFlags = CONTEXT_DEBUG_REGISTERS;
                if (GetThreadContext(th, &c)) {
                    c.Dr0 = addr;
                    c.Dr7 = dr7;
                    c.ContextFlags = CONTEXT_DEBUG_REGISTERS;
                    SetThreadContext(th, &c);
                }
                ResumeThread(th);
            }
            CloseHandle(th);
        } while (Thread32Next(snap, &te));
    }
    CloseHandle(snap);
}

// ── Boot ─────────────────────────────────────────────────────────────────────

static volatile LONG g_booted = 0;

// Locate Vfs_LoadResource (via the write-BP unpack trigger, with a poll fallback)
// and install the inline hook. Runs on its own thread so DllMain does no work under
// the loader lock beyond spawning it.
static DWORD WINAPI watcher_thread(LPVOID) {
    uint8_t* base = (uint8_t*)GetModuleHandleA(nullptr);
    uint8_t* candidate = base + VFS_LOADRESOURCE_RVA;
    DWORD selfTid = GetCurrentThreadId();
    dbg("[boot] watcher: module base=%p, expecting Vfs_LoadResource at %p\n", base, candidate);

    uint8_t* target = nullptr;

    if (region_readable(candidate, VFS_SIG_LEN) && sig_matches(candidate)) {
        // Already unpacked (we lost the race, or a future build ships unpacked).
        target = candidate;
        dbg("[boot] watcher: target already unpacked; hooking directly\n");
    } else {
        // Arm the write-BP trigger and wait for the unpacker to write target[0].
        g_unpack_event = CreateEventA(nullptr, TRUE, FALSE, nullptr);
        g_veh_handle = AddVectoredExceptionHandler(1, unpack_veh);
        InterlockedExchange(&g_write_armed, 1);
        set_dr_all_threads((uintptr_t)candidate, kDr7WriteDr0, selfTid);
        dbg("[boot] watcher: armed write-BP trigger @%p; waiting for unpack\n", candidate);

        DWORD wr = g_unpack_event ? WaitForSingleObject(g_unpack_event, 15000) : WAIT_FAILED;
        InterlockedExchange(&g_write_armed, 0);
        set_dr_all_threads(0, 0, selfTid);              // disarm every thread
        if (g_veh_handle) { RemoveVectoredExceptionHandler(g_veh_handle); g_veh_handle = nullptr; }
        if (g_unpack_event) { CloseHandle(g_unpack_event); g_unpack_event = nullptr; }

        if (wr == WAIT_OBJECT_0) {
            // Trigger fired — the copy of our function is in flight; confirm the full
            // prologue is present (lands within microseconds) before hooking.
            for (int i = 0; i < 200 && !target; i++) {
                if (region_readable(candidate, VFS_SIG_LEN) && sig_matches(candidate)) target = candidate;
                else Sleep(1);
            }
            dbg(target ? "[boot] watcher: unpack-write trigger FIRED; prologue present\n"
                       : "[boot] watcher: trigger fired but prologue never completed — will poll\n");
        } else {
            dbg("[boot] watcher: *** WRITE-BP TRIGGER DID NOT FIRE (wait=%lu) — FALLING BACK TO POLL ***\n", wr);
        }
    }

    // Fallback: poll the expected address, then a one-shot signature scan.
    for (int i = 0; i < 600 && !target; i++) {
        if (region_readable(candidate, VFS_SIG_LEN) && sig_matches(candidate)) { target = candidate; break; }
        Sleep(100);
    }
    if (!target) {
        dbg("[boot] watcher: expected address never matched; scanning image\n");
        target = scan_for_vfs(base);
        if (target)
            dbg("[boot] watcher: found by scan at %p (rva=0x%X)\n", target, (unsigned)(target - base));
    }
    if (!target) {
        dbg("[boot] watcher: Vfs_LoadResource not found — Talon.Boot is a no-op this launch\n");
        return 0;
    }

    g_orig_VfsLoadResource = (PfnVfsLoadResource)inline_hook(target, (void*)hook_VfsLoadResource);
    if (g_orig_VfsLoadResource)
        dbg("[boot] watcher: hooked Vfs_LoadResource @%p, trampoline=%p, override dir=%s\n",
            target, g_orig_VfsLoadResource, g_override_dir);
    else
        dbg("[boot] watcher: inline_hook failed\n");
    return 0;
}

// Reads TALON_OVERRIDE_DIR and, if present, spawns the watcher. Idempotent.
static void talon_boot() {
    if (InterlockedCompareExchange(&g_booted, 1, 0) != 0) return;

    open_log();

    SYSTEMTIME st;
    GetSystemTime(&st);
    dbg("[boot] Talon.Boot loaded (pid=%lu) at %04u-%02u-%02u %02u:%02u:%02u.%03uZ\n",
        GetCurrentProcessId(), st.wYear, st.wMonth, st.wDay,
        st.wHour, st.wMinute, st.wSecond, st.wMilliseconds);

    // DIAGNOSTIC: is Vfs_LoadResource already unpacked at inject time (pre-entrypoint)?
    // If so, .text is plaintext at load and we can hook right here in DllMain — no watcher,
    // no polling. If not, the code is packed and we genuinely need an unpack signal.
    {
        uint8_t* base = (uint8_t*)GetModuleHandleA(nullptr);
        uint8_t* cand = base + VFS_LOADRESOURCE_RVA;
        bool readable = region_readable(cand, VFS_SIG_LEN);
        bool present  = readable && sig_matches(cand);
        dbg("[boot] DIAGNOSTIC pre-entrypoint: target=%p readable=%d prologue=%s  first bytes: %02X %02X %02X %02X %02X %02X\n",
            cand, (int)readable, present ? "PRESENT (.text plaintext at load — no unpack signal needed)" : "absent (packed)",
            readable ? cand[0] : 0, readable ? cand[1] : 0, readable ? cand[2] : 0,
            readable ? cand[3] : 0, readable ? cand[4] : 0, readable ? cand[5] : 0);
    }

    DWORD n = GetEnvironmentVariableA("TALON_OVERRIDE_DIR", g_override_dir, sizeof(g_override_dir));
    if (n == 0 || n >= sizeof(g_override_dir)) {
        g_override_dir[0] = '\0';
        dbg("[boot] TALON_OVERRIDE_DIR not set — Talon.Boot is a no-op this launch\n");
        return;
    }
    dbg("[boot] override dir = %s\n", g_override_dir);

    char censusBuf[8];
    g_census = GetEnvironmentVariableA("TALON_VFS_CENSUS", censusBuf, sizeof(censusBuf)) > 0;
    if (g_census) dbg("[boot] VFS census logging ENABLED (cap=%ld)\n", kCensusCap);

    // Spawn the watcher off the loader lock. Creating (not waiting on) a thread
    // from DllMain is safe; DisableThreadLibraryCalls suppresses its attach call.
    HANDLE h = CreateThread(nullptr, 0, watcher_thread, nullptr, 0, nullptr);
    if (h) CloseHandle(h);
    else dbg("[boot] CreateThread(watcher) failed (err=%lu)\n", GetLastError());
}

// Explicit hand-off entrypoint for the entrypoint-rewrite / CLR bootstrap paths.
// talon_boot() is idempotent, so a later call after DllMain already ran is a no-op.
extern "C" __declspec(dllexport) void TalonInit() {
    talon_boot();
}

BOOL WINAPI DllMain(HINSTANCE inst, DWORD reason, LPVOID) {
    if (reason != DLL_PROCESS_ATTACH) return TRUE;
    DisableThreadLibraryCalls(inst);
    talon_boot();
    return TRUE;
}
