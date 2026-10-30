// ============================================================================
//  Services.c - the other native entry points the engine hands out, made permanent
//  (docs/UNLOADABLE_ENGINE_PLAN.md: "nothing native points into collectible code").
//
//  1. Window subclass: RcSubclass_Install puts the loader's own WndProc on a window,
//     once for the life of the process, and forwards to the current engine's handler.
//     RcSubclass_Release (and every reload) makes it a pure pass-through to the window's
//     previous proc. The engine never has to unhook, so a foreign subclasser stacked on
//     top of us (Decal, DirectInput) can never be ripped out of the chain, and nothing
//     in the chain ever points at an unloaded engine.
//
//  2. Plugin API stubs: RcApiStub(index, fn) returns a permanent stub for table slot
//     `index` that jumps to `fn`. Plugins copy the table and can't give it back; after a
//     reload the slots are empty until the next engine fills them, and a plugin thread
//     that calls one in between waits (any calling convention: registers and stack are
//     untouched) and then runs the new engine's function.
// ============================================================================

#include "Loader.h"

// --- 1. window subclass --------------------------------------------------------

#define MAX_SUBCLASSED 16

typedef LRESULT (CALLBACK *wndproc_fn)(HWND, UINT, WPARAM, LPARAM);

typedef struct Subclass {
    HWND hwnd;
    WNDPROC previous;             // the proc we replaced (AC's, or whoever was there)
    wndproc_fn volatile handler;  // current engine's handler, or NULL = pass-through
} Subclass;

static Subclass g_subclassed[MAX_SUBCLASSED];
static volatile LONG g_subclassCount;
static CRITICAL_SECTION g_servicesLock;
static int g_servicesReady;

static void EnsureServices(void)
{
    if (!g_servicesReady) {
        InitializeCriticalSection(&g_servicesLock);
        g_servicesReady = 1;
    }
}

static Subclass* FindSubclass(HWND hwnd)
{
    LONG n = g_subclassCount;
    for (LONG i = 0; i < n; i++)
        if (g_subclassed[i].hwnd == hwnd) return &g_subclassed[i];
    return NULL;
}

static LRESULT CALLBACK LoaderWndProc(HWND hwnd, UINT msg, WPARAM w, LPARAM l)
{
    Subclass* s = FindSubclass(hwnd);
    if (!s) return DefWindowProcA(hwnd, msg, w, l);
    wndproc_fn h = s->handler;
    if (h) return h(hwnd, msg, w, l);
    return CallWindowProcA(s->previous, hwnd, msg, w, l);
}

// Returns 1 and the window's previous proc (what the engine's handler must forward to).
int __stdcall RcSubclass_Install(HWND hwnd, void* handler, void** previousOut)
{
    EnsureServices();
    int ok = 1;
    EnterCriticalSection(&g_servicesLock);
    Subclass* s = FindSubclass(hwnd);
    if (!s) {
        if (g_subclassCount >= MAX_SUBCLASSED) ok = 0;
        else {
            s = &g_subclassed[g_subclassCount];
            s->hwnd = hwnd;
            s->handler = NULL;
            SetLastError(0);
            s->previous = (WNDPROC)SetWindowLongA(hwnd, GWL_WNDPROC, (LONG)(LONG_PTR)LoaderWndProc);
            if (!s->previous) {
                LoaderLog("ERR", "Subclass: SetWindowLong on 0x%p failed (error %lu)", (void*)hwnd, GetLastError());
                ok = 0;
            } else {
                InterlockedIncrement(&g_subclassCount);
                LoaderLog("INF", "Subclass: 0x%p subclassed for good (previous proc 0x%p).", (void*)hwnd, (void*)s->previous);
            }
        }
    }
    if (ok) {
        s->handler = (wndproc_fn)handler;
        if (previousOut) *previousOut = (void*)s->previous;
    }
    LeaveCriticalSection(&g_servicesLock);
    return ok;
}

