/*
 * RynthCore.SehTrampoline.dll  —  x86 native SEH wrapper for dangerous AC API calls.
 *
 * NativeAOT managed try/catch cannot intercept access violations (hardware
 * exceptions / corrupted-state exceptions).  When the bot calls AC APIs on
 * objects that are being torn down by the client's object lifecycle, the AV
 * propagates up and kills acclient.exe.
 *
 * This DLL provides __try/__except wrappers that live entirely in native code.
 * Each export calls a supplied function pointer inside a structured exception
 * handler scoped only to that one call — it does not install any process-wide
 * handler (VEH/SUEF), so it cannot interfere with AC's or Avalonia's own
 * internal exception handling.
 *
 * Conventions:
 *   - All exports are __cdecl so the caller controls stack cleanup.
 *   - Return value: 1 = call completed normally; 0 = AV was caught.
 *   - out_* parameters are always written (safe default on AV).
 *   - __thiscall helpers are separate non-__try functions; MSVC does not allow
 *     inline __asm and __try in the same function on x86.
 *
 * Build:  cl /nologo /O2 /MD /LD SehTrampoline.c /link /MACHINE:X86
 *             /OUT:RynthCore.SehTrampoline.dll /DEF:SehTrampoline.def
 */

#define WIN32_LEAN_AND_MEAN
#include <windows.h>

/* File version of this native module (bump with every behavioural change). */
#define RC_SEH_TRAMPOLINE_VERSION 8   /* v8: noise repeats only at powers of two, capped relogs per address,
                                         a total byte budget for native-crash.log (CrashLogFilter.h);
                                         v7: stack sweeps bounded by this thread's stack (TEB), never
                                         reading at or past StackBase;
                                         v6: AwfulWaffle's crash logger (per-address dedupe, pid/date,
                                         flush ring, RC_FlushCrashLog, module cache) merged with v3-v5;
                                         v5: crash log handle opened once at install; no CreateFileW
                                         while an exception is being dispatched */

/* Crash-logger state that the SEH_* wrappers and DllMain also touch (defined below). */
static __declspec(thread) int t_sehDepth;       /* >0 while inside an SEH_* wrapper call   */
static void CrashLoggerFlushOnExit(void);

/* Every SEH_* wrapper brackets its guarded call with these so the crash logger knows an
 * access violation on this thread is about to be caught by our own __except. */
#define RC_SEH_ENTER() (++t_sehDepth)
#define RC_SEH_LEAVE() (--t_sehDepth)

BOOL APIENTRY DllMain(HMODULE hModule, DWORD ul_reason, LPVOID lpReserved)
{
    (void)hModule;
    /* lpReserved != NULL on DLL_PROCESS_DETACH means the process is exiting (ExitProcess),
     * which is how AC's own crash handling ends the process: write out any recent
     * first-chance AVs so a crash AC swallowed still leaves a trace. */
    if (ul_reason == DLL_PROCESS_DETACH && lpReserved != NULL) {
        CrashLoggerFlushOnExit();
    }
    return TRUE;
}

/* Diagnostic: native module version so the engine log can confirm which build is loaded. */
__declspec(dllexport) unsigned int __cdecl RC_SehTrampolineVersion(void)
{
    return RC_SEH_TRAMPOLINE_VERSION;
}

/* ── Cdecl helpers ─────────────────────────────────────────────────────── */

/* void* __cdecl fn(unsigned int id)  — e.g. GetWeenieObject */
typedef void* (__cdecl *Fn_CdeclPtrUint)(unsigned int);
__declspec(dllexport) int __cdecl
SEH_CdeclPtrUint(Fn_CdeclPtrUint fn, unsigned int arg, void** out_result)
{
    *out_result = NULL;
    RC_SEH_ENTER();
    __try {
        *out_result = fn(arg);
    }
    __except(GetExceptionCode() == EXCEPTION_ACCESS_VIOLATION
             ? EXCEPTION_EXECUTE_HANDLER : EXCEPTION_CONTINUE_SEARCH) {
        RC_SEH_LEAVE();
        *out_result = NULL;
        return 0;
    }
    RC_SEH_LEAVE();
    return 1;
}

/* void* __cdecl fn()  — e.g. GetCombatSystem */
typedef void* (__cdecl *Fn_CdeclPtrVoid)(void);
__declspec(dllexport) int __cdecl
SEH_CdeclPtrVoid(Fn_CdeclPtrVoid fn, void** out_result)
{
    *out_result = NULL;
    RC_SEH_ENTER();
    __try {
        *out_result = fn();
    }
    __except(GetExceptionCode() == EXCEPTION_ACCESS_VIOLATION
             ? EXCEPTION_EXECUTE_HANDLER : EXCEPTION_CONTINUE_SEARCH) {
        RC_SEH_LEAVE();
        *out_result = NULL;
        return 0;
    }
    RC_SEH_LEAVE();
    return 1;
}

/* void __cdecl fn(unsigned int, unsigned char)  — e.g. CastSpell(spellId, targetIsSelected) */
typedef void (__cdecl *Fn_CdeclVoidUintByte)(unsigned int, unsigned char);
__declspec(dllexport) int __cdecl
SEH_CdeclVoidUintByte(Fn_CdeclVoidUintByte fn, unsigned int arg1, unsigned char arg2)
{
    RC_SEH_ENTER();
    __try {
        fn(arg1, arg2);
    }
    __except(GetExceptionCode() == EXCEPTION_ACCESS_VIOLATION
             ? EXCEPTION_EXECUTE_HANDLER : EXCEPTION_CONTINUE_SEARCH) {
        RC_SEH_LEAVE();
        return 0;
    }
    RC_SEH_LEAVE();
    return 1;
}

/* void __thiscall fn(this, unsigned int spellId, unsigned int targetId)
 * e.g. ClientMagicSystem::FreeHandsAndCastSpell(this, spellId, targetId) @ 0x00567C90.
 * Modeled as __fastcall (this in ECX, a dummy EDX slot, spellId+targetId on the stack;
 * callee cleans the 8 stack bytes) — the exact, proven binding RynthSuite2 uses. The
 * target is an EXPLICIT argument, so this never reads AC's global selection and never
 * touches UI state (unlike CastSpell(spellId, targetIsSelected=1) @ 0x00568DE0, which
 * reads [magicSysSingleton+0xF4]). __fastcall is a real declarable convention, so this
 * needs no inline __asm and can host its own __try. */
