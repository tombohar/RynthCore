// SpikeHost: Phase 0 spike for docs/UNLOADABLE_ENGINE_PLAN.md.
//
// Starts an app-local CoreCLR (x86) through the raw coreclr_* hosting API, loads
// Shim.dll into the default ALC and calls Shim.Entry.Run, which loads Payload.dll
// into a collectible ALC N times, calls into it from native code, unloads it and
// measures free address space through the HostApi below.
//
// Built as a DLL (SpikeHost.dll, export SpikeRun) so the same code can be injected
// into acclient; SpikeHostExe.exe is a console driver that LoadLibrary's it.

#include <windows.h>
#include <stdio.h>
#include <stdarg.h>

typedef int (__stdcall *coreclr_initialize_fn)(const char* exePath, const char* appDomainFriendlyName,
    int propertyCount, const char** propertyKeys, const char** propertyValues,
    void** hostHandle, unsigned int* domainId);
typedef int (__stdcall *coreclr_create_delegate_fn)(void* hostHandle, unsigned int domainId,
    const char* assemblyName, const char* typeName, const char* methodName, void** fn);

typedef struct HostApi {
    int size;
    double (__stdcall *freeMb)(double* largestFreeMb);
    void (__stdcall *log)(const char* utf8);
    int (__stdcall *callBinary)(void* fn, int a, int b);   // native frame -> managed fn pointer
    unsigned int (__stdcall *committedMb)(void);
} HostApi;

typedef int (__stdcall *shim_run_fn)(HostApi* api, int cycles, int mode);

static char g_dir[MAX_PATH];
static FILE* g_log;

static void LogLine(const char* fmt, ...)
{
    char buf[2048];
    va_list ap;
    va_start(ap, fmt);
    _vsnprintf_s(buf, sizeof buf, _TRUNCATE, fmt, ap);
    va_end(ap);
    SYSTEMTIME t;
    GetLocalTime(&t);
    char line[2200];
    _snprintf_s(line, sizeof line, _TRUNCATE, "[%02d:%02d:%02d.%03d] [pid:%lu] %s\n",
        t.wHour, t.wMinute, t.wSecond, t.wMilliseconds, GetCurrentProcessId(), buf);
    fputs(line, stdout);
    fflush(stdout);
    if (g_log) { fputs(line, g_log); fflush(g_log); }
    OutputDebugStringA(line);
}

static double __stdcall FreeMb(double* largestFreeMb)
{
    SYSTEM_INFO si;
    GetSystemInfo(&si);
    unsigned char* p = (unsigned char*)si.lpMinimumApplicationAddress;
    unsigned char* end = (unsigned char*)si.lpMaximumApplicationAddress;
    unsigned long long total = 0, largest = 0;
    MEMORY_BASIC_INFORMATION mbi;
    while (p < end && VirtualQuery(p, &mbi, sizeof mbi) == sizeof mbi) {
        if (mbi.State == MEM_FREE) {
            total += mbi.RegionSize;
            if (mbi.RegionSize > largest) largest = mbi.RegionSize;
        }
        p = (unsigned char*)mbi.BaseAddress + mbi.RegionSize;
    }
    if (largestFreeMb) *largestFreeMb = largest / (1024.0 * 1024.0);
    return total / (1024.0 * 1024.0);
}

static unsigned int __stdcall CommittedMb(void)
{
    SYSTEM_INFO si;
    GetSystemInfo(&si);
    unsigned char* p = (unsigned char*)si.lpMinimumApplicationAddress;
    unsigned char* end = (unsigned char*)si.lpMaximumApplicationAddress;
    unsigned long long total = 0;
    MEMORY_BASIC_INFORMATION mbi;
    while (p < end && VirtualQuery(p, &mbi, sizeof mbi) == sizeof mbi) {
        if (mbi.State == MEM_COMMIT && mbi.Type == MEM_PRIVATE) total += mbi.RegionSize;
        p = (unsigned char*)mbi.BaseAddress + mbi.RegionSize;
    }
    return (unsigned int)(total / (1024 * 1024));
}

static void __stdcall ManagedLog(const char* utf8) { LogLine("[shim] %s", utf8); }

static int __stdcall CallBinary(void* fn, int a, int b)
{
    typedef int (__stdcall *bin_fn)(int, int);
    return ((bin_fn)fn)(a, b);
}

// Appends every *.dll in dir to the ';'-separated TPA list.
static void AppendDlls(char* tpa, size_t cap, const char* dir)
{
    char pattern[MAX_PATH];
    _snprintf_s(pattern, sizeof pattern, _TRUNCATE, "%s\\*.dll", dir);
    WIN32_FIND_DATAA fd;
    HANDLE h = FindFirstFileA(pattern, &fd);
    if (h == INVALID_HANDLE_VALUE) return;
    do {
        // Native runtime DLLs sit in the same folder; skip what isn't managed.
        const char* n = fd.cFileName;
        if (!_stricmp(n, "coreclr.dll") || !_stricmp(n, "clrjit.dll") || !_stricmp(n, "clrgc.dll") ||
            !_stricmp(n, "hostfxr.dll") || !_stricmp(n, "hostpolicy.dll") || !_stricmp(n, "mscordaccore.dll") ||
            !_stricmp(n, "mscordbi.dll") || !_stricmp(n, "mscorrc.dll") || !_stricmp(n, "clretwrc.dll") ||
            !_strnicmp(n, "mscordaccore_", 13) || !_stricmp(n, "msquic.dll") ||
            !_stricmp(n, "System.IO.Compression.Native.dll") || !_stricmp(n, "Microsoft.DiaSymReader.Native.x86.dll") ||
            !_stricmp(n, "SpikeHost.dll"))
            continue;
        strcat_s(tpa, cap, dir);
        strcat_s(tpa, cap, "\\");
        strcat_s(tpa, cap, n);
        strcat_s(tpa, cap, ";");
    } while (FindNextFileA(h, &fd));
    FindClose(h);
}

