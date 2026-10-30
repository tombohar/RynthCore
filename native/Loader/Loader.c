// ============================================================================
//  RynthCore.Loader.dll - native loader (docs/UNLOADABLE_ENGINE_PLAN.md, Phase 1)
//
//  Injected into acclient.exe by RynthCore.Injector / the launcher, which call the
//  RynthCoreInit export on a remote thread (same contract as the old NativeAOT loader).
//
//  Two engine hosts, chosen by looking at Runtime\RynthCore.Engine.dll:
//    - NativeAOT engine (a native DLL exporting RynthCoreEngineInit): staged to
//      .engine_loads\, LoadLibrary'd, never freed. Today's behaviour.
//    - Managed engine (a PE with a CLR header): the loader starts CoreCLR once from
//      Runtime\dotnet\ and hands the engine to RynthCore.Shim.dll (default ALC), which
//      loads each generation into a collectible AssemblyLoadContext and unloads it on
//      reload, so a reload gives its memory back.
//
//  Either way the loader owns the reload event, the file watcher, the address-space
//  guard, the reload cap (MaxHotReloads per client) and a MinHook facade (Hooks.c):
//  every hook is installed once, through a permanent stub, and a reload only re-points
//  the stub. For the managed engine it also sets up CoreCLR's crash dumps (Logs\dumps).
// ============================================================================

#include "Loader.h"

#include <stdio.h>
#include <stdarg.h>
#include <stdlib.h>

#define ENGINE_DLL_NAME        "RynthCore.Engine.dll"
#define ENGINE_INIT_EXPORT     "RynthCoreEngineInit"
#define ENGINE_SHUTDOWN_EXPORT "RynthCoreShutdown"
#define ENGINE_REFUSED_EXPORT  "RynthCoreReloadRefused"
#define ENGINE_MAXRELOADS_EXPORT "RynthCoreMaxHotReloads"
#define ENGINE_CAPPED_EXPORT   "RynthCoreReloadCapped"
#ifndef LOG_DIR                // the offline tests (tools\Loader*Tests) log into their own folder
#define LOG_DIR                "C:\\Games\\RynthCore\\Logs"
#define LOG_PARENT             "C:\\Games\\RynthCore"
#endif
#define STAGING_SUBDIR         ".engine_loads"
// Hot reloads one client takes before it refuses the rest (until it restarts). The engine
// says the real number (engine.json MaxHotReloads, RynthCoreMaxHotReloads export); this is
// for an engine without the export. 3 from 2026-10-11, after clients died with CoreCLR fatal
// error 0x80131506 at coreclr+0x256E19 after 4+ reloads in one process (2026-10-08..10). The
// 10-15 dump triage put those on the v1 SehTrampoline crash logger (ReversePInvokeBadTransition,
// fixed by db645ba on 10-12), not on reloading: none since, and address space was healthy at
// every crash. So 20 since 2026-10-15: a backstop, not a known CoreCLR limit.
#define DEFAULT_MAX_HOT_RELOADS 20
// CoreCLR's own crash dump (createdump.exe, DOTNET_DbgEnableMiniDump): clr_<pid>_<time>.dmp
// in Logs\dumps, newest DUMP_KEEP kept. With the heap (type 2) only while the volume keeps
// DUMP_HEAP_MIN_FREE_MB free (a heap dump of a client is ~1 GB); else a normal one (stacks).
#define DUMP_SUBDIR            "dumps"
#define DUMP_PREFIX            "clr_"
#define DUMP_KEEP              3
#define DUMP_HEAP_MIN_FREE_MB  3072
#define MIN_RELOAD_INTERVAL_MS 1500
// A managed engine gives its memory back on reload, so the guard only has to keep
// room for one more generation; the NativeAOT engine leaks every generation.
#define MIN_FREE_MB_AOT        1200
#define MIN_FREE_MB_CLR        500
// ...and (CoreCLR, 2026-10-15) one free block that size: the new generation's images and the
// GC's segments each need a contiguous range, so a fragmented space can fail a reload that
// the free total allows. Every crash triaged 10-15 still had >= 1.6 GB in one block.
#define MIN_LARGEST_MB_CLR     400