typedef void (__fastcall *Fn_FreeHandsCast)(void* ecx, void* edx, unsigned int spellId, unsigned int targetId);
__declspec(dllexport) int __cdecl
SEH_FreeHandsCast(void* fn, void* this_ptr, unsigned int spellId, unsigned int targetId)
{
    RC_SEH_ENTER();
    __try {
        ((Fn_FreeHandsCast)fn)(this_ptr, 0, spellId, targetId);
    }
    __except(GetExceptionCode() == EXCEPTION_ACCESS_VIOLATION
             ? EXCEPTION_EXECUTE_HANDLER : EXCEPTION_CONTINUE_SEARCH) {
        RC_SEH_LEAVE();
        return 0;
    }
    RC_SEH_LEAVE();
    return 1;
}

/* ── Thiscall helpers — inline __asm lives here, NOT in __try functions ── */

/*
 * MSVC x86: __thiscall puts 'this' in ECX.  C has no __thiscall call-site
 * syntax, so we use inline __asm.  These helpers must NOT contain __try;
 * the outer SEH wrappers call them so the SEH frame is established by the
 * caller's stack frame, not by the helper.
 */

/* unsigned char __thiscall fn(unsigned int objectId) */
static unsigned char __cdecl
_helper_thiscall_byte_uint(void* fn_ptr, void* this_ptr, unsigned int arg)
{
    unsigned char result = 0;
    __asm {
        mov  ecx, this_ptr
        push arg
        call fn_ptr
        mov  result, al
    }
    return result;
}

/* unsigned int __thiscall fn()  — no stack args, this in ECX */
static unsigned int __cdecl
_helper_thiscall_uint_noarg(void* fn_ptr, void* this_ptr)
{
    unsigned int result = 0;
    __asm {
        mov  ecx, this_ptr
        call fn_ptr
        mov  result, eax
    }
    return result;
}

/* ── Thiscall SEH wrappers ─────────────────────────────────────────────── */

/*
 * unsigned char __thiscall fn(unsigned int objectId)
 * e.g. ClientCombatSystem::ObjectIsAttackable(objectId)
 */
__declspec(dllexport) int __cdecl
SEH_ThiscallByteUint(void* fn, void* this_ptr, unsigned int arg,
                     unsigned char* out_result)
{
    *out_result = 0;
    RC_SEH_ENTER();
    __try {
        *out_result = _helper_thiscall_byte_uint(fn, this_ptr, arg);
    }
    __except(GetExceptionCode() == EXCEPTION_ACCESS_VIOLATION
             ? EXCEPTION_EXECUTE_HANDLER : EXCEPTION_CONTINUE_SEARCH) {
        RC_SEH_LEAVE();
        *out_result = 0;
        return 0;
    }
    RC_SEH_LEAVE();
    return 1;
}

/*
 * unsigned int __thiscall fn()
 * e.g. ACCWeenieObject::InqType()
 */
__declspec(dllexport) int __cdecl
SEH_ThiscallUintNoArg(void* fn, void* this_ptr, unsigned int* out_result)
{
    *out_result = 0;
    RC_SEH_ENTER();
    __try {
        *out_result = _helper_thiscall_uint_noarg(fn, this_ptr);
    }
    __except(GetExceptionCode() == EXCEPTION_ACCESS_VIOLATION
             ? EXCEPTION_EXECUTE_HANDLER : EXCEPTION_CONTINUE_SEARCH) {
        RC_SEH_LEAVE();
        *out_result = 0;
        return 0;
    }
    RC_SEH_LEAVE();
    return 1;
}

/* int __thiscall fn(unsigned int stype, void* outStruct)
 * e.g. CACQualities/CWeenieObject::InqAttribute2nd(stype, &SecondaryAttribute).
 * this in ECX; stype + outStruct on the stack (callee cleans, thiscall). Reads a
 * 2nd-level attribute (health/stam/mana) into the caller's struct. Used to pull a
 * MONSTER's real MaxHealth out of its (possibly partially-populated) qualities
 * table without crashing if a sub-table is null — the AV that forced the
 * AllowNonPlayerQualities gate. */
static int __cdecl
_helper_thiscall_int_uint_ptr(void* fn_ptr, void* this_ptr,
                              unsigned int arg1, void* arg2)
{
    int result = 0;
    __asm {
        mov  ecx, this_ptr
        push arg2          /* outStruct (rightmost) */
        push arg1          /* stype */
        call fn_ptr        /* thiscall callee cleans the 8 stack bytes */
        mov  result, eax
    }
    return result;
}

__declspec(dllexport) int __cdecl
SEH_ThiscallIntUintPtr(void* fn, void* this_ptr, unsigned int arg1, void* arg2,
                       int* out_result)
{
    *out_result = 0;
    RC_SEH_ENTER();
    __try {
        *out_result = _helper_thiscall_int_uint_ptr(fn, this_ptr, arg1, arg2);
    }
    __except(GetExceptionCode() == EXCEPTION_ACCESS_VIOLATION
             ? EXCEPTION_EXECUTE_HANDLER : EXCEPTION_CONTINUE_SEARCH) {
        RC_SEH_LEAVE();
        *out_result = 0;
        return 0;
    }
    RC_SEH_LEAVE();
    return 1;
}

