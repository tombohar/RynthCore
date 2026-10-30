// ============================================================================
//  Hooks.c - MinHook facade with permanent stubs
//
//  Exports MinHook's API (MH_Initialize, MH_CreateHook, MH_EnableHook, ...) with the
//  same signatures and status codes, so the engine's Hooking/MinHook.cs can call either
//  this facade or minhook.x86.dll.
//
//  The difference: nothing native ever points at engine code directly. For every target
//  the facade allocates a permanent 6-byte stub, `jmp dword ptr [slot]`, and installs the
//  real MinHook hook once, with the stub as the detour. The slot holds either the
//  engine's detour (hook enabled) or MinHook's trampoline (hook disabled: a transparent
//  pass-through to the original, whatever the calling convention). A reload only
//  re-points slots, so an engine generation can be unloaded as soon as no thread is
//  inside its code, and the next generation's MH_CreateHook for the same target reuses
//  the same stub and gets the same trampoline back.
//
//  Hooks are "owned" by the generation that created them: MH_CreateHook on a target
//  created by an earlier generation succeeds (no MH_ERROR_ALREADY_CREATED), and
//  HooksPassThroughAll() (every reload) turns every slot back into a pass-through and
//  takes the patch out of the target (MinHook disable: original bytes back), so the next
//  generation's pattern scans see AC's real prologues; its MH_CreateHook puts it back.
//  The stub and trampoline are never freed, so a thread still returning through them is
//  safe.
//
//  Patching is batched. Every MinHook enable/disable freezes all the process's threads
//  (a system-wide thread snapshot, ~100 ms here), and an engine installs ~60 hooks: one
//  freeze each made up ~7 s of every reload. The facade records which targets should be
//  patched and applies them in one queued MinHook freeze: straight after the engine's
//  init export returns (HooksFlush; on a first launch AC's main thread is still suspended
//  then), on every reload, and ~20 ms after the last change otherwise (a background
//  flusher). A hook's slot is set at once; its patch follows within that window.
// ============================================================================

#include "Loader.h"
#include <stdio.h>

#define MH_OK                        0
#define MH_ERROR_ALREADY_INITIALIZED 1
#define MH_ERROR_NOT_INITIALIZED     2
#define MH_ERROR_ALREADY_CREATED     3
#define MH_ERROR_NOT_CREATED         4
#define MH_ERROR_ENABLED             5
#define MH_ERROR_DISABLED            6
#define MH_ERROR_MEMORY_ALLOC        9
#define MH_UNKNOWN                  -1

#define MAX_HOOKS   1024
#define STUB_SIZE   8

typedef int (__stdcall *mh_init_fn)(void);
typedef int (__stdcall *mh_create_fn)(void* target, void* detour, void** original);
typedef int (__stdcall *mh_target_fn)(void* target);
typedef const char* (__stdcall *mh_status_fn)(int status);

typedef struct HookEntry {
    void* target;
    void* trampoline;      // MinHook's call-the-original trampoline (never freed)
    void* detour;          // the owning generation's detour, or NULL once removed
    int generation;        // generation that last created it
    int created;           // created (and not removed) by `generation`
    int enabled;           // slot == detour
    int patched;           // MinHook's jump is in the target
    int want;              // it should be (applied by the next flush)
} HookEntry;

static CRITICAL_SECTION g_lock;
static int g_ready;
static int g_generation;
static HMODULE g_minhook;
static mh_init_fn p_init;
static mh_create_fn p_create;
static mh_target_fn p_enable;
static mh_target_fn p_disable;
static mh_target_fn p_queueDisable;
static mh_target_fn p_queueEnable;
static mh_init_fn p_applyQueued;
static mh_status_fn p_status;

static HookEntry g_hooks[MAX_HOOKS];
static void* volatile g_slots[MAX_HOOKS];
static unsigned char* g_stubs;
static int g_count;
static HANDLE g_dirtyEvent;
static volatile DWORD g_lastChange;

static void FlushLocked(const char* why);

static DWORD WINAPI Flusher(LPVOID unused)
{
    (void)unused;
    for (;;) {
        WaitForSingleObject(g_dirtyEvent, INFINITE);
        do Sleep(20); while (GetTickCount() - g_lastChange < 20);   // settle: one freeze per burst
        EnterCriticalSection(&g_lock);
        FlushLocked("batched");
        LeaveCriticalSection(&g_lock);
    }
}