typedef unsigned int (__stdcall *engine_u32_fn)(void* param);
typedef unsigned int (__stdcall *engine_refused_fn)(int freeMb);
typedef int (__stdcall *engine_max_reloads_fn)(void);
typedef unsigned int (__stdcall *engine_capped_fn)(int reloads, int cap);

static volatile LONG g_initialized;
static char g_loaderDir[MAX_PATH];
static char g_enginePath[MAX_PATH];
static int g_engineIsManaged;
static HMODULE g_engineModule;          // NativeAOT host only
static int g_initCount;
static HANDLE g_reloadEvent;
static FILETIME g_canonicalWrite;
static volatile LONG g_reloadInFlight;
static volatile ULONGLONG g_lastReloadDone;
static CRITICAL_SECTION g_logLock;

// ---------------------------------------------------------------------------
// Logging: same per-client file and line format as the engine (LogPaths.cs).
// ---------------------------------------------------------------------------

void LoaderLog(const char* level, const char* fmt, ...)
{
    char msg[3072];
    va_list ap;
    va_start(ap, fmt);
    _vsnprintf_s(msg, sizeof msg, _TRUNCATE, fmt, ap);
    va_end(ap);

    SYSTEMTIME t;
    GetLocalTime(&t);
    char line[3300];
    int n = _snprintf_s(line, sizeof line, _TRUNCATE, "[%02d:%02d:%02d.%03d] [pid:%lu] [%s] [loader] %s\r\n",
        t.wHour, t.wMinute, t.wSecond, t.wMilliseconds, GetCurrentProcessId(), level, msg);
    if (n < 0) n = (int)strlen(line);

    char path[MAX_PATH];
    _snprintf_s(path, sizeof path, _TRUNCATE, LOG_DIR "\\RynthCore.%lu.log", GetCurrentProcessId());
    EnterCriticalSection(&g_logLock);
#ifdef LOG_PARENT
    CreateDirectoryA(LOG_PARENT, NULL);
#endif
    CreateDirectoryA(LOG_DIR, NULL);
    for (int attempt = 0; attempt < 4; attempt++) {
        HANDLE h = CreateFileA(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
        if (h != INVALID_HANDLE_VALUE) {
            DWORD written;
            WriteFile(h, line, (DWORD)n, &written, NULL);
            CloseHandle(h);
            break;
        }
        Sleep(5);
    }
    LeaveCriticalSection(&g_logLock);
    OutputDebugStringA(line);
}

// ---------------------------------------------------------------------------
// Address space
// ---------------------------------------------------------------------------

long LoaderFreeAddressSpaceMb(long* largestMb)
{
    SYSTEM_INFO si;
    GetSystemInfo(&si);
    unsigned char* p = (unsigned char*)si.lpMinimumApplicationAddress;
    unsigned char* end = (unsigned char*)si.lpMaximumApplicationAddress;
    unsigned long long total = 0, largest = 0;
    MEMORY_BASIC_INFORMATION mbi;
    while (p < end && VirtualQuery(p, &mbi, sizeof mbi) == sizeof mbi) {
        if (mbi.RegionSize == 0) break;
        if (mbi.State == MEM_FREE) {
            total += mbi.RegionSize;
            if (mbi.RegionSize > largest) largest = mbi.RegionSize;
        }
        p = (unsigned char*)mbi.BaseAddress + mbi.RegionSize;
    }
    if (largestMb) *largestMb = (long)(largest / (1024 * 1024));
    return (long)(total / (1024 * 1024));
}

// Why a hot reload would be refused for address space (LoaderFreeAddressSpaceMb's figures):
// 0 = room for it, 1 = too little free in all, 2 = a CoreCLR engine with enough free in all but
// no single free block of MIN_LARGEST_MB_CLR.
static int ReloadSpaceShort(int managed, long freeMb, long largestMb)
{
    if (freeMb < (managed ? MIN_FREE_MB_CLR : MIN_FREE_MB_AOT)) return 1;
    if (managed && largestMb < MIN_LARGEST_MB_CLR) return 2;
    return 0;
}

// ---------------------------------------------------------------------------
// Engine file: staging and kind
// ---------------------------------------------------------------------------

static int FileLastWrite(const char* path, FILETIME* out)
{
    WIN32_FILE_ATTRIBUTE_DATA d;
    if (!GetFileAttributesExA(path, GetFileExInfoStandard, &d)) return 0;
    *out = d.ftLastWriteTime;
    return 1;
}

// 1 = the file is a .NET assembly (PE with a CLR header), 0 = native, -1 = unreadable.
static int IsManagedPe(const char* path)
{
    HANDLE h = CreateFileA(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL,
        OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE) return -1;
    unsigned char buf[4096];
    DWORD read = 0;
    BOOL ok = ReadFile(h, buf, sizeof buf, &read, NULL);
    CloseHandle(h);
    if (!ok || read < sizeof(IMAGE_DOS_HEADER)) return -1;
    IMAGE_DOS_HEADER* dos = (IMAGE_DOS_HEADER*)buf;
    if (dos->e_magic != IMAGE_DOS_SIGNATURE || dos->e_lfanew <= 0 ||
        (DWORD)dos->e_lfanew + sizeof(IMAGE_NT_HEADERS32) > read) return -1;
    IMAGE_NT_HEADERS32* nt = (IMAGE_NT_HEADERS32*)(buf + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE) return -1;
    if (nt->OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR32_MAGIC) return 0;
    if (nt->OptionalHeader.NumberOfRvaAndSizes <= IMAGE_DIRECTORY_ENTRY_COM_DESCRIPTOR) return 0;
    return nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_COM_DESCRIPTOR].VirtualAddress != 0;
}