/* -- Process-wide native crash logger (VEH + last-chance filter) -----------
 * Logs the faulting 32-bit context + a module-resolved stack sweep to a
 * dedicated file BEFORE the process dies, for the exceptions that bypass the
 * engine's managed handlers: 0xC0000005 (AV / corrupted state; CoreCLR will not
 * let a managed catch handle one raised in AC code under a managed detour, it
 * fail-fasts instead), 0xC0000602 (fail-fast), 0xC0000409 (stack buffer
 * overrun), 0xC00000FD (stack overflow) and 0xC0000374 (heap corruption).
 * The vectored handler is registered FIRST, so it sees an AV before CoreCLR
 * turns it into anything: the entry names the real faulting address and module.
 *
 * Which entries are written is CrashLogFilter.h's rule (once per address, a repeat
 * again at a power-of-two count or after 30 s; .NET Framework and CoreCLR noise not
 * charged to the limit; a last-chance entry always). Each entry carries the process id,
 * thread id, date and time.
 *
 * This is PURE NATIVE: no managed callback / reverse-P/Invoke transition, so
 * unlike the removed managed CrashLogger VEH it cannot itself re-trigger a
 * fail-fast on an AC-owned thread. It only OBSERVES: both handlers return
 * EXCEPTION_CONTINUE_SEARCH (the last-chance one first calls the filter it
 * replaced) so AC's own SEH / the runtime still proceed exactly as before. Uses
 * only kernel32 APIs + hand-rolled formatting (no CRT stdio / user32) so the
 * build link line stays unchanged. All buffers are static and used under one
 * spin lock (with its owner's thread id, so a fault inside the logger on the same
 * thread is passed on, never waited on): a stack overflow leaves the faulting
 * thread about one page of stack. x86 (32-bit) context fields only.
 */
#include "CrashLogFilter.h"

/*
 * Exception time does as little as it can: the address dedupe runs first (most
 * exceptions are repeats and stop there), module names come from a cache built
 * outside exception time (install, then RC_RefreshCrashLoggerModules from the engine's
 * heartbeat), and the log file is opened once at install, so writing an entry is
 * formatting plus WriteFile. Only an address the cache does not cover (a module loaded
 * since the last refresh) falls back to GetModuleHandleExW, for the faulting address
 * and eip only.
 *
 * NEVER call CreateFileW (or anything else the engine detours with a managed hook:
 * CreateFileA, MessageBoxA/W, ExitProcess, TerminateProcess, RtlExitUserProcess,
 * NtTerminateProcess) from the handlers, the flush or DllMain. DatFileShareHooks detours
 * kernel32!CreateFileW with a MANAGED delegate; our VEH runs first, on the faulting thread,
 * and for a fault in managed code that thread is still in cooperative GC mode, so entering
 * the detour is a reverse P/Invoke from cooperative mode: CoreCLR's
 * ReversePInvokeBadTransition -> HandleFatalError (event 1023, exit 0x80131506). Seven
 * client deaths 2026-09-29..10-12 were that (fixed in v5). The file is opened once, from
 * RC_InstallCrashLogger, a normal P/Invoke in preemptive mode.
 *
 * v6 (merge of AwfulWaffle's logger with v3-v5): AVs inside our own SEH_* wrappers
 * (t_sehDepth > 0, which our __except catches) are not written one by one, only kept in
 * the flush ring; the Client::UseTime guard (v4) writes its caught AV through this
 * writer; process exit (DllMain) flushes ring records from the last 30 s, as v2-v5 did.
 */
#define CL_EXIT_FLUSH_WINDOW_MS 30000u  /* ring records this recent are flushed at exit */
#define CL_MAX_GAMETICK         16      /* game-tick guard records per process */

#define CL_MOD_MAX 384

typedef struct {
    unsigned int base, end;      /* [base, end) */
    unsigned char cls;           /* clf_class */
    char leaf[48];               /* ASCII leaf name, truncated */
} cl_mod;

typedef struct {
    cl_mod mods[CL_MOD_MAX];
    int count;
} cl_mod_table;

static wchar_t       g_crashLogPath[MAX_PATH];
static HANDLE        g_logHandle  = INVALID_HANDLE_VALUE;
static PVOID         g_vehHandle  = NULL;
static LPTOP_LEVEL_EXCEPTION_FILTER g_prevFilter = NULL;
static volatile LONG g_logOwner   = 0;     /* thread id holding the logger, 0 = free */
static clf_state     g_filter;             /* under g_logOwner */
static clf_ring      g_ring;               /* under g_logOwner: recent first-chance records */
static clf_rec       g_flushRecs[CLF_RING];/* under g_logOwner */
static char          g_buf[4096];          /* under g_logOwner */
static wchar_t       g_wpath[MAX_PATH];    /* under g_logOwner */
static char          g_fallbackLeaf[48];   /* under g_logOwner */
static cl_mod_table  g_modTables[2];       /* double buffer: refresh builds the inactive one */
static volatile LONG g_modActive = -1;     /* index of the published table, -1 = none */
static volatile LONG g_modRefreshing = 0;
static volatile LONG g_gameTickRecords = 0;
static __declspec(thread) int t_inUef;      /* last-chance filter running (guards chain cycles) */

typedef BOOL  (WINAPI *PFN_EnumMods)(HANDLE, HMODULE*, DWORD, LPDWORD);
typedef BOOL  (WINAPI *PFN_ModInfo)(HANDLE, HMODULE, void*, DWORD);
typedef DWORD (WINAPI *PFN_BaseName)(HANDLE, HMODULE, LPWSTR, DWORD);
typedef struct { LPVOID lpBaseOfDll; DWORD SizeOfImage; LPVOID EntryPoint; } cl_modinfo;

static int cl_lock(void)
{
    LONG me = (LONG)GetCurrentThreadId();
    int i;
    if (g_logOwner == me) return 0;        /* faulted inside the logger: pass it on */
    for (i = 0; i < 20000; i++) {
        if (InterlockedCompareExchange(&g_logOwner, me, 0) == 0) return 1;
        YieldProcessor();
        if ((i & 0xFF) == 0xFF) SwitchToThread();
    }
    return 0;                              /* owner stuck (suspended?): skip this entry */
}

static void cl_unlock(void) { InterlockedExchange(&g_logOwner, 0); }

static char* cl_str(char* p, const char* s) { while (*s) { *p++ = *s++; } return p; }

static char* cl_hex(char* p, unsigned int v)
{
    const char* H = "0123456789ABCDEF";
    int i;
    *p++ = '0'; *p++ = 'x';
    for (i = 28; i >= 0; i -= 4) { *p++ = H[(v >> i) & 0xF]; }
    return p;
}

