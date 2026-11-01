// ============================================================================
//  tools\LoaderFreezeBench\bench.c - how long one MinHook queued apply (the loader's
//  "Hooks: N patch(es) applied in one freeze") takes while this process's LOWEST-priority
//  threads can't get a CPU, with and without the HIGH priority class the loader's
//  BoostForFreeze (native\Loader\Hooks.c) puts around every freeze.
//
//  The burner is a second copy of this program: BELOW_NORMAL class, a normal-priority spinning
//  thread per CPU (+2), so it sits above this process's LOWEST threads (BELOW_NORMAL class too)
//  but below every normal-priority process (the game clients keep their CPU). It runs ~15 s
//  per starved phase. Not a gate test (it measures, it doesn't pass or fail, and it loads the
//  machine): run it by hand with Run-Bench.ps1.
//
//  2026-11-01 on the 8-core PC with five clients running (the machine already busy): an apply
//  took 0.4-13 s as is unstarved and 12-38 s starved; with the boost 85-110 ms either way.
// ============================================================================
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

typedef int (__stdcall *fn0)(void);
typedef int (__stdcall *fncreate)(void*, void*, void**);
typedef int (__stdcall *fn1)(void*);

static volatile LONG g_stop;
static HANDLE g_never;

static DWORD WINAPI Spin(LPVOID p) { (void)p; volatile unsigned x = 0; while (!g_stop) x++; return 0; }
static DWORD WINAPI Idle(LPVOID p) { (void)p; WaitForSingleObject(g_never, INFINITE); return 0; }

__declspec(noinline) int __stdcall Target(int a) { return a * 3 + 1; }
__declspec(noinline) int __stdcall Detour(int a) { return a; }

static fn1 g_qen, g_qdis;
static fn0 g_apply;
static int g_enabled;

static double ApplyOnce(int boost)
{
    (g_enabled ? g_qdis : g_qen)((void*)Target);
    LARGE_INTEGER f, t0, t1;
    QueryPerformanceFrequency(&f);
    QueryPerformanceCounter(&t0);
    DWORD before = GetPriorityClass(GetCurrentProcess());
    if (boost) SetPriorityClass(GetCurrentProcess(), HIGH_PRIORITY_CLASS);
    int st = g_apply();
    QueryPerformanceCounter(&t1);   // the freeze is over here: every thread runs again
    if (boost) SetPriorityClass(GetCurrentProcess(), before);
    g_enabled = !g_enabled;
    if (st != 0) printf("  MH_ApplyQueued = %d\n", st);
    return (t1.QuadPart - t0.QuadPart) * 1000.0 / f.QuadPart;
}

static HANDLE StartBurner(int seconds)
{
    char self[MAX_PATH], cmd[MAX_PATH + 32];
    GetModuleFileNameA(NULL, self, MAX_PATH);
    _snprintf_s(cmd, sizeof cmd, _TRUNCATE, "\"%s\" burn %d", self, seconds);
    STARTUPINFOA si = { sizeof si };
    PROCESS_INFORMATION pi;
    if (!CreateProcessA(NULL, cmd, NULL, NULL, FALSE, 0, NULL, NULL, &si, &pi)) return NULL;
    CloseHandle(pi.hThread);
    Sleep(1500);   // every CPU busy before the first apply
    return pi.hProcess;
}

static void Phase(const char* name, int boost, int trials, int burnSeconds)
{
    HANDLE burner = burnSeconds > 0 ? StartBurner(burnSeconds) : NULL;
    if (burnSeconds > 0 && !burner) { printf("%s: could not start the burner\n", name); return; }
    for (int i = 0; i < trials; i++) {
        if (burner && WaitForSingleObject(burner, 0) == WAIT_OBJECT_0) { printf("%s: burner ended, phase cut short\n", name); break; }
        Sleep(300);
        printf("%-22s trial %d: %8.1f ms\n", name, i, ApplyOnce(boost));
        fflush(stdout);
    }
    if (burner) { TerminateProcess(burner, 0); CloseHandle(burner); }
}

int main(int argc, char** argv)
{
    if (argc >= 3 && strcmp(argv[1], "burn") == 0) {
        SetPriorityClass(GetCurrentProcess(), BELOW_NORMAL_PRIORITY_CLASS);
        SYSTEM_INFO si;
        GetSystemInfo(&si);
        for (DWORD i = 0; i < si.dwNumberOfProcessors + 2; i++) CloseHandle(CreateThread(NULL, 0, Spin, NULL, 0, NULL));
        Sleep(atoi(argv[2]) * 1000);
        return 0;
    }
    if (argc < 2) { printf("bench <minhook.x86.dll>\n"); return 2; }

    SetPriorityClass(GetCurrentProcess(), BELOW_NORMAL_PRIORITY_CLASS);
    g_never = CreateEventA(NULL, TRUE, FALSE, NULL);
    for (int i = 0; i < 6; i++) {   // like AC's d3d9 worker threads: LOWEST, waiting
        HANDLE t = CreateThread(NULL, 0, Idle, NULL, 0, NULL);
        SetThreadPriority(t, THREAD_PRIORITY_LOWEST);
        CloseHandle(t);
    }
    HMODULE mh = LoadLibraryA(argv[1]);
    if (!mh) { printf("could not load %s\n", argv[1]); return 3; }
    fn0 init = (fn0)GetProcAddress(mh, "MH_Initialize");
    fncreate create = (fncreate)GetProcAddress(mh, "MH_CreateHook");
    g_qen = (fn1)GetProcAddress(mh, "MH_QueueEnableHook");
    g_qdis = (fn1)GetProcAddress(mh, "MH_QueueDisableHook");
    g_apply = (fn0)GetProcAddress(mh, "MH_ApplyQueued");
    void* orig;
    if (!init || !create || !g_qen || !g_qdis || !g_apply || init() != 0 || create((void*)Target, (void*)Detour, &orig) != 0) {
        printf("MinHook init/create failed\n");
        return 4;
    }

    Phase("not starved, as is", 0, 3, 0);
    Phase("not starved, boost", 1, 3, 0);
    Phase("starved, boost", 1, 4, 15);
    Phase("starved, as is", 0, 2, 15);
    g_stop = 1;
    return 0;
}
