// Talon.Boot — DQX loose-file override payload (VFS-semantic hook).
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

#include "vfs_hook.h"
#include "hook_manager.h"
#include "log.h"

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <string.h>
#include <stdio.h>
#include <stdint.h>

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
static TalonHook*         g_vfs_hook = nullptr;   // our registration in the hook manager

// Opt-in diagnostic (TALON_VFS_CENSUS): log the path of every resource the game
// requests through Vfs_LoadResource, capped so an asset-heavy boot can't flood
// the log. Useful for discovering the archive-relative paths a mod would mirror.
static bool               g_census = false;
static volatile LONG      g_census_count = 0;
static const LONG         kCensusCap = 400;

void vfs_set_override_dir(const char* dir) {
    if (!dir) { g_override_dir[0] = '\0'; return; }
    strncpy(g_override_dir, dir, sizeof(g_override_dir) - 1);
    g_override_dir[sizeof(g_override_dir) - 1] = '\0';
}

void vfs_set_census(bool enabled) { g_census = enabled; }

// Our replacement. Declared __fastcall so we capture the incoming ecx (`this`);
// edx is unused (the game's __thiscall never sets it) and the remaining four
// params land on the stack exactly as Vfs_LoadResource's stack args do, so the
// callee-cleanup of 0x10 bytes matches the original.
static void* __fastcall hook_VfsLoadResource(void* thisPtr, void* /*edx*/,
                                             const char* path, int expansion,
                                             int mount, int mustBeZero) {
    // In-flight guard so hook_remove() can drain safely if this hook is ever torn down.
    HookGuard guard(g_vfs_hook);

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

const uint32_t VFS_LOADRESOURCE_RVA = 0xFD0E0;

// 53 8B DC 83 ?? ?? 83 ?? ?? 83 ?? ?? 55 8B ?? ?? 89 ?? ?? ?? 8B EC B8 ?? ?? ?? ?? E8
//   push ebx; mov ebx,esp; (sub/and/add esp,imm8)x3; push ebp; mov ebp,[ebx+4];
//   mov [esp+4],ebp; mov ebp,esp; mov eax,imm32; call __chkstk
static const int VFS_SIG[] = {
    0x53, 0x8B, 0xDC, 0x83, -1, -1, 0x83, -1, -1, 0x83, -1, -1,
    0x55, 0x8B, -1, -1, 0x89, -1, -1, -1, 0x8B, 0xEC, 0xB8, -1, -1, -1, -1, 0xE8
};
const int VFS_SIG_LEN = (int)(sizeof(VFS_SIG) / sizeof(VFS_SIG[0]));

bool sig_matches(const uint8_t* p) {
    for (int i = 0; i < VFS_SIG_LEN; i++)
        if (VFS_SIG[i] >= 0 && p[i] != (uint8_t)VFS_SIG[i]) return false;
    return true;
}

bool region_readable(const uint8_t* addr, size_t len) {
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

// ── Hook registration ────────────────────────────────────────────────────────
// Register the override hook with the manager; the unpack barrier installs it (via
// hook_install_all) once .text is unpacked. The target VA is a fixed linear address
// (base + RVA), valid to register even though the bytes are still zero-filled here —
// only the later MH_CreateHook needs the prologue present.

void vfs_register() {
    uint8_t* base   = (uint8_t*)GetModuleHandleA(nullptr);
    void*    target = base + VFS_LOADRESOURCE_RVA;
    g_vfs_hook = hook_register("Vfs_LoadResource", target,
                               (void*)hook_VfsLoadResource, (void**)&g_orig_VfsLoadResource);
    if (g_vfs_hook)
        dbg("[boot] registered Vfs_LoadResource hook (target=%p, override dir=%s)\n",
            target, g_override_dir);
    else
        dbg("[boot] vfs_register: hook_register failed\n");
}