static char* cl_u(char* p, unsigned int v)
{
    char tmp[12]; int n = 0;
    if (v == 0) { *p++ = '0'; return p; }
    while (v) { tmp[n++] = (char)('0' + (v % 10)); v /= 10; }
    while (n) { *p++ = tmp[--n]; }
    return p;
}

static char* cl_u2(char* p, unsigned int v) { if (v < 10) { *p++ = '0'; } return cl_u(p, v); }

/*
 * Rebuild the module cache (NOT at exception time: it takes the loader lock). Builds the
 * table no reader is using and publishes it with one interlocked store; refreshes are
 * serialized and seconds apart, so a reader never sees its table rebuilt under it.
 */
__declspec(dllexport) void __cdecl
RC_RefreshCrashLoggerModules(void)
{
    static PFN_EnumMods pEnum;
    static PFN_ModInfo pInfo;
    static PFN_BaseName pName;
    HMODULE mods[CL_MOD_MAX];
    DWORD needed = 0, n, i;
    HANDLE self = GetCurrentProcess();
    LONG active, next;
    cl_mod_table* t;
    if (InterlockedCompareExchange(&g_modRefreshing, 1, 0) != 0) return;
    if (!pEnum) {
        HMODULE k32 = GetModuleHandleW(L"kernel32.dll");
        pEnum = (PFN_EnumMods)GetProcAddress(k32, "K32EnumProcessModules");
        pInfo = (PFN_ModInfo)GetProcAddress(k32, "K32GetModuleInformation");
        pName = (PFN_BaseName)GetProcAddress(k32, "K32GetModuleBaseNameW");
    }
    if (!pEnum || !pInfo || !pName || !pEnum(self, mods, sizeof(mods), &needed)) {
        InterlockedExchange(&g_modRefreshing, 0);
        return;
    }
    n = needed / sizeof(HMODULE);
    if (n > CL_MOD_MAX) n = CL_MOD_MAX;
    active = g_modActive;
    next = active == 0 ? 1 : 0;
    t = &g_modTables[next];
    t->count = 0;
    for (i = 0; i < n; i++) {
        cl_modinfo mi;
        wchar_t name[64];
        DWORD len, k;
        cl_mod* m;
        if (!pInfo(self, mods[i], &mi, sizeof(mi))) continue;
        len = pName(self, mods[i], name, 63);
        name[len < 63 ? len : 63] = 0;
        m = &t->mods[t->count++];
        m->base = (unsigned int)(UINT_PTR)mi.lpBaseOfDll;
        m->end = m->base + mi.SizeOfImage;
        m->cls = (unsigned char)clf_module_class(name);
        for (k = 0; k < sizeof(m->leaf) - 1 && name[k]; k++)
            m->leaf[k] = name[k] < 128 ? (char)name[k] : '?';
        m->leaf[k] = 0;
    }
    InterlockedExchange(&g_modActive, next);
    InterlockedExchange(&g_modRefreshing, 0);
}

/* Cached module containing addr, or NULL. No API calls. */
static const cl_mod* cl_mod_cached(unsigned int addr)
{
    LONG a = g_modActive;
    const cl_mod_table* t;
    int i;
    if (a < 0) return NULL;
    t = &g_modTables[a];
    for (i = 0; i < t->count; i++)
        if (addr >= t->mods[i].base && addr < t->mods[i].end) return &t->mods[i];
    return NULL;
}

/*
 * Module of addr: the cache, then (allowFallback) the loader. Under the lock. Returns
 * the leaf and sets *base and *cls, or NULL (no module: JIT code or garbage).
 */
static const char* cl_module(unsigned int addr, int allowFallback, unsigned int* base, clf_class* cls)
{
    const cl_mod* m = cl_mod_cached(addr);
    HMODULE hm = NULL;
    DWORD n, i, leaf = 0, k;
    if (m) { *base = m->base; *cls = (clf_class)m->cls; return m->leaf; }
    if (!allowFallback) return NULL;
    /* GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS(0x4) | UNCHANGED_REFCOUNT(0x2) */
    if (!GetModuleHandleExW(0x4 | 0x2, (LPCWSTR)(UINT_PTR)addr, &hm) || !hm) return NULL;
    n = GetModuleFileNameW(hm, g_wpath, MAX_PATH);
    if (n >= MAX_PATH) n = MAX_PATH - 1;
    g_wpath[n] = 0;
    for (i = 0; i < n; i++) { if (g_wpath[i] == L'\\' || g_wpath[i] == L'/') leaf = i + 1; }
    *base = (unsigned int)(UINT_PTR)hm;
    *cls = clf_module_class(g_wpath + leaf);
    for (k = 0; k < sizeof(g_fallbackLeaf) - 1 && g_wpath[leaf + k]; k++)
        g_fallbackLeaf[k] = g_wpath[leaf + k] < 128 ? (char)g_wpath[leaf + k] : '?';
    g_fallbackLeaf[k] = 0;
    return g_fallbackLeaf;
}

/* Append "module.dll+0xRVA", or "0xADDR" if the address is in no module. */
static char* cl_sym(char* p, unsigned int addr, int allowFallback)
{
    unsigned int base;
    clf_class cls;
    const char* leaf = cl_module(addr, allowFallback, &base, &cls);
    if (leaf) {
        p = cl_str(p, leaf);
        p = cl_str(p, "+");
        p = cl_hex(p, addr - base);
    } else {
        p = cl_hex(p, addr);
    }
    return p;
}

/*
 * How many 32-bit words a stack read starting at esp may take on THIS thread (at most
 * maxWords): up to the thread's stack base, never at or past it. 0 when esp is not on this
 * thread's stack ([StackLimit, StackBase) from the TEB; no API call). The handlers run on
 * the faulting thread, so the context's esp is on this stack. v7: before, the sweeps read a
 * fixed count and relied on the fault past the stack top to stop them; a 2026-10-15 dump
 * showed the sweep reading 0x12BF0000 (the stack base) inside CoreCLR's fatal path.
 */
