// ============================================================================
//  Clr.c - hosts CoreCLR once, from the app-local runtime in Runtime\dotnet\, and
//  talks to RynthCore.Shim.dll (default ALC), which owns the collectible
//  AssemblyLoadContext each engine generation lives in.
//
//  The raw coreclr_* API is used (no hostfxr, runtimeconfig.json or deps.json): the
//  TPA list is every managed DLL in Runtime\dotnet\ plus the Shim. The engine and its
//  own dependencies (Avalonia, ImGui.NET, ...) are NOT on the TPA list: the Shim loads
//  them into the engine's collectible ALC, so they go away with it.
// ============================================================================

#include "Loader.h"
#include <stdio.h>

typedef int (__stdcall *coreclr_initialize_fn)(const char* exePath, const char* appDomainFriendlyName,
    int propertyCount, const char** propertyKeys, const char** propertyValues,
    void** hostHandle, unsigned int* domainId);
typedef int (__stdcall *coreclr_create_delegate_fn)(void* hostHandle, unsigned int domainId,
    const char* assemblyName, const char* typeName, const char* methodName, void** fn);

// Shared with RynthCore.Shim (ShimHost.cs): keep the layouts in step.
typedef struct LoaderApi {
    int size;
    void (__stdcall *log)(const char* level, const char* utf8);
    int (__stdcall *freeMb)(int* largestMb);
    const char* loaderDir;
    int (__stdcall *hookCount)(void);
} LoaderApi;

typedef struct ShimApi {
    int size;
    int (__stdcall *loadEngine)(int generation, const char* stagedPathUtf8);
    int (__stdcall *unloadEngine)(void);                  // 1 = the ALC was freed
    void* (__stdcall *engineExport)(const char* nameUtf8);
} ShimApi;

typedef int (__stdcall *shim_init_fn)(LoaderApi* api, ShimApi* out);

static LoaderApi g_loaderApi;
static ShimApi g_shim;
static int g_started;

int __stdcall RcHooks_Count(void);

static void __stdcall ShimLog(const char* level, const char* utf8) { LoaderLog(level ? level : "INF", "[shim] %s", utf8); }
static int __stdcall ShimFreeMb(int* largestMb)
{
    long largest;
    long free = LoaderFreeAddressSpaceMb(&largest);
    if (largestMb) *largestMb = (int)largest;
    return (int)free;
}

static int IsNativeRuntimeFile(const char* n)
{
    static const char* natives[] = {
        "coreclr.dll", "clrjit.dll", "clrgc.dll", "clrgcexp.dll", "hostfxr.dll", "hostpolicy.dll",
        "mscordaccore.dll", "mscordbi.dll", "mscorrc.dll", "clretwrc.dll", "msquic.dll",
        "System.IO.Compression.Native.dll", "Microsoft.DiaSymReader.Native.x86.dll",
    };
    for (int i = 0; i < (int)(sizeof natives / sizeof natives[0]); i++)
        if (!_stricmp(n, natives[i])) return 1;
    return !_strnicmp(n, "mscordaccore_", 13);
}

static int AppendDlls(char* tpa, size_t cap, const char* dir)
{
    char pattern[MAX_PATH];
    _snprintf_s(pattern, sizeof pattern, _TRUNCATE, "%s\\*.dll", dir);
    WIN32_FIND_DATAA fd;
    HANDLE h = FindFirstFileA(pattern, &fd);
    if (h == INVALID_HANDLE_VALUE) return 0;
    int n = 0;
    do {
        if (IsNativeRuntimeFile(fd.cFileName)) continue;
        strcat_s(tpa, cap, dir);
        strcat_s(tpa, cap, "\\");
        strcat_s(tpa, cap, fd.cFileName);
        strcat_s(tpa, cap, ";");
        n++;
    } while (FindNextFileA(h, &fd));
    FindClose(h);
    return n;
}