int HooksInit(const char* loaderDir)
{
    InitializeCriticalSection(&g_lock);
    char path[MAX_PATH];
    _snprintf_s(path, sizeof path, _TRUNCATE, "%s\\minhook.x86.dll", loaderDir);
    g_minhook = LoadLibraryA(path);
    if (!g_minhook) g_minhook = LoadLibraryA("minhook.x86.dll");
    if (!g_minhook) return 0;
    p_init = (mh_init_fn)GetProcAddress(g_minhook, "MH_Initialize");
    p_create = (mh_create_fn)GetProcAddress(g_minhook, "MH_CreateHook");
    p_enable = (mh_target_fn)GetProcAddress(g_minhook, "MH_EnableHook");
    p_disable = (mh_target_fn)GetProcAddress(g_minhook, "MH_DisableHook");
    p_queueDisable = (mh_target_fn)GetProcAddress(g_minhook, "MH_QueueDisableHook");
    p_queueEnable = (mh_target_fn)GetProcAddress(g_minhook, "MH_QueueEnableHook");
    p_applyQueued = (mh_init_fn)GetProcAddress(g_minhook, "MH_ApplyQueued");
    p_status = (mh_status_fn)GetProcAddress(g_minhook, "MH_StatusToString");
    if (!p_init || !p_create || !p_enable || !p_disable) return 0;
    int st = p_init();
    if (st != MH_OK && st != MH_ERROR_ALREADY_INITIALIZED) {
        LoaderLog("ERR", "Hooks: MH_Initialize = %d", st);
        return 0;
    }
    g_stubs = (unsigned char*)VirtualAlloc(NULL, MAX_HOOKS * STUB_SIZE, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    if (!g_stubs) return 0;
    g_dirtyEvent = CreateEventA(NULL, FALSE, FALSE, NULL);
    if (p_queueEnable && p_queueDisable && p_applyQueued && g_dirtyEvent) {
        HANDLE t = CreateThread(NULL, 0, Flusher, NULL, 0, NULL);
        if (t) CloseHandle(t);
    }
    g_ready = 1;
    LoaderLog("INF", "Hooks: MinHook facade ready (%s, %d stub slots, %s patching).", path, MAX_HOOKS,
        p_queueEnable ? "batched" : "per-hook");
    return 1;
}

void HooksBeginGeneration(int generation)
{
    EnterCriticalSection(&g_lock);
    g_generation = generation;
    LeaveCriticalSection(&g_lock);
}

static void SetPatched(int i, int patched)
{
    HookEntry* e = &g_hooks[i];
    if (e->patched == patched) return;
    int st = patched ? p_enable(e->target) : p_disable(e->target);
    if (st == MH_OK) e->patched = patched;
    else LoaderLog("ERR", "Hooks: MinHook %s %p failed: %d", patched ? "enable" : "disable", e->target, st);
}

// Records whether target i should be patched; the next flush applies it. (Caller holds g_lock.)
static void Want(int i, int patched)
{
    HookEntry* e = &g_hooks[i];
    e->want = patched;
    if (e->want == e->patched) return;
    if (!p_queueEnable || !p_queueDisable || !p_applyQueued || !g_dirtyEvent) {
        SetPatched(i, patched);                           // no queue in this MinHook: one at a time
        return;
    }
    g_lastChange = GetTickCount();
    SetEvent(g_dirtyEvent);
}

// Applies every pending patch/unpatch in one MinHook freeze. (Caller holds g_lock.)
static void FlushLocked(const char* why)
{
    static int queued[MAX_HOOKS];
    int n = 0;
    for (int i = 0; i < g_count; i++) {
        HookEntry* e = &g_hooks[i];
        if (e->want == e->patched) continue;
        int st = e->want ? p_queueEnable(e->target) : p_queueDisable(e->target);
        if (st == MH_OK) queued[n++] = i;
        else SetPatched(i, e->want);
    }
    if (n == 0) return;
    DWORD t0 = GetTickCount();
    int st = p_applyQueued();
    if (st == MH_OK) {
        for (int k = 0; k < n; k++) g_hooks[queued[k]].patched = g_hooks[queued[k]].want;
        LoaderLog("INF", "Hooks: %d patch(es) applied in one freeze (%lu ms, %s).", n, GetTickCount() - t0, why);
    } else {
        LoaderLog("ERR", "Hooks: MH_ApplyQueued (%d, %s) = %d; applying one at a time.", n, why, st);
        for (int k = 0; k < n; k++) SetPatched(queued[k], g_hooks[queued[k]].want);
    }
}

void HooksFlush(void)
{
    if (!g_ready) return;
    EnterCriticalSection(&g_lock);
    FlushLocked("engine init");
    LeaveCriticalSection(&g_lock);
}

static void SetSlot(int i, int enabled)
{
    HookEntry* e = &g_hooks[i];
    e->enabled = enabled;
    InterlockedExchangePointer((PVOID volatile*)&g_slots[i], enabled ? e->detour : e->trampoline);
}

void HooksPassThroughAll(void)
{
    if (!g_ready) return;
    EnterCriticalSection(&g_lock);
    int live = 0;
    for (int i = 0; i < g_count; i++) {
        if (g_hooks[i].enabled) live++;
        SetSlot(i, 0);
        Want(i, 0);
        g_hooks[i].created = 0;
        g_hooks[i].detour = NULL;
    }
    FlushLocked("reload");                                // AC's own prologues back, now
    LeaveCriticalSection(&g_lock);
    LoaderLog("INF", "Hooks: %d hook(s) now pass straight through (%d were still enabled).", g_count, live);
}

static int Find(void* target)
{
    for (int i = 0; i < g_count; i++)
        if (g_hooks[i].target == target) return i;
    return -1;
}

// --- MinHook API ------------------------------------------------------------

int __stdcall MH_Initialize(void) { return g_ready ? MH_ERROR_ALREADY_INITIALIZED : MH_ERROR_NOT_INITIALIZED; }

// Hooks outlive every engine generation; "uninitialize" only turns them into pass-throughs.
int __stdcall MH_Uninitialize(void) { HooksPassThroughAll(); return MH_OK; }

int __stdcall MH_CreateHook(void* target, void* detour, void** original)
{
    if (!g_ready) return MH_ERROR_NOT_INITIALIZED;
    int st = MH_OK;
    EnterCriticalSection(&g_lock);
    int i = Find(target);
    if (i >= 0) {
        HookEntry* e = &g_hooks[i];
        if (e->created && e->generation == g_generation) {
            st = MH_ERROR_ALREADY_CREATED;
        } else {
            e->detour = detour;
            e->generation = g_generation;
            e->created = 1;
            SetSlot(i, 0);
            Want(i, 1);
            if (original) *original = e->trampoline;
        }
    } else if (g_count >= MAX_HOOKS) {
        st = MH_ERROR_MEMORY_ALLOC;
    } else {
        i = g_count;
        unsigned char* stub = g_stubs + i * STUB_SIZE;
        void* volatile* slot = &g_slots[i];
        stub[0] = 0xFF; stub[1] = 0x25;                  // jmp dword ptr [slot]
        *(void* volatile**)(stub + 2) = slot;
        stub[6] = 0xCC; stub[7] = 0xCC;
        FlushInstructionCache(GetCurrentProcess(), stub, STUB_SIZE);

        void* tramp = NULL;
        st = p_create(target, stub, &tramp);
        if (st == MH_OK) {
            HookEntry* e = &g_hooks[i];
            e->target = target;
            e->trampoline = tramp;
            e->detour = detour;
            e->generation = g_generation;
            e->created = 1;
            e->enabled = 0;
            *slot = tramp;                                // pass-through until the engine enables it
            g_count++;
            e->patched = 0;
            Want(i, 1);                             // the patch goes in (slot passes through)
            st = MH_OK;
            if (original) *original = tramp;
        }
    }
    LeaveCriticalSection(&g_lock);
    return st;
}

int __stdcall MH_RemoveHook(void* target)
{
    if (!g_ready) return MH_ERROR_NOT_INITIALIZED;
    int st = MH_OK;
    EnterCriticalSection(&g_lock);
    int i = Find(target);
    if (i < 0 || !g_hooks[i].created) st = MH_ERROR_NOT_CREATED;
    else {
        SetSlot(i, 0);
        Want(i, 0);                                 // original bytes back, like MinHook's remove
        g_hooks[i].created = 0;
        g_hooks[i].detour = NULL;
    }
    LeaveCriticalSection(&g_lock);
    return st;
}

static int SetEnabled(void* target, int enable)
{
    if (!g_ready) return MH_ERROR_NOT_INITIALIZED;
    int st = MH_OK;
    EnterCriticalSection(&g_lock);
    if (target == NULL) {                                 // MH_ALL_HOOKS
        for (int i = 0; i < g_count; i++)
            if (g_hooks[i].created && g_hooks[i].enabled != enable) SetSlot(i, enable);
    } else {
        int i = Find(target);
        if (i < 0 || !g_hooks[i].created) st = MH_ERROR_NOT_CREATED;
        else if (g_hooks[i].enabled == enable) st = enable ? MH_ERROR_ENABLED : MH_ERROR_DISABLED;
        else SetSlot(i, enable);
    }
    LeaveCriticalSection(&g_lock);
    return st;
}

int __stdcall MH_EnableHook(void* target) { return SetEnabled(target, 1); }
int __stdcall MH_DisableHook(void* target) { return SetEnabled(target, 0); }

const char* __stdcall MH_StatusToString(int status)
{
    if (p_status) return p_status(status);
    return status == MH_OK ? "MH_OK" : "MH_UNKNOWN";
}

// Lets the engine tell this facade from minhook.x86.dll (and an old NativeAOT loader).
int __stdcall RcHooks_Version(void) { return 1; }
int __stdcall RcHooks_Count(void) { return g_count; }