static unsigned int cl_stack_words(unsigned int esp, unsigned int maxWords)
{
    NT_TIB* tib = (NT_TIB*)NtCurrentTeb();
    unsigned int base = (unsigned int)(UINT_PTR)tib->StackBase;
    unsigned int limit = (unsigned int)(UINT_PTR)tib->StackLimit;
    unsigned int words;
    if (esp < limit || esp >= base) return 0;
    words = (base - esp) / 4;            /* the last word read ends at or below base */
    return words < maxWords ? words : maxWords;
}

/* Class of the nearest cached-module code pointer on the stack (AC if none found). */
static clf_class cl_first_frame_class(unsigned int esp)
{
    clf_class result = CLF_CLASS_AC;
    unsigned int words = cl_stack_words(esp, 512);
    __try {
        unsigned int* sp = (unsigned int*)(UINT_PTR)esp;
        unsigned int i;
        for (i = 0; i < words; i++) {
            unsigned int v = sp[i];
            const cl_mod* m;
            if (v <= 0x00401000u || v >= 0x7FFF0000u) continue;
            m = cl_mod_cached(v);
            if (m) { result = (clf_class)m->cls; break; }
        }
    }
    __except (EXCEPTION_EXECUTE_HANDLER) { result = CLF_CLASS_AC; }
    return result;
}

static void cl_write(const char* p, DWORD n)
{
    DWORD wr;
    if (g_logHandle != INVALID_HANDLE_VALUE && WriteFile(g_logHandle, p, n, &wr, NULL))
        g_filter.logBytes += wr;     /* under g_logOwner, like every writer */
}

/* The byte budget (CrashLogFilter.h). Under the lock, before an entry. Returns 1 when the
 * entry may be written. The first refusal of an ordinary entry writes one marker line. */
static int cl_budget(int fatal)
{
    clf_bytes b = clf_bytes_gate(&g_filter, fatal);
    if (b == CLF_BYTES_MARK) {
        const char* m = "\r\n==== NATIVE CRASH LOGGER: log size budget reached, further ordinary entries are dropped "
                        "(fatal entries still get a reserve; restart the client or trim the file to resume) ====\r\n";
        cl_write(m, (DWORD)lstrlenA(m));
    }
    return b == CLF_BYTES_OK;
}

/* Append " pid=N tid=N date=YYYY-MM-DD time=HH:MM:SS" (local time). */
static char* cl_stamp(char* p)
{
    SYSTEMTIME st;
    p = cl_str(p, " pid=");
    p = cl_u(p, GetCurrentProcessId());
    p = cl_str(p, " tid=");
    p = cl_u(p, GetCurrentThreadId());
    GetLocalTime(&st);
    p = cl_str(p, " date=");
    p = cl_u(p, st.wYear);   p = cl_str(p, "-");
    p = cl_u2(p, st.wMonth); p = cl_str(p, "-");
    p = cl_u2(p, st.wDay);
    p = cl_str(p, " time=");
    p = cl_u2(p, st.wHour);   p = cl_str(p, ":");
    p = cl_u2(p, st.wMinute); p = cl_str(p, ":");
    p = cl_u2(p, st.wSecond);
    return p;
}

static void cl_write_entry(PEXCEPTION_POINTERS ep, const char* kind, unsigned int hits, int fatal)
{
    char* p = g_buf;
    CONTEXT* c = ep->ContextRecord;
    DWORD code = ep->ExceptionRecord->ExceptionCode;
    unsigned int exAddr = (unsigned int)(UINT_PTR)ep->ExceptionRecord->ExceptionAddress;
    unsigned int words;
    if (g_logHandle == INVALID_HANDLE_VALUE) return;
    if (!cl_budget(fatal)) return;

    p = cl_str(p, "\r\n==== NATIVE CRASH LOGGER (");
    p = cl_str(p, kind);
    p = cl_str(p, ") ====\r\n  code=");
    p = cl_hex(p, code);
    p = cl_str(p, " exAddr=");
    p = cl_sym(p, exAddr, 1);
    if (hits > 1) { p = cl_str(p, " repeat="); p = cl_u(p, hits); }
    p = cl_stamp(p);
    if ((code == 0xC0000005 || code == 0xC0000409) && ep->ExceptionRecord->NumberParameters >= 2) {
        unsigned int acc      = (unsigned int)ep->ExceptionRecord->ExceptionInformation[0];
        unsigned int dataAddr = (unsigned int)ep->ExceptionRecord->ExceptionInformation[1];
        p = cl_str(p, " access=");
        p = cl_str(p, acc == 1 ? "WRITE" : (acc == 8 ? "EXEC" : "READ"));
        p = cl_str(p, " dataAddr=");
        p = cl_hex(p, dataAddr);   /* the [null+X] value, e.g. 0xC for the parser race */
    }
    p = cl_str(p, "\r\n  logged=");
    p = cl_u(p, g_filter.counted);
    p = cl_str(p, "/");
    p = cl_u(p, CLF_MAX_COUNTED);
    p = cl_str(p, " noiseRepeats=");
    p = cl_u(p, g_filter.noiseRepeats);
    p = cl_str(p, " otherRepeats=");
    p = cl_u(p, g_filter.otherRepeats);
    p = cl_str(p, " overCap=");
    p = cl_u(p, g_filter.overCap);
    p = cl_str(p, " relogCapped=");
    p = cl_u(p, g_filter.relogCapped);
    p = cl_str(p, " logBytes=");
    p = cl_u(p, g_filter.logBytes);
    p = cl_str(p, "\r\n  eip="); p = cl_sym(p, c->Eip, 1);
    p = cl_str(p, " esp=");      p = cl_hex(p, c->Esp);
    p = cl_str(p, " ebp=");      p = cl_hex(p, c->Ebp);
    p = cl_str(p, "\r\n  eax="); p = cl_hex(p, c->Eax);
    p = cl_str(p, " ebx=");      p = cl_hex(p, c->Ebx);
    p = cl_str(p, " ecx=");      p = cl_hex(p, c->Ecx);
    p = cl_str(p, " edx=");      p = cl_hex(p, c->Edx);
    p = cl_str(p, " esi=");      p = cl_hex(p, c->Esi);
    p = cl_str(p, " edi=");      p = cl_hex(p, c->Edi);
    p = cl_str(p, "\r\n  stack code pointers (esp -> up):\r\n");
    cl_write(g_buf, (DWORD)(p - g_buf));

    words = cl_stack_words(c->Esp, 4096);
    if (words == 0) {
        const char* m = "    <esp is not on this thread's stack; no sweep>\r\n";
        cl_write(m, (DWORD)lstrlenA(m));
    }
    __try {
        unsigned int* sp = (unsigned int*)(UINT_PTR)c->Esp;
        unsigned int i;
        int emitted = 0;
        for (i = 0; i < words && emitted < 96; i++) {
            unsigned int v = sp[i];
            if (v > 0x00401000u && v < 0x7FFF0000u && cl_mod_cached(v)) {
                char* q = g_buf;
                q = cl_str(q, "    +");
                q = cl_hex(q, (unsigned int)(i * 4));
                q = cl_str(q, "  ");
                q = cl_sym(q, v, 0);
                q = cl_str(q, "\r\n");
                cl_write(g_buf, (DWORD)(q - g_buf));
                emitted++;
            }
        }
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        const char* m = "    <stack sweep faulted>\r\n";
        cl_write(m, (DWORD)lstrlenA(m));
    }

    {
        const char* e = "==== END NATIVE CRASH ====\r\n";
        cl_write(e, (DWORD)lstrlenA(e));
    }
}