int __stdcall RcSubclass_Release(HWND hwnd)
{
    Subclass* s = FindSubclass(hwnd);
    if (!s) return 0;
    s->handler = NULL;
    return 1;
}

// --- 2. plugin API stubs --------------------------------------------------------

#define MAX_API_STUBS 512
#define API_STUB_SIZE 32

static void* volatile g_apiSlots[MAX_API_STUBS];
static unsigned char* g_apiStubs;
static volatile LONG g_apiWaitLogged;

// Called from a stub whose slot is empty (between engine generations). Blocks until the
// next engine fills the table. stdcall, one argument; the stub saves every register.
static void __stdcall ApiWait(int index)
{
    DWORD start = GetTickCount();
    int logged = 0;
    while (g_apiSlots[index] == NULL) {
        Sleep(10);
        if (!logged && GetTickCount() - start > 10000) {
            logged = 1;
            LoaderLog("WRN", "ApiStub: a plugin thread has waited 10 s for API slot %d (no engine loaded?).", index);
        }
    }
}

static void BuildApiStub(int i)
{
    unsigned char* p = g_apiStubs + i * API_STUB_SIZE;
    unsigned char* start = p;
    void* volatile* slot = &g_apiSlots[i];
    *p++ = 0xA1; *(void* volatile**)p = slot; p += 4;          // mov eax, [slot]
    *p++ = 0x85; *p++ = 0xC0;                                 // test eax, eax
    *p++ = 0x74; *p++ = 0x02;                                 // jz +2 (wait)
    *p++ = 0xFF; *p++ = 0xE0;                                 // jmp eax
    *p++ = 0x60;                                              // pushad
    *p++ = 0x68; *(int*)p = i; p += 4;                        // push i
    *p++ = 0xE8; *(int*)p = (int)((unsigned char*)ApiWait - (p + 4)); p += 4;   // call ApiWait
    *p++ = 0x61;                                              // popad
    *p++ = 0xE9; *(int*)p = (int)(start - (p + 4)); p += 4;   // jmp start
    while (p < start + API_STUB_SIZE) *p++ = 0xCC;
}

void* __stdcall RcApiStub(int index, void* fn)
{
    EnsureServices();
    if (index < 0 || index >= MAX_API_STUBS) return fn;
    EnterCriticalSection(&g_servicesLock);
    if (!g_apiStubs) {
        g_apiStubs = (unsigned char*)VirtualAlloc(NULL, MAX_API_STUBS * API_STUB_SIZE, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
        if (g_apiStubs) {
            for (int i = 0; i < MAX_API_STUBS; i++) BuildApiStub(i);
            FlushInstructionCache(GetCurrentProcess(), g_apiStubs, MAX_API_STUBS * API_STUB_SIZE);
        }
    }
    LeaveCriticalSection(&g_servicesLock);
    if (!g_apiStubs) return fn;
    // Already one of ours (a table wrapped twice): never point a slot at a stub.
    if ((unsigned char*)fn >= g_apiStubs && (unsigned char*)fn < g_apiStubs + MAX_API_STUBS * API_STUB_SIZE)
        return fn;
    InterlockedExchangePointer((PVOID volatile*)&g_apiSlots[index], fn);
    return g_apiStubs + index * API_STUB_SIZE;
}

// Every reload: nothing we hand out may keep pointing into the old engine.
void ServicesReleaseAll(void)
{
    EnsureServices();
    EnterCriticalSection(&g_servicesLock);
    for (LONG i = 0; i < g_subclassCount; i++) g_subclassed[i].handler = NULL;
    for (int i = 0; i < MAX_API_STUBS; i++) g_apiSlots[i] = NULL;
    LeaveCriticalSection(&g_servicesLock);
    LoaderLog("INF", "Services: %ld window subclass(es) pass through, plugin API slots emptied until the next engine.", g_subclassCount);
}

int __stdcall RcServices_Version(void) { return 1; }
