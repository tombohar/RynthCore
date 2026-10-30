// Offline checks for Loader.c's reload cap and CoreCLR crash-dump setup. Built with the real
// Loader.c included, so these are the shipped functions. Logs go to .\logs (LOG_DIR below),
// never C:\Games. This exe stands in for the engine: it exports RynthCoreMaxHotReloads and
// RynthCoreReloadCapped (Run-Tests.ps1 links them undecorated), and the loader finds them with
// GetProcAddress, the NativeAOT-host path.
#define LOG_DIR "logs"
#include "Loader.c"

static int fails;
#define CHECK(c, ...) do { if (c) printf("ok   "); else { printf("FAIL "); fails++; } printf(__VA_ARGS__); printf("\n"); } while (0)

// --- the fake engine ---------------------------------------------------------------

static volatile LONG fakeCap = 3;
static volatile LONG cappedCalls, cappedReloads = -99, cappedCap = -99;

int __stdcall FakeMaxHotReloads(void) { return fakeCap; }

unsigned int __stdcall FakeReloadCapped(int reloads, int cap)
{
    cappedReloads = reloads;
    cappedCap = cap;
    InterlockedIncrement(&cappedCalls);
    return 0;
}

// --- helpers -------------------------------------------------------------------------

static void MakeFile(const char* dir, const char* name, int ageMinutes)
{
    char p[MAX_PATH];
    _snprintf_s(p, sizeof p, _TRUNCATE, "%s\\%s", dir, name);
    HANDLE h = CreateFileA(p, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    FILETIME now;
    GetSystemTimeAsFileTime(&now);
    ULARGE_INTEGER t;
    t.LowPart = now.dwLowDateTime;
    t.HighPart = now.dwHighDateTime;
    t.QuadPart -= (ULONGLONG)ageMinutes * 60 * 10000000ULL;
    FILETIME ft = { t.LowPart, t.HighPart };
    SetFileTime(h, NULL, NULL, &ft);
    CloseHandle(h);
}

static int FileExists(const char* dir, const char* name)
{
    char p[MAX_PATH];
    _snprintf_s(p, sizeof p, _TRUNCATE, "%s\\%s", dir, name);
    return GetFileAttributesA(p) != INVALID_FILE_ATTRIBUTES;
}

static int CountFiles(const char* dir, const char* pattern)
{
    char p[MAX_PATH];
    _snprintf_s(p, sizeof p, _TRUNCATE, "%s\\%s", dir, pattern);
    WIN32_FIND_DATAA fd;
    HANDLE f = FindFirstFileA(p, &fd);
    if (f == INVALID_HANDLE_VALUE) return 0;
    int n = 0;
    do { if (!(fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)) n++; } while (FindNextFileA(f, &fd));
    FindClose(f);
    return n;
}

static void Env(const char* name, char* out, DWORD cap)
{
    out[0] = 0;
    if (GetEnvironmentVariableA(name, out, cap) == 0) out[0] = 0;
}

static int WaitForCalls(LONG want)
{
    for (int i = 0; i < 200 && cappedCalls < want; i++) Sleep(10);
    return cappedCalls >= want;
}

int main(void)
{
    InitializeCriticalSection(&g_logLock);
    char root[MAX_PATH];
    GetFullPathNameA("work", sizeof root, root, NULL);
    CreateDirectoryA(root, NULL);
    CreateDirectoryA(LOG_DIR, NULL);
    DWORD self = GetCurrentProcessId();

    // 1. The cap rule.
    CHECK(!ReloadCapReached(0, 3) && !ReloadCapReached(2, 3), "under the cap: 0 and 2 reloads of 3 allowed");
    CHECK(ReloadCapReached(3, 3) && ReloadCapReached(4, 3), "at/over the cap: the 4th reload (3 done, cap 3) refused");
    CHECK(ReloadCapReached(0, 0), "cap 0: no hot reloads at all");
    CHECK(!ReloadCapReached(0, -1) && !ReloadCapReached(1000, -1), "negative cap: no cap");

    // 1b. The address-space guard: free in all, and (CoreCLR) the largest free block.
    CHECK(ReloadSpaceShort(1, 1800, 1600) == 0, "CoreCLR, 1800 MB free, 1600 MB block: room");
    CHECK(ReloadSpaceShort(1, MIN_FREE_MB_CLR, MIN_LARGEST_MB_CLR) == 0, "CoreCLR, exactly both floors: room");
    CHECK(ReloadSpaceShort(1, MIN_FREE_MB_CLR - 1, 300) == 1, "CoreCLR, under the free floor: short in all (checked first)");
    CHECK(ReloadSpaceShort(1, 1200, MIN_LARGEST_MB_CLR - 1) == 2, "CoreCLR, 1200 MB free but a 399 MB largest block: refused");
    CHECK(ReloadSpaceShort(0, 1500, 100) == 0, "NativeAOT: no largest-block floor");
    CHECK(ReloadSpaceShort(0, MIN_FREE_MB_AOT - 1, 1000) == 1, "NativeAOT, under its free floor: short in all");
    {
        long largest = -1;
        long freeNow = LoaderFreeAddressSpaceMb(&largest);
        CHECK(largest > 0 && largest <= freeNow, "this process: largest block %ld MB of %ld MB free", largest, freeNow);
    }

    // 2. Where the cap comes from: the engine's export, else the default.
    g_engineIsManaged = 0;
    g_engineModule = NULL;
    CHECK(CurrentReloadCap() == DEFAULT_MAX_HOT_RELOADS && DEFAULT_MAX_HOT_RELOADS == 20,
        "no engine export: the default (%d)", CurrentReloadCap());
    g_engineModule = GetModuleHandleA(NULL);
    CHECK(EngineExport(ENGINE_MAXRELOADS_EXPORT) && EngineExport(ENGINE_CAPPED_EXPORT), "the fake engine's exports resolve");
    fakeCap = 7;
    CHECK(CurrentReloadCap() == 7, "the engine's MaxHotReloads is used (%d)", CurrentReloadCap());
    fakeCap = 3;

    // 3. A refused reload, through the real ReloadWatcher: 3 reloads done (gen 4), cap 3.
    //    Nothing may be shut down, unloaded or staged, and the engine is told each time.
    strcpy_s(g_loaderDir, sizeof g_loaderDir, root);
    _snprintf_s(g_enginePath, sizeof g_enginePath, _TRUNCATE, "%s\\" ENGINE_DLL_NAME, root);
    char staging[MAX_PATH];
    _snprintf_s(staging, sizeof staging, _TRUNCATE, "%s\\" STAGING_SUBDIR, root);
    CreateDirectoryA(staging, NULL);
    char gen4[96], gen5[96], gen3[96];
    _snprintf_s(gen3, sizeof gen3, _TRUNCATE, "RynthCore.Engine.p%lu.gen3.dll", self);
    _snprintf_s(gen4, sizeof gen4, _TRUNCATE, "RynthCore.Engine.p%lu.gen4.dll", self);
    _snprintf_s(gen5, sizeof gen5, _TRUNCATE, "RynthCore.Engine.p%lu.gen5.dll", self);
    MakeFile(staging, gen3, 5);   // an older generation a real reload would clean up
    MakeFile(staging, gen4, 1);   // the running generation
    g_initCount = 4;
    g_reloadEvent = CreateEventA(NULL, FALSE, FALSE, NULL);
    HANDLE watcher = CreateThread(NULL, 0, ReloadWatcher, NULL, 0, NULL);

    SetEvent(g_reloadEvent);
    int told = WaitForCalls(1);
    Sleep(50);
    CHECK(told && cappedReloads == 3 && cappedCap == 3, "refused: the engine is told (reloads=%ld, cap=%ld)", cappedReloads, cappedCap);
    CHECK(g_initCount == 4 && g_reloadInFlight == 0 && g_lastReloadDone == 0, "refused: still gen 4, no reload ran");
    CHECK(FileExists(staging, gen4) && FileExists(staging, gen3) && !FileExists(staging, gen5),
        "refused: staging untouched (gen 4 and gen 3 kept, no gen 5 staged)");

    // A second publish: refused again, told again (the engine decides what goes to chat).
    SetEvent(g_reloadEvent);
    CHECK(WaitForCalls(2) && g_initCount == 4, "a second signal: refused again, told again (%ld calls)", cappedCalls);

    // Cap 0 refuses the first reload too.
    g_initCount = 1;
    fakeCap = 0;
    SetEvent(g_reloadEvent);
    CHECK(WaitForCalls(3) && cappedReloads == 0 && cappedCap == 0 && g_initCount == 1, "cap 0: the first reload refused");
    fakeCap = 3;
    (void)watcher;   // runs until the process exits

    // 4. Dump type for the free space.
    CHECK(CrashDumpType(10ULL << 30) == 2, "10 GB free: dump with heap (type 2)");
    CHECK(CrashDumpType((unsigned long long)DUMP_HEAP_MIN_FREE_MB << 20) == 2, "exactly the floor: type 2");
    CHECK(CrashDumpType(((unsigned long long)DUMP_HEAP_MIN_FREE_MB << 20) - 1) == 1, "below the floor: normal dump (type 1)");
    CHECK(CrashDumpType(0) == 1, "unknown free space (0): type 1");

    // 5. Pruning: the newest 3 clr_*.dmp stay; other dumps and folders are left alone.
    char dumps[MAX_PATH];
    _snprintf_s(dumps, sizeof dumps, _TRUNCATE, "%s\\dumps", root);
    CreateDirectoryA(dumps, NULL);
    MakeFile(dumps, "clr_100_1.dmp", 50);
    MakeFile(dumps, "clr_101_2.dmp", 40);
    MakeFile(dumps, "clr_102_3.dmp", 30);
    MakeFile(dumps, "clr_103_4.dmp", 20);
    MakeFile(dumps, "clr_104_5.dmp", 10);
    MakeFile(dumps, "hang_1_old.dmp", 500);
    MakeFile(dumps, "clr_notes.txt", 600);
    char sub[MAX_PATH];
    _snprintf_s(sub, sizeof sub, _TRUNCATE, "%s\\clr_folder.dmp", dumps);
    CreateDirectoryA(sub, NULL);
    int deleted = PruneCrashDumps(dumps, DUMP_KEEP);
    CHECK(deleted == 2 && !FileExists(dumps, "clr_100_1.dmp") && !FileExists(dumps, "clr_101_2.dmp"),
        "the 2 oldest clr dumps deleted (%d)", deleted);
    CHECK(FileExists(dumps, "clr_102_3.dmp") && FileExists(dumps, "clr_103_4.dmp") && FileExists(dumps, "clr_104_5.dmp"),
        "the newest 3 kept");
    CHECK(FileExists(dumps, "hang_1_old.dmp") && FileExists(dumps, "clr_notes.txt") &&
        GetFileAttributesA(sub) != INVALID_FILE_ATTRIBUTES, "hang dumps, other files and folders untouched");
    CHECK(PruneCrashDumps(dumps, DUMP_KEEP) == 0, "a second prune deletes nothing");
    char missing[MAX_PATH];
    _snprintf_s(missing, sizeof missing, _TRUNCATE, "%s\\nope", root);
    CHECK(PruneCrashDumps(missing, DUMP_KEEP) == 0, "a missing folder: nothing to do");

    // 6. The environment CoreCLR reads at start.
    char v[MAX_PATH], want[MAX_PATH], runtimeDir[MAX_PATH], newDumps[MAX_PATH];
    SetEnvironmentVariableA("DOTNET_DbgEnableMiniDump", NULL);
    SetEnvironmentVariableA("COMPlus_DbgEnableMiniDump", NULL);
    SetEnvironmentVariableA("DOTNET_DbgMiniDumpType", NULL);
    SetEnvironmentVariableA("DOTNET_DbgMiniDumpName", NULL);
    _snprintf_s(runtimeDir, sizeof runtimeDir, _TRUNCATE, "%s\\dotnet", root);   // no createdump.exe: warns
    _snprintf_s(newDumps, sizeof newDumps, _TRUNCATE, "%s\\newdumps", root);      // created on demand
    MakeFile(dumps, "clr_105_6.dmp", 5);
    int type = ConfigureCrashDumps(dumps, runtimeDir);
    ULARGE_INTEGER freeBytes;
    GetDiskFreeSpaceExA(dumps, &freeBytes, NULL, NULL);
    CHECK(type == CrashDumpType(freeBytes.QuadPart), "dump type %d for %llu MB free", type, freeBytes.QuadPart >> 20);
    Env("DOTNET_DbgEnableMiniDump", v, sizeof v);
    CHECK(strcmp(v, "1") == 0, "DOTNET_DbgEnableMiniDump=%s", v);
    Env("DOTNET_DbgMiniDumpType", v, sizeof v);
    CHECK(atoi(v) == type, "DOTNET_DbgMiniDumpType=%s", v);
    Env("DOTNET_DbgMiniDumpName", v, sizeof v);
    _snprintf_s(want, sizeof want, _TRUNCATE, "%s\\clr_%lu_%%t.dmp", dumps, self);
    CHECK(strcmp(v, want) == 0, "DOTNET_DbgMiniDumpName=%s (pid in the name, createdump's %%t for the time)", v);
    CHECK(CountFiles(dumps, "clr_*.dmp") == 3 && !FileExists(dumps, "clr_102_3.dmp"), "pruned at startup to the newest 3");

    // Already configured (a test client, or =0 to turn it off): left alone.
    SetEnvironmentVariableA("DOTNET_DbgEnableMiniDump", "0");
    SetEnvironmentVariableA("DOTNET_DbgMiniDumpName", "C:\\elsewhere\\x.dmp");
    CHECK(ConfigureCrashDumps(newDumps, runtimeDir) == 0, "DOTNET_DbgEnableMiniDump=0 already set: returns 0");
    Env("DOTNET_DbgEnableMiniDump", v, sizeof v);
    CHECK(strcmp(v, "0") == 0, "...and stays 0");
    Env("DOTNET_DbgMiniDumpName", v, sizeof v);
    CHECK(strcmp(v, "C:\\elsewhere\\x.dmp") == 0, "...name untouched");
    SetEnvironmentVariableA("DOTNET_DbgEnableMiniDump", NULL);
    SetEnvironmentVariableA("COMPlus_DbgEnableMiniDump", "1");
    CHECK(ConfigureCrashDumps(newDumps, runtimeDir) == 0, "the legacy COMPlus_ name counts too");
    SetEnvironmentVariableA("COMPlus_DbgEnableMiniDump", NULL);
    CHECK(ConfigureCrashDumps(newDumps, runtimeDir) > 0 && GetFileAttributesA(newDumps) != INVALID_FILE_ATTRIBUTES,
        "a missing dumps folder is created");

    printf(fails ? "\n%d FAILED\n" : "\nALL PASSED\n", fails);
    return fails;
}