/*
 * Under the lock: write the recent first-chance records that were not written one by one
 * (clf_ring_take), oldest first, so the log ends with the newest, usually the fatal hit.
 * Module names from the cache only: no loader call on a dying process.
 */
static void cl_flush_locked(const char* reason, unsigned int code, unsigned int maxAgeMs)
{
    unsigned int older = 0;
    int n = clf_ring_take(&g_ring, g_flushRecs, &older), i;
    char* p;
    if (maxAgeMs != 0) {
        /* v6: process exit (DllMain) keeps only the recent records, as v2-v5 did; a
         * contained AV from long before a normal exit is not crash evidence. */
        unsigned int now = GetTickCount();
        int k = 0;
        for (i = 0; i < n; i++)
            if (now - g_flushRecs[i].tickMs <= maxAgeMs) g_flushRecs[k++] = g_flushRecs[i];
        n = k;
    }
    if (n == 0 || g_logHandle == INVALID_HANDLE_VALUE) return;
    if (!cl_budget(maxAgeMs == 0)) return;   /* last chance / crash exit are fatal: they get the reserve */
    p = g_buf;
    p = cl_str(p, "\r\n==== NATIVE CRASH LOGGER (flush: ");
    p = cl_str(p, reason);
    p = cl_str(p, ") ====\r\n  code=");
    p = cl_hex(p, code);
    p = cl_stamp(p);
    p = cl_str(p, "\r\n  recent first-chance records not written one by one, oldest first (");
    p = cl_u(p, (unsigned int)n);
    p = cl_str(p, " held");
    if (older) { p = cl_str(p, ", "); p = cl_u(p, older); p = cl_str(p, " older ones no longer held"); }
    p = cl_str(p, "):\r\n");
    cl_write(g_buf, (DWORD)(p - g_buf));
    for (i = 0; i < n; i++) {
        const clf_rec* x = &g_flushRecs[i];
        p = g_buf;
        p = cl_str(p, "    exAddr=");
        p = cl_sym(p, x->addr, 0);
        p = cl_str(p, " code=");
        p = cl_hex(p, x->code);
        p = cl_str(p, " eip=");
        p = cl_sym(p, x->eip, 0);
        p = cl_str(p, " tid=");
        p = cl_u(p, x->tid);
        p = cl_str(p, " tick=");
        p = cl_u(p, x->tickMs);
        p = cl_str(p, " repeat=");
        p = cl_u(p, x->hits);
        p = cl_str(p, "\r\n");
        cl_write(g_buf, (DWORD)(p - g_buf));
    }
    {
        const char* e = "==== END NATIVE CRASH ====\r\n";
        cl_write(e, (DWORD)lstrlenA(e));
    }
}

static LONG CALLBACK CrashVeh(PEXCEPTION_POINTERS ep)
{
    DWORD code = ep->ExceptionRecord->ExceptionCode;
    if (!clf_is_logged_code(code)) {
        return EXCEPTION_CONTINUE_SEARCH;      /* cheap exit, no lock, for C++/CLR throws */
    }
    if (!cl_lock()) { return EXCEPTION_CONTINUE_SEARCH; }
    if (code == CLF_CODE_ACCESS_VIOLATION && t_sehDepth > 0) {
        /* v6 (v2's rule): an SEH_* wrapper's __except catches this AV (the game-tick guard
         * writes its own record), so it is not written one by one. Kept in the flush ring
         * only, in case a handler further up turns the process exit into a crash. */
        clf_ring_push(&g_ring, (unsigned int)(UINT_PTR)ep->ExceptionRecord->ExceptionAddress, code,
            ep->ContextRecord->Eip, GetCurrentThreadId(), GetTickCount(), 1, 0);
        cl_unlock();
        return EXCEPTION_CONTINUE_SEARCH;
    }
    {
        unsigned int exAddr = (unsigned int)(UINT_PTR)ep->ExceptionRecord->ExceptionAddress;
        unsigned int now = GetTickCount();
        unsigned int hits = 1;
        int written = 0;
        clf_slot* slot = clf_find(&g_filter, exAddr);
        if (slot) {
            /* A repeat: decided without touching any module. */
            if (clf_repeat(&g_filter, slot, code, now, &hits) == CLF_LOG_REPEAT) {
                cl_write_entry(ep, slot->cls != CLF_CLASS_AC ? "first-chance, repeat, noise" : "first-chance, repeat", hits, 0);
                written = 1;
            }
        } else {
            unsigned int base;
            clf_class faultCls = CLF_CLASS_AC;
            const char* leaf = cl_module(exAddr, 1, &base, &faultCls);
            clf_class cls = clf_classify(code, leaf ? faultCls : CLF_CLASS_AC, leaf == NULL,
                leaf == NULL && code == CLF_CODE_ACCESS_VIOLATION ? cl_first_frame_class(ep->ContextRecord->Esp) : CLF_CLASS_AC);
            clf_decision d = clf_new(&g_filter, code, exAddr, cls, now);
            if (d == CLF_LOG_COUNTED)
                cl_write_entry(ep, "first-chance", 1, 0);
            else if (d == CLF_LOG_NOISE)
                cl_write_entry(ep, cls == CLF_CLASS_NETFX
                    ? "first-chance, .NET Framework noise, not counted"
                    : "first-chance, managed (CoreCLR) noise, not counted", 1, 0);
            written = d == CLF_LOG_COUNTED || d == CLF_LOG_NOISE;
        }
        /* Kept for a flush (memory only): the fatal hit is often one not written here. */
        clf_ring_push(&g_ring, exAddr, code, ep->ContextRecord->Eip, GetCurrentThreadId(), now, hits, written);
    }
    cl_unlock();
    return EXCEPTION_CONTINUE_SEARCH;
}