__declspec(dllexport) int __stdcall SpikeRun(int cycles, int mode)
{
    HMODULE self;
    GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
        (LPCSTR)&SpikeRun, &self);
    GetModuleFileNameA(self, g_dir, MAX_PATH);
    *strrchr(g_dir, '\\') = 0;

    char logPath[MAX_PATH];
    _snprintf_s(logPath, sizeof logPath, _TRUNCATE, "%s\\spike.%lu.log", g_dir, GetCurrentProcessId());
    fopen_s(&g_log, logPath, "a");

    double largest;
    double free0 = FreeMb(&largest);
    LogLine("host: start, free %.0f MB (largest %.0f), private commit %u MB", free0, largest, CommittedMb());

    char runtimeDir[MAX_PATH], coreclrPath[MAX_PATH];
    _snprintf_s(runtimeDir, sizeof runtimeDir, _TRUNCATE, "%s\\runtime", g_dir);
    _snprintf_s(coreclrPath, sizeof coreclrPath, _TRUNCATE, "%s\\coreclr.dll", runtimeDir);

    DWORD t0 = GetTickCount();
    HMODULE clr = LoadLibraryExA(coreclrPath, NULL, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (!clr) { LogLine("host: LoadLibrary coreclr failed %lu", GetLastError()); return -1; }
    coreclr_initialize_fn init = (coreclr_initialize_fn)GetProcAddress(clr, "coreclr_initialize");
    coreclr_create_delegate_fn create = (coreclr_create_delegate_fn)GetProcAddress(clr, "coreclr_create_delegate");
    if (!init || !create) { LogLine("host: coreclr exports missing"); return -2; }

    static char tpa[64 * 1024];
    tpa[0] = 0;
    AppendDlls(tpa, sizeof tpa, runtimeDir);
    AppendDlls(tpa, sizeof tpa, g_dir);   // Shim.dll + Contracts.dll (default ALC)

    // Mode bit 0x200: concurrent (background) GC on.
    const char* keys[] = {
        "TRUSTED_PLATFORM_ASSEMBLIES", "APP_PATHS", "NATIVE_DLL_SEARCH_DIRECTORIES",
        "System.GC.Server", "System.GC.Concurrent", "System.Globalization.Invariant",
        "System.GC.RetainVM",
    };
    char nativeDirs[2 * MAX_PATH + 4];
    _snprintf_s(nativeDirs, sizeof nativeDirs, _TRUNCATE, "%s;%s;", runtimeDir, g_dir);
    const char* values[] = {
        tpa, "", nativeDirs,
        "false", (mode & 0x200) ? "true" : "false", "true",
        "false",
    };
    int count = sizeof keys / sizeof keys[0];

    char exePath[MAX_PATH];
    GetModuleFileNameA(NULL, exePath, MAX_PATH);
    void* hostHandle = NULL;
    unsigned int domainId = 0;
    int hr = init(exePath, "RynthCoreSpike", count, keys, values, &hostHandle, &domainId);
    if (hr < 0) { LogLine("host: coreclr_initialize failed 0x%08x", hr); return -3; }
    LogLine("host: CoreCLR up in %lu ms, free %.0f MB (cost %.0f MB), private commit %u MB",
        GetTickCount() - t0, FreeMb(&largest), free0 - FreeMb(NULL), CommittedMb());

    shim_run_fn run = NULL;
    hr = create(hostHandle, domainId, "Shim", "Shim.Entry", "Run", (void**)&run);
    if (hr < 0 || !run) { LogLine("host: create_delegate Shim.Entry.Run failed 0x%08x", hr); return -4; }

    static HostApi api;
    api.size = sizeof api;
    api.freeMb = FreeMb;
    api.log = ManagedLog;
    api.callBinary = CallBinary;
    api.committedMb = CommittedMb;
    int rc = run(&api, cycles, mode);
    LogLine("host: Shim.Run returned %d, free %.0f MB (largest %.0f)", rc, FreeMb(&largest), largest);
    if (g_log) { fclose(g_log); g_log = NULL; }
    return rc;
}

// Injected use: RYNTH_SPIKE_AUTORUN="cycles,mode,delayMs" in the target's environment
// makes DllMain start a worker (no CLR work under the loader lock).
static int g_autoCycles = 20, g_autoMode = 0, g_autoDelay = 3000;

static DWORD WINAPI AutoRun(LPVOID p)
{
    (void)p;
    Sleep(g_autoDelay);
    SpikeRun(g_autoCycles, g_autoMode);
    return 0;
}

BOOL WINAPI DllMain(HINSTANCE h, DWORD reason, LPVOID reserved)
{
    (void)h; (void)reserved;
    if (reason == DLL_PROCESS_ATTACH) {
        char v[64];
        if (GetEnvironmentVariableA("RYNTH_SPIKE_AUTORUN", v, sizeof v) > 0) {
            sscanf_s(v, "%d,%i,%d", &g_autoCycles, &g_autoMode, &g_autoDelay);
            HANDLE t = CreateThread(NULL, 0, AutoRun, NULL, 0, NULL);
            if (t) CloseHandle(t);
        }
    }
    return TRUE;
}