int ClrStart(const char* loaderDir)
{
    if (g_started) return 0;
    char runtimeDir[MAX_PATH], coreclrPath[MAX_PATH], shimPath[MAX_PATH];
    _snprintf_s(runtimeDir, sizeof runtimeDir, _TRUNCATE, "%s\\dotnet", loaderDir);
    _snprintf_s(coreclrPath, sizeof coreclrPath, _TRUNCATE, "%s\\coreclr.dll", runtimeDir);
    _snprintf_s(shimPath, sizeof shimPath, _TRUNCATE, "%s\\RynthCore.Shim.dll", loaderDir);
    if (GetFileAttributesA(shimPath) == INVALID_FILE_ATTRIBUTES) {
        LoaderLog("ERR", "CLR: %s is missing", shimPath);
        return 1;
    }

    long free0 = LoaderFreeAddressSpaceMb(NULL);
    DWORD t0 = GetTickCount();
    HMODULE clr = LoadLibraryExA(coreclrPath, NULL, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (!clr) {
        LoaderLog("ERR", "CLR: LoadLibrary %s failed (error %lu)", coreclrPath, GetLastError());
        return 2;
    }
    coreclr_initialize_fn init = (coreclr_initialize_fn)GetProcAddress(clr, "coreclr_initialize");
    coreclr_create_delegate_fn create = (coreclr_create_delegate_fn)GetProcAddress(clr, "coreclr_create_delegate");
    if (!init || !create) return 3;

    static char tpa[128 * 1024];
    tpa[0] = 0;
    int count = AppendDlls(tpa, sizeof tpa, runtimeDir);
    strcat_s(tpa, sizeof tpa, shimPath);
    strcat_s(tpa, sizeof tpa, ";");

    char nativeDirs[2 * MAX_PATH + 4], baseDir[MAX_PATH + 2];
    _snprintf_s(nativeDirs, sizeof nativeDirs, _TRUNCATE, "%s;%s;", loaderDir, runtimeDir);
    _snprintf_s(baseDir, sizeof baseDir, _TRUNCATE, "%s\\", loaderDir);
    const char* keys[] = {
        "TRUSTED_PLATFORM_ASSEMBLIES", "APP_PATHS", "NATIVE_DLL_SEARCH_DIRECTORIES", "APP_CONTEXT_BASE_DIRECTORY",
        "System.GC.Server", "System.GC.Concurrent", "System.Globalization.Invariant", "System.GC.RetainVM",
    };
    // Concurrent (background) GC on: the engine and the managed plugins share this one GC,
    // and with it off every gen2 (~every 10 s, driven by large allocations) was a fully
    // blocking 55-66 ms pause that AC's main thread hit whenever it was in managed code.
    const char* values[] = {
        tpa, "", nativeDirs, baseDir,
        "false", "true", "true", "false",
    };
    char exePath[MAX_PATH];
    GetModuleFileNameA(NULL, exePath, MAX_PATH);
    void* hostHandle = NULL;
    unsigned int domainId = 0;
    int hr = init(exePath, "RynthCore", (int)(sizeof keys / sizeof keys[0]), keys, values, &hostHandle, &domainId);
    if (hr < 0) {
        LoaderLog("ERR", "CLR: coreclr_initialize failed 0x%08x", hr);
        return 4;
    }

    shim_init_fn shimInit = NULL;
    hr = create(hostHandle, domainId, "RynthCore.Shim", "RynthCore.Shim.ShimHost", "Init", (void**)&shimInit);
    if (hr < 0 || !shimInit) {
        LoaderLog("ERR", "CLR: RynthCore.Shim.ShimHost.Init not found (0x%08x)", hr);
        return 5;
    }
    g_loaderApi.size = sizeof g_loaderApi;
    g_loaderApi.log = ShimLog;
    g_loaderApi.freeMb = ShimFreeMb;
    g_loaderApi.loaderDir = LoaderDirectory();
    g_loaderApi.hookCount = RcHooks_Count;
    g_shim.size = sizeof g_shim;
    int rc = shimInit(&g_loaderApi, &g_shim);
    if (rc != 0 || !g_shim.loadEngine || !g_shim.unloadEngine || !g_shim.engineExport) {
        LoaderLog("ERR", "CLR: Shim init failed (rc=%d)", rc);
        return 6;
    }
    g_started = 1;
    LoaderLog("INF", "CLR: CoreCLR up in %lu ms from %s (%d framework assemblies), %ld MB of address space used.",
        GetTickCount() - t0, runtimeDir, count, free0 - LoaderFreeAddressSpaceMb(NULL));
    return 0;
}

int ClrLoadEngine(int generation, const char* stagedPath)
{
    return g_started ? g_shim.loadEngine(generation, stagedPath) : -1;
}

void ClrUnloadEngine(void)
{
    if (g_started) g_shim.unloadEngine();
}

void* ClrEngineExport(const char* name)
{
    return g_started ? g_shim.engineExport(name) : NULL;
}