/* Last chance: nothing handled it. Logged, then the filter this one replaced decides. */
static LONG WINAPI CrashLastChance(PEXCEPTION_POINTERS ep)
{
    LONG verdict = EXCEPTION_CONTINUE_SEARCH;
    /* A replaced-then-reasserted filter chain can loop back here; bail out of the cycle. */
    if (t_inUef) { return EXCEPTION_CONTINUE_SEARCH; }
    t_inUef = 1;
    if (cl_lock()) {
        cl_flush_locked("last chance", ep->ExceptionRecord->ExceptionCode, 0);
        if (clf_last_chance(&g_filter) == CLF_LOG_LAST_CHANCE)
            cl_write_entry(ep, "LAST-CHANCE, unhandled", 1, 1);
        cl_unlock();
    }
    if (g_prevFilter && g_prevFilter != CrashLastChance) {
        verdict = g_prevFilter(ep);
    }
    t_inUef = 0;
    return verdict;
}

/*
 * A crash exit is under way (the engine's TerminateProcess / NtTerminateProcess /
 * ExitProcess / RtlExitUserProcess detours, for a crash code): write the recent
 * first-chance records not written one by one. Each record is written once, so the
 * second of two nested exit paths writes nothing. exitCode 0 is the engine's priming
 * call and does nothing.
 */
__declspec(dllexport) void __cdecl
RC_FlushCrashLog(unsigned int exitCode)
{
    if (exitCode == 0) return;
    if (!cl_lock()) return;
    cl_flush_locked("crash exit", exitCode, 0);
    cl_unlock();
}

/* DllMain, process exit (v2-v5 behaviour): ring records from the last 30 s that were not
 * written one by one. AC's own crash handling ends with ExitProcess, sometimes with a code
 * the engine does not count as a crash. Cache-only module names; no loader call. */
static void CrashLoggerFlushOnExit(void)
{
    if (g_logHandle == INVALID_HANDLE_VALUE) { return; }
    if (!cl_lock()) { return; }
    cl_flush_locked("process exit", 0, CL_EXIT_FLUSH_WINDOW_MS);
    cl_unlock();
}

/*
 * Install the VEH once and (re-)assert the last-chance filter. logPath = wide path to the
 * dedicated crash log, opened HERE (a normal P/Invoke, preemptive mode; never from a
 * handler, see the CreateFileW note above) for the life of the process (append, shared
 * for reading, writing and deleting). Safe to call more than once (every engine
 * generation does): a later call re-installs the filter if AC or a runtime replaced it,
 * and a different path switches files. Also (re)builds the module cache.
 */
__declspec(dllexport) void __cdecl
RC_InstallCrashLogger(const wchar_t* logPath)
{
    LPTOP_LEVEL_EXCEPTION_FILTER prev;
    if (logPath && logPath[0]) {
        int i = 0, same = 1;
        for (; logPath[i] && i < MAX_PATH - 1; i++) {
            if (g_crashLogPath[i] != logPath[i]) { same = 0; }
        }
        if (g_crashLogPath[i] != 0) { same = 0; }
        if ((!same || g_logHandle == INVALID_HANDLE_VALUE) && cl_lock()) {
            HANDLE h;
            for (i = 0; logPath[i] && i < MAX_PATH - 1; i++) { g_crashLogPath[i] = logPath[i]; }
            g_crashLogPath[i] = 0;
            h = CreateFileW(g_crashLogPath, FILE_APPEND_DATA,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL, OPEN_ALWAYS,
                FILE_ATTRIBUTE_NORMAL, NULL);
            if (h != INVALID_HANDLE_VALUE) {
                HANDLE old = g_logHandle;
                DWORD size = GetFileSize(h, NULL);   /* the budget counts what is already there */
                g_filter.logBytes = size == INVALID_FILE_SIZE ? 0u : (unsigned int)size;
                g_filter.capMarked = 0;
                g_logHandle = h;
                if (old != INVALID_HANDLE_VALUE) { CloseHandle(old); }
            }
            cl_unlock();
        }
    }
    RC_RefreshCrashLoggerModules();
    if (!g_vehHandle) {
        g_vehHandle = AddVectoredExceptionHandler(1, CrashVeh);
    }
    prev = SetUnhandledExceptionFilter(CrashLastChance);
    if (prev != CrashLastChance) {
        g_prevFilter = prev;
    }
}