// 1 = the process with this id is running and started before `staged` (the time its file
// was created), so the file may be that client's live engine; 0 = gone, or a newer
// process that only reuses the id.
static int ProcessOwnsStagedFile(DWORD pid, const FILETIME* staged)
{
    HANDLE h = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    if (!h) return GetLastError() == ERROR_ACCESS_DENIED;   // running, can't look: leave it
    int owns = WaitForSingleObject(h, 0) != WAIT_OBJECT_0;
    FILETIME created, exited, kernel, user;
    if (owns && GetProcessTimes(h, &created, &exited, &kernel, &user) && CompareFileTime(&created, staged) > 0)
        owns = 0;
    CloseHandle(h);
    return owns;
}

// Deletes the staged engines (.dll + .pdb) no client needs: this client's older
// generations, and the files of clients that have closed. Never another live client's:
// until 2026-10-09 every file went, and a client staging at the same moment (every
// watcher fires within ~1.5 s of an engine publish) lost its file mid-load - a missing
// file, or worse a "loaded" image with broken IL (ops/overnight/2026-10-08-gen4-corrupt-load.md).
static void CleanStaleStagingFiles(const char* stagingDir, int generation)
{
    char pattern[MAX_PATH];
    _snprintf_s(pattern, sizeof pattern, _TRUNCATE, "%s\\RynthCore.Engine.p*.gen*.*", stagingDir);
    WIN32_FIND_DATAA fd;
    HANDLE f = FindFirstFileA(pattern, &fd);
    if (f == INVALID_HANDLE_VALUE) return;
    DWORD self = GetCurrentProcessId();
    do {
        unsigned long pid = 0;
        int gen = 0;
        if (sscanf_s(fd.cFileName, "RynthCore.Engine.p%lu.gen%d.", &pid, &gen) != 2) continue;
        int stale = pid == self ? gen < generation : !ProcessOwnsStagedFile((DWORD)pid, &fd.ftCreationTime);
        if (!stale) continue;
        char p[MAX_PATH];
        _snprintf_s(p, sizeof p, _TRUNCATE, "%s\\%s", stagingDir, fd.cFileName);
        DeleteFileA(p);   // fails for files still mapped or guarded: fine
    } while (FindNextFileA(f, &fd));
    FindClose(f);
}