/* ── Tagged-text parser guard (native MinHook detour body) ────────────────
 * Null-guards AC's tag-parser consumer FUN_0067D3C0 (live VA 0x0067D3C0)
 * against a null parser-context singleton. DAT_008F881C (AC's "current tag
 * parser context") is unsynchronized; when our off-thread pump triggers a
 * text-generation path while AC's main thread is mid-parse, it races to null
 * and the consumer does `MOV EDI,[EAX+0xC]` with EAX=0 at 0x0067D3DA -> AV
 * READ [null+0xC]. That is the recurring text-parser-race crash (captured
 * 2026-06-03 native-crash.log: code=0xC0000005 exAddr=acclient.exe+0x27D3DA).
 *
 * MUST be native: the consumer is called by AC's main thread for EVERY parsed
 * tag. A MANAGED MinHook detour here (reverse-P/Invoke per tag) reintroduced a
 * NativeAOT fail-fast and crashed in ~7s (2026-05-25, pid 20852). This detour
 * has NO managed transition. The engine installs the hook via MinHook and
 * hands us the trampoline (original) through RC_SetTagParserOriginal BEFORE it
 * enables the hook. If the parser context is null we drop ONE parse run
 * (return 0) instead of dereferencing it; the caller (FUN_0067E150) discards
 * the return value, so dropping it is safe.
 *
 * The hooked function is __cdecl: void* FUN_0067D3C0(void* parserContext).
 */
typedef void* (__cdecl *Fn_TagParserConsume)(void*);
static Fn_TagParserConsume g_tagParserOriginal = NULL;
static volatile LONG       g_tagParserSkips    = 0;

static void* __cdecl RC_TagParserGuard(void* parserContext)
{
    if (parserContext == NULL) {
        InterlockedIncrement(&g_tagParserSkips);
        return NULL;                 /* drop this parse run — no null deref */
    }
    if (g_tagParserOriginal == NULL) {
        return NULL;                 /* not wired yet (pre-SetOriginal) — fail safe */
    }
    return g_tagParserOriginal(parserContext);
}

/* Returns the address of the native detour for the engine to pass to MinHook. */
__declspec(dllexport) void* __cdecl RC_GetTagParserGuardAddress(void)
{
    return (void*)(UINT_PTR)&RC_TagParserGuard;
}

/* Engine calls this with the MinHook trampoline (original) BEFORE enabling. */
__declspec(dllexport) void __cdecl RC_SetTagParserOriginal(void* original)
{
    g_tagParserOriginal = (Fn_TagParserConsume)original;
}

/* Diagnostic: how many null-context parse runs we've dropped. */
__declspec(dllexport) unsigned int __cdecl RC_TagParserSkipCount(void)
{
    return (unsigned int)g_tagParserSkips;
}

/* ── Game-tick guard (v4) ─────────────────────────────────────────────────
 * Client::UseTime (0x00411FA0) is detoured by a MANAGED hook. An AV inside AC's tick
 * (e.g. chatclient.dll+0x1050 copying a ~2 MB chat-server message from a null buffer,
 * 2026-10-11, two clients in the same second) unwinds into that managed frame, which
 * CoreCLR treats as an unhandled exception and fail-fasts; AC's own handlers further
 * up never run. This wrapper catches the AV in native code first.
 *
 * The filter runs on the faulting thread before unwinding, so it records the faulting
 * address, the data address and the module name, and writes a full record (registers +
 * stack sweep) to the crash log through the logger's writer (v6), capped at
 * CL_MAX_GAMETICK records per process. No CreateFileW here either (see the logger). */
typedef struct {
    unsigned int exAddr;
    unsigned int dataAddr;
    char*        module;
    int          moduleCap;
} GT_FAULT;

/* int __thiscall fn(this) — no stack args; EAX is returned whole, as AC's caller tests AL. */
static int __cdecl _helper_thiscall_int_noarg(void* fn_ptr, void* this_ptr)
{
    int result = 0;
    __asm {
        mov  ecx, this_ptr
        call fn_ptr
        mov  result, eax
    }
    return result;
}

static int gt_filter(PEXCEPTION_POINTERS ep, GT_FAULT* f)
{
    if (ep->ExceptionRecord->ExceptionCode != EXCEPTION_ACCESS_VIOLATION) {
        return EXCEPTION_CONTINUE_SEARCH;
    }
    f->exAddr = (unsigned int)(UINT_PTR)ep->ExceptionRecord->ExceptionAddress;
    f->dataAddr = ep->ExceptionRecord->NumberParameters >= 2
        ? (unsigned int)ep->ExceptionRecord->ExceptionInformation[1] : 0;
    if (f->module && f->moduleCap > 0) { f->module[0] = 0; }
    if (cl_lock()) {
        unsigned int base = 0;
        clf_class cls;
        const char* leaf = cl_module(f->exAddr, 1, &base, &cls);
        if (leaf && f->module && f->moduleCap > 0) {
            int k = 0;
            for (; leaf[k] && k < f->moduleCap - 1; k++) { f->module[k] = leaf[k]; }
            f->module[k] = 0;
        }
        if (InterlockedIncrement(&g_gameTickRecords) <= CL_MAX_GAMETICK) {
            cl_write_entry(ep, "AV CAUGHT IN GAME TICK (Client::UseTime guard; tick dropped, client kept running)", 1, 0);
        }
        cl_unlock();
        if (leaf && f->module && f->module[0]) {
            f->exAddr -= base;   /* module-relative when resolved */
        }
    }
    return EXCEPTION_EXECUTE_HANDLER;
}

/* Calls int __thiscall fn(this) under SEH. Returns 1 when the call completed, 0 when an
 * AV was caught; on 0, *out_exAddr is module-relative if module[0] != 0, else absolute. */
__declspec(dllexport) int __cdecl
SEH_GameTickThiscall(void* fn, void* this_ptr, int* out_result,
                     unsigned int* out_exAddr, unsigned int* out_dataAddr,
                     char* module, int moduleCap)
{
    GT_FAULT f;
    f.exAddr = 0; f.dataAddr = 0; f.module = module; f.moduleCap = moduleCap;
    *out_result = 0; *out_exAddr = 0; *out_dataAddr = 0;
    if (module && moduleCap > 0) { module[0] = 0; }
    RC_SEH_ENTER();
    __try {
        *out_result = _helper_thiscall_int_noarg(fn, this_ptr);
    }
    __except(gt_filter(GetExceptionInformation(), &f)) {
        RC_SEH_LEAVE();
        *out_exAddr = f.exAddr;
        *out_dataAddr = f.dataAddr;
        return 0;
    }
    RC_SEH_LEAVE();
    return 1;
}