// Writes a copy of `src` to `dst` and returns a handle that keeps `dst` from being deleted
// (no FILE_SHARE_DELETE) until it is closed; INVALID_HANDLE_VALUE on failure. Held through
// the engine load, so nothing can pull the file out from under CoreCLR/LoadLibrary (a
// guard like this took 100 of 100 racing deletes in the offline repro, every load clean).
// The guard only reads, so the loader's own read-only opens of the file still succeed.
static HANDLE CopyGuarded(const char* src, const char* dst)
{
    HANDLE in = CreateFileA(src, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL,
        OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (in == INVALID_HANDLE_VALUE) return INVALID_HANDLE_VALUE;
    HANDLE out = CreateFileA(dst, GENERIC_WRITE, FILE_SHARE_READ, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (out == INVALID_HANDLE_VALUE) {
        DWORD e = GetLastError();
        CloseHandle(in);
        SetLastError(e);
        return INVALID_HANDLE_VALUE;
    }
    static unsigned char buf[256 * 1024];
    DWORD read = 0, written = 0;
    BOOL ok = TRUE;
    while (ok && (ok = ReadFile(in, buf, sizeof buf, &read, NULL)) && read > 0)
        ok = WriteFile(out, buf, read, &written, NULL) && written == read;
    DWORD e = GetLastError();
    CloseHandle(in);
    // The read-only guard opens before the writer closes, so there's no gap to delete in.
    HANDLE guard = ok ? CreateFileA(dst, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL,
        OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL) : INVALID_HANDLE_VALUE;
    if (guard == INVALID_HANDLE_VALUE && ok) e = GetLastError();
    CloseHandle(out);
    if (guard == INVALID_HANDLE_VALUE) {
        DeleteFileA(dst);
        SetLastError(e);
    }
    return guard;
}

// Copies the canonical engine to .engine_loads\RynthCore.Engine.p<pid>.gen<N>.dll so the
// canonical file is never locked (a developer can copy over it) and every generation is
// a distinct module/assembly path. *guard keeps the copy undeletable until the caller
// closes it (after the load).
static int StageEngine(int generation, char* stagedPath, size_t cap, HANDLE* guard)
{
    char stagingDir[MAX_PATH];
    _snprintf_s(stagingDir, sizeof stagingDir, _TRUNCATE, "%s\\" STAGING_SUBDIR, g_loaderDir);
    CreateDirectoryA(stagingDir, NULL);
    CleanStaleStagingFiles(stagingDir, generation);
    _snprintf_s(stagedPath, cap, _TRUNCATE, "%s\\RynthCore.Engine.p%lu.gen%d.dll",
        stagingDir, GetCurrentProcessId(), generation);
    *guard = CopyGuarded(g_enginePath, stagedPath);
    if (*guard == INVALID_HANDLE_VALUE) {
        LoaderLog("ERR", "StageEngine: copy to %s failed (error %lu)", stagedPath, GetLastError());
        return 0;
    }
    if (g_engineIsManaged) {
        // The debug symbols travel with the assembly so stack traces keep line numbers.
        char srcPdb[MAX_PATH], dstPdb[MAX_PATH];
        _snprintf_s(srcPdb, sizeof srcPdb, _TRUNCATE, "%s\\RynthCore.Engine.pdb", g_loaderDir);
        _snprintf_s(dstPdb, sizeof dstPdb, _TRUNCATE, "%.*s.pdb", (int)(strlen(stagedPath) - 4), stagedPath);
        CopyFileA(srcPdb, dstPdb, FALSE);
    }
    LoaderLog("INF", "Staged engine for gen %d at %s", generation, stagedPath);
    return 1;
}

// ---------------------------------------------------------------------------
// CoreCLR crash dumps
// ---------------------------------------------------------------------------

typedef struct DumpFile {
    FILETIME written;
    char name[MAX_PATH];
} DumpFile;

static int CompareNewestFirst(const void* a, const void* b)
{
    return CompareFileTime(&((const DumpFile*)b)->written, &((const DumpFile*)a)->written);
}

// Deletes all but the newest `keep` clr_*.dmp in `dir` (other dumps, e.g. the engine's
// hang_*.dmp, are left alone). Returns how many it deleted. Init thread only.
static int PruneCrashDumps(const char* dir, int keep)
{
    static DumpFile files[512];
    char pattern[MAX_PATH];
    _snprintf_s(pattern, sizeof pattern, _TRUNCATE, "%s\\" DUMP_PREFIX "*.dmp", dir);
    WIN32_FIND_DATAA fd;
    HANDLE f = FindFirstFileA(pattern, &fd);
    if (f == INVALID_HANDLE_VALUE) return 0;
    int count = 0;
    do {
        if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) continue;
        files[count].written = fd.ftLastWriteTime;
        strcpy_s(files[count].name, sizeof files[count].name, fd.cFileName);
        count++;
    } while (count < (int)(sizeof files / sizeof files[0]) && FindNextFileA(f, &fd));
    FindClose(f);
    qsort(files, (size_t)count, sizeof files[0], CompareNewestFirst);
    int deleted = 0;
    for (int i = keep < 0 ? 0 : keep; i < count; i++) {
        char p[MAX_PATH];
        _snprintf_s(p, sizeof p, _TRUNCATE, "%s\\%s", dir, files[i].name);
        if (DeleteFileA(p)) deleted++;   // fails for a dump still being written: fine
    }
    return deleted;
}

// The DOTNET_DbgMiniDumpType for a volume with `freeBytes` free: 2 (MiniDumpWithPrivateReadWrite
// Memory: the GC heap and every private page, what a CoreCLR fatal error needs) when there is
// room, else 1 (normal: threads and stacks, a few MB). Never 4 (full): it adds the mapped
// images, which the debugger reads from disk anyway, for a larger file.
static int CrashDumpType(unsigned long long freeBytes)
{
    return freeBytes >= (unsigned long long)DUMP_HEAP_MIN_FREE_MB * 1024 * 1024 ? 2 : 1;
}

// Before CoreCLR starts (it reads these once, in coreclr_initialize): a fatal runtime error
// (FailFast, 0x80131506...) runs createdump.exe from the runtime folder and writes
// <dumpDir>\clr_<pid>_<time>.dmp. Those never reach WER, so before this they left nothing.
// Returns the dump type set, or 0 when the environment already configures it (a test or a
// developer's own choice, including DOTNET_DbgEnableMiniDump=0 to turn it off).
static int ConfigureCrashDumps(const char* dumpDir, const char* runtimeDir)
{
    char existing[64];
    if (GetEnvironmentVariableA("DOTNET_DbgEnableMiniDump", existing, sizeof existing) > 0 ||
        GetEnvironmentVariableA("COMPlus_DbgEnableMiniDump", existing, sizeof existing) > 0) {
        LoaderLog("INF", "CrashDumps: DbgEnableMiniDump=%s is already set in the environment; left as it is.", existing);
        return 0;
    }
    CreateDirectoryA(dumpDir, NULL);
    int pruned = PruneCrashDumps(dumpDir, DUMP_KEEP);
    ULARGE_INTEGER freeBytes;
    if (!GetDiskFreeSpaceExA(dumpDir, &freeBytes, NULL, NULL)) freeBytes.QuadPart = 0;
    int type = CrashDumpType(freeBytes.QuadPart);

    // %t is createdump's own specifier (time of the dump); the pid is filled in here.
    char name[MAX_PATH];
    _snprintf_s(name, sizeof name, _TRUNCATE, "%s\\" DUMP_PREFIX "%lu_%%t.dmp", dumpDir, GetCurrentProcessId());
    SetEnvironmentVariableA("DOTNET_DbgEnableMiniDump", "1");
    SetEnvironmentVariableA("DOTNET_DbgMiniDumpType", type == 2 ? "2" : "1");
    SetEnvironmentVariableA("DOTNET_DbgMiniDumpName", name);

    LoaderLog("INF", "CrashDumps: a CoreCLR fatal error writes %s (type %d, %s; %llu MB free; newest %d kept, %d older deleted).",
        name, type, type == 2 ? "with heap" : "stacks only, low disk", freeBytes.QuadPart / (1024 * 1024), DUMP_KEEP, pruned);
    char createdump[MAX_PATH];
    _snprintf_s(createdump, sizeof createdump, _TRUNCATE, "%s\\createdump.exe", runtimeDir);
    if (GetFileAttributesA(createdump) == INVALID_FILE_ATTRIBUTES)
        LoaderLog("WRN", "CrashDumps: %s is missing, so a CoreCLR crash still leaves no dump. "
            "Run scripts\\Publish-EngineCoreClr.ps1 (full, not -EngineOnly) to put it back.", createdump);
    return type;
}

// ---------------------------------------------------------------------------
// Engine calls, whichever host
// ---------------------------------------------------------------------------

static void* EngineExport(const char* name)
{
    if (g_engineIsManaged) return ClrEngineExport(name);
    return g_engineModule ? (void*)GetProcAddress(g_engineModule, name) : NULL;
}

// 1 = a client that has done `reloadsDone` hot reloads takes no more; `cap` < 0 = no cap.
static int ReloadCapReached(int reloadsDone, int cap)
{
    return cap >= 0 && reloadsDone >= cap;
}

// The current engine's MaxHotReloads (engine.json), or the default for an engine without
// the export.
static int CurrentReloadCap(void)
{
    engine_max_reloads_fn f = (engine_max_reloads_fn)EngineExport(ENGINE_MAXRELOADS_EXPORT);
    return f ? f() : DEFAULT_MAX_HOT_RELOADS;
}

static unsigned int LoadAndInitEngine(void)
{
    g_initCount++;
    HooksBeginGeneration(g_initCount);

    char staged[MAX_PATH];
    HANDLE guard;
    if (!StageEngine(g_initCount, staged, sizeof staged, &guard)) return 11;
    if (!FileLastWrite(g_enginePath, &g_canonicalWrite)) GetSystemTimeAsFileTime(&g_canonicalWrite);

    // The guard stays open until the load returns; after that the image mapping holds the file.
    if (g_engineIsManaged) {
        int rc = ClrLoadEngine(g_initCount, staged);
        CloseHandle(guard);
        if (rc != 0) {
            LoaderLog("ERR", "Managed engine gen %d failed to load (rc=%d)", g_initCount, rc);
            return 4;
        }
    } else {
        HMODULE m = LoadLibraryA(staged);
        DWORD e = GetLastError();
        CloseHandle(guard);
        if (!m) {
            LoaderLog("ERR", "LoadLibrary failed for engine (path=%s, error %lu)", staged, e);
            return 4;
        }
        g_engineModule = m;
        LoaderLog("INF", "Engine module loaded at 0x%08p from %s", (void*)m, staged);
    }

    engine_u32_fn init = (engine_u32_fn)EngineExport(ENGINE_INIT_EXPORT);
    if (!init) {
        LoaderLog("ERR", "Engine missing export '%s'", ENGINE_INIT_EXPORT);
        return 5;
    }
    unsigned int rc = init((void*)(INT_PTR)g_initCount);
    // The hooks the engine installed during init go in now, in one freeze, before the
    // injector resumes AC's main thread (first launch) or the new generation runs.
    HooksFlush();
    LoaderLog("INF", ENGINE_INIT_EXPORT "(initCount=%d) returned %u.", g_initCount, rc);
    return rc;
}

static void Reload(void)
{
    long largest;
    long freeBefore = LoaderFreeAddressSpaceMb(&largest);
    LoaderLog("INF", "Reload requested (gen %d, %ld MB free, largest %ld MB).", g_initCount, freeBefore, largest);

    engine_u32_fn shutdown = (engine_u32_fn)EngineExport(ENGINE_SHUTDOWN_EXPORT);
    if (shutdown) {
        unsigned int rc = shutdown(NULL);
        LoaderLog("INF", ENGINE_SHUTDOWN_EXPORT " returned %u.", rc);
    } else {
        LoaderLog("WRN", "Engine has no '%s' export; skipping graceful shutdown.", ENGINE_SHUTDOWN_EXPORT);
    }

    // Whatever the engine left enabled now passes straight through to AC.
    HooksPassThroughAll();
    ServicesReleaseAll();
    Sleep(150);

    if (g_engineIsManaged) {
        ClrUnloadEngine();
    } else if (g_engineModule) {
        // A NativeAOT module can't be freed (its runtime keeps threads with frames in it).
        LoaderLog("INF", "Reload: NativeAOT engine 0x%08p left mapped (can't unload).", (void*)g_engineModule);
        g_engineModule = NULL;
    }

    unsigned int rc = LoadAndInitEngine();
    long freeAfter = LoaderFreeAddressSpaceMb(&largest);
    LoaderLog(rc == 0 ? "INF" : "ERR", "Reload: %s (gen %d, %ld MB free, %+ld MB vs before the reload).",
        rc == 0 ? "complete" : "re-init FAILED", g_initCount, freeAfter, freeAfter - freeBefore);
    int cap = CurrentReloadCap();
    if (ReloadCapReached(g_initCount - 1, cap))
        LoaderLog("INF", "Reload: hot reload %d of %d (engine.json MaxHotReloads); this client takes no more, "
            "a newer engine needs a client restart.", g_initCount - 1, cap);
}

// ---------------------------------------------------------------------------
// Reload triggers: the engine's named event, and the canonical DLL changing on disk
// ---------------------------------------------------------------------------

static DWORD WINAPI ReloadWatcher(LPVOID unused)
{
    (void)unused;
    for (;;) {
        if (WaitForSingleObject(g_reloadEvent, INFINITE) != WAIT_OBJECT_0) { Sleep(100); continue; }

        ULONGLONG last = g_lastReloadDone;
        if (last != 0 && GetTickCount64() - last < MIN_RELOAD_INTERVAL_MS) {
            LoaderLog("INF", "ReloadWatcher: signal %llu ms after the last reload; coalesced.", GetTickCount64() - last);
            continue;
        }

        // The reload cap: nothing is shut down, unloaded or staged, so the client keeps running
        // on this generation and its staged file stays as it is (the next client start cleans
        // it up once this process is gone, CleanStaleStagingFiles).
        int reloadsDone = g_initCount - 1;
        int cap = CurrentReloadCap();
        if (ReloadCapReached(reloadsDone, cap)) {
            LoaderLog("WRN", "ReloadWatcher: reload REFUSED - this client has hot reloaded %d time(s), the cap "
                "(engine.json MaxHotReloads) is %d. Staying on engine gen %d; restart the client to load the "
                "new engine. (The cap is a backstop against piling up hot reloads in one client.)",
                reloadsDone, cap, g_initCount);
            engine_capped_fn capped = (engine_capped_fn)EngineExport(ENGINE_CAPPED_EXPORT);
            if (capped) capped(reloadsDone, cap);
            continue;
        }

        long minFree = g_engineIsManaged ? MIN_FREE_MB_CLR : MIN_FREE_MB_AOT;
        long largestMb;
        long freeMb = LoaderFreeAddressSpaceMb(&largestMb);
        int shortOf = ReloadSpaceShort(g_engineIsManaged, freeMb, largestMb);
        if (shortOf == 1)
            LoaderLog("WRN", "ReloadWatcher: reload REFUSED - only %ld MB of address space free (need %ld MB). "
                "Restart the client to load the new engine.", freeMb, minFree);
        else if (shortOf == 2)
            LoaderLog("WRN", "ReloadWatcher: reload REFUSED - the largest free block of address space is only %ld MB "
                "(need %ld MB; %ld MB free in all). Restart the client to load the new engine.",
                largestMb, (long)MIN_LARGEST_MB_CLR, freeMb);
        if (shortOf) {
            // The engine is told the figure that fell short; it says so in chat.
            engine_refused_fn refused = (engine_refused_fn)EngineExport(ENGINE_REFUSED_EXPORT);
            if (refused) refused((int)(shortOf == 1 ? freeMb : largestMb));
            continue;
        }

        if (InterlockedCompareExchange(&g_reloadInFlight, 1, 0) != 0) {
            LoaderLog("INF", "ReloadWatcher: reload already in flight; coalesced.");
            continue;
        }
        Reload();
        g_lastReloadDone = GetTickCount64();
        InterlockedExchange(&g_reloadInFlight, 0);

        int drained = 0;
        while (WaitForSingleObject(g_reloadEvent, 0) == WAIT_OBJECT_0) drained++;
        if (drained) LoaderLog("INF", "ReloadWatcher: drained %d signal(s) received during the reload.", drained);
    }
}

static DWORD WINAPI FileWatcher(LPVOID unused)
{
    (void)unused;
    for (;;) {
        Sleep(1000);
        FILETIME now;
        if (!FileLastWrite(g_enginePath, &now) || CompareFileTime(&now, &g_canonicalWrite) <= 0) continue;
        // Let the writer (cp, dotnet publish) finish before staging from it.
        Sleep(500);
        FILETIME stable;
        if (!FileLastWrite(g_enginePath, &stable) || CompareFileTime(&stable, &now) != 0) continue;
        LoaderLog("INF", "FileWatcher: %s changed; signaling reload.", g_enginePath);
        g_canonicalWrite = stable;
        SetEvent(g_reloadEvent);
    }
}

// ---------------------------------------------------------------------------
// Exports
// ---------------------------------------------------------------------------

const char* LoaderDirectory(void) { return g_loaderDir; }
int LoaderGeneration(void) { return g_initCount; }

unsigned int __stdcall RynthCoreInit(void* param)
{
    (void)param;
    if (InterlockedCompareExchange(&g_initialized, 1, 0) != 0) {
        LoaderLog("WRN", "RynthCoreInit called twice; ignoring the second call.");
        return 1;
    }

    HMODULE self = NULL;
    GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
        (LPCSTR)&RynthCoreInit, &self);
    GetModuleFileNameA(self, g_loaderDir, MAX_PATH);
    char* slash = strrchr(g_loaderDir, '\\');
    if (slash) *slash = 0;

    LoaderLog("INF", "RynthCoreInit (native loader %s) starting in %s.", LOADER_VERSION, g_loaderDir);
    {
        // Test clients (tools\TestClient, scripts\Test-Clr*.ps1) say so first, so monitors can skip them.
        char tag[128];
        if (GetEnvironmentVariableA("RYNTHCORE_TEST_CLIENT", tag, sizeof tag) > 0)
            LoaderLog("INF", "TestClient: %s", tag);
    }

    _snprintf_s(g_enginePath, sizeof g_enginePath, _TRUNCATE, "%s\\" ENGINE_DLL_NAME, g_loaderDir);
    int managed = IsManagedPe(g_enginePath);
    if (managed < 0) {
        LoaderLog("ERR", "FATAL: engine DLL not found or unreadable at %s", g_enginePath);
        return 3;
    }
    g_engineIsManaged = managed;
    long largest;
    long freeMb = LoaderFreeAddressSpaceMb(&largest);
    LoaderLog("INF", "Engine is %s; %ld MB of address space free (largest %ld MB).",
        managed ? "a managed assembly: hosting CoreCLR" : "NativeAOT", freeMb, largest);

    if (!HooksInit(g_loaderDir)) {
        LoaderLog("ERR", "FATAL: MinHook could not be loaded from %s", g_loaderDir);
        return 6;
    }
    if (managed) {
        char dumpDir[MAX_PATH], runtimeDir[MAX_PATH];
        _snprintf_s(dumpDir, sizeof dumpDir, _TRUNCATE, LOG_DIR "\\" DUMP_SUBDIR);
        _snprintf_s(runtimeDir, sizeof runtimeDir, _TRUNCATE, "%s\\dotnet", g_loaderDir);
        ConfigureCrashDumps(dumpDir, runtimeDir);
        int rc = ClrStart(g_loaderDir);
        if (rc != 0) {
            LoaderLog("ERR", "FATAL: CoreCLR did not start (rc=%d)", rc);
            return 7;
        }
    }

    unsigned int rc = LoadAndInitEngine();
    if (rc != 0) return rc;

    char eventName[128];
    _snprintf_s(eventName, sizeof eventName, _TRUNCATE, "Local\\RynthCore.Engine.RequestReload.p%lu", GetCurrentProcessId());
    g_reloadEvent = CreateEventA(NULL, FALSE, FALSE, eventName);
    if (!g_reloadEvent) {
        LoaderLog("WRN", "CreateEvent failed (error %lu); reload disabled.", GetLastError());
        return 0;
    }
    HANDLE t = CreateThread(NULL, 0, ReloadWatcher, NULL, 0, NULL);
    if (t) CloseHandle(t);
    t = CreateThread(NULL, 0, FileWatcher, NULL, 0, NULL);
    if (t) CloseHandle(t);
    LoaderLog("INF", "Reload watcher armed on '%s'; file watcher on '%s' (poll 1 s).", eventName, g_enginePath);
    int cap = CurrentReloadCap();
    if (cap < 0) LoaderLog("INF", "Hot reloads: no cap (engine.json MaxHotReloads %d).", cap);
    else LoaderLog("INF", "Hot reloads: at most %d in this client (engine.json MaxHotReloads); after that a new engine needs a restart.", cap);
    return 0;
}

BOOL WINAPI DllMain(HINSTANCE h, DWORD reason, LPVOID reserved)
{
    (void)h; (void)reserved;
    if (reason == DLL_PROCESS_ATTACH) {
        InitializeCriticalSection(&g_logLock);
        DisableThreadLibraryCalls(h);
    }
    return TRUE;
}
