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

BOOL APIENTRY DllMain(HMODULE hModule, DWORD ul_reason, LPVOID lpReserved)
{
    (void)hModule; (void)ul_reason; (void)lpReserved;
    return TRUE;
}

/* ── Cdecl helpers ─────────────────────────────────────────────────────── */

/* void* __cdecl fn(unsigned int id)  — e.g. GetWeenieObject */
typedef void* (__cdecl *Fn_CdeclPtrUint)(unsigned int);
__declspec(dllexport) int __cdecl
SEH_CdeclPtrUint(Fn_CdeclPtrUint fn, unsigned int arg, void** out_result)
{
    *out_result = NULL;
    __try {
        *out_result = fn(arg);
    }
    __except(GetExceptionCode() == EXCEPTION_ACCESS_VIOLATION
             ? EXCEPTION_EXECUTE_HANDLER : EXCEPTION_CONTINUE_SEARCH) {
        *out_result = NULL;
        return 0;
    }
    return 1;
}

/* void* __cdecl fn()  — e.g. GetCombatSystem */
typedef void* (__cdecl *Fn_CdeclPtrVoid)(void);
__declspec(dllexport) int __cdecl
SEH_CdeclPtrVoid(Fn_CdeclPtrVoid fn, void** out_result)
{
    *out_result = NULL;
    __try {
        *out_result = fn();
    }
    __except(GetExceptionCode() == EXCEPTION_ACCESS_VIOLATION
             ? EXCEPTION_EXECUTE_HANDLER : EXCEPTION_CONTINUE_SEARCH) {
        *out_result = NULL;
        return 0;
    }
    return 1;
}

/* void __cdecl fn(unsigned int, unsigned char)  — e.g. CastSpell(spellId, targetIsSelected) */
typedef void (__cdecl *Fn_CdeclVoidUintByte)(unsigned int, unsigned char);
__declspec(dllexport) int __cdecl
SEH_CdeclVoidUintByte(Fn_CdeclVoidUintByte fn, unsigned int arg1, unsigned char arg2)
{
    __try {
        fn(arg1, arg2);
    }
    __except(GetExceptionCode() == EXCEPTION_ACCESS_VIOLATION
             ? EXCEPTION_EXECUTE_HANDLER : EXCEPTION_CONTINUE_SEARCH) {
        return 0;
    }
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
    __try {
        ((Fn_FreeHandsCast)fn)(this_ptr, 0, spellId, targetId);
    }
    __except(GetExceptionCode() == EXCEPTION_ACCESS_VIOLATION
             ? EXCEPTION_EXECUTE_HANDLER : EXCEPTION_CONTINUE_SEARCH) {
        return 0;
    }
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
    __try {
        *out_result = _helper_thiscall_byte_uint(fn, this_ptr, arg);
    }
    __except(GetExceptionCode() == EXCEPTION_ACCESS_VIOLATION
             ? EXCEPTION_EXECUTE_HANDLER : EXCEPTION_CONTINUE_SEARCH) {
        *out_result = 0;
        return 0;
    }
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
    __try {
        *out_result = _helper_thiscall_uint_noarg(fn, this_ptr);
    }
    __except(GetExceptionCode() == EXCEPTION_ACCESS_VIOLATION
             ? EXCEPTION_EXECUTE_HANDLER : EXCEPTION_CONTINUE_SEARCH) {
        *out_result = 0;
        return 0;
    }
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
    __try {
        *out_result = _helper_thiscall_int_uint_ptr(fn, this_ptr, arg1, arg2);
    }
    __except(GetExceptionCode() == EXCEPTION_ACCESS_VIOLATION
             ? EXCEPTION_EXECUTE_HANDLER : EXCEPTION_CONTINUE_SEARCH) {
        *out_result = 0;
        return 0;
    }
    return 1;
}

/* ── Process-wide native crash logger (VEH) ───────────────────────────────
 * Logs the faulting 32-bit context + a module-resolved stack sweep to a
 * dedicated file BEFORE the process dies, for the fatal exceptions that bypass
 * NativeAOT's managed handlers: 0xC0000005 (AV / corrupted-state) and
 * 0xC0000602 (STATUS_FAIL_FAST from RaiseFailFastException — e.g. an unhandled
 * managed exception or a stack-walk fail-fast in the plugin/engine).
 *
 * This is PURE NATIVE: no managed callback / reverse-P/Invoke transition, so
 * unlike the removed managed CrashLogger VEH it cannot itself re-trigger a
 * NativeAOT fail-fast on an AC-owned thread. It only OBSERVES — returns
 * EXCEPTION_CONTINUE_SEARCH so AC's own SEH / the runtime still proceed exactly
 * as before. Uses only kernel32 APIs + hand-rolled formatting (no CRT stdio /
 * user32) so the build link line stays unchanged. Hardened: stack sweep in
 * __try/__except, capped log count, dedicated file (no contention with the
 * managed log). x86 (32-bit) context fields only — matches the WOW64 target.
 */
static wchar_t       g_crashLogPath[MAX_PATH];
static volatile LONG g_crashCount = 0;
static PVOID         g_vehHandle  = NULL;

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

/* Append "module.dll+0xRVA", or "0xADDR" if the address is in no module. */
static char* cl_sym(char* p, unsigned int addr)
{
    HMODULE hm = NULL;
    /* GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS(0x4) | UNCHANGED_REFCOUNT(0x2) */
    if (GetModuleHandleExW(0x4 | 0x2, (LPCWSTR)(UINT_PTR)addr, &hm) && hm) {
        wchar_t wpath[MAX_PATH];
        DWORD n = GetModuleFileNameW(hm, wpath, MAX_PATH);
        DWORD i, leaf = 0;
        for (i = 0; i < n; i++) { if (wpath[i] == L'\\' || wpath[i] == L'/') leaf = i + 1; }
        for (i = leaf; i < n; i++) { *p++ = (char)wpath[i]; }
        p = cl_str(p, "+");
        p = cl_hex(p, addr - (unsigned int)(UINT_PTR)hm);
    } else {
        p = cl_hex(p, addr);
    }
    return p;
}

static LONG CALLBACK CrashVeh(PEXCEPTION_POINTERS ep)
{
    DWORD code = ep->ExceptionRecord->ExceptionCode;
    if (code != 0xC0000005 && code != 0xC0000602 && code != 0xC0000409) {
        return EXCEPTION_CONTINUE_SEARCH;
    }
    if (InterlockedIncrement(&g_crashCount) > 24) {
        return EXCEPTION_CONTINUE_SEARCH;
    }

    {
        HANDLE h = CreateFileW(g_crashLogPath, FILE_APPEND_DATA,
            FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_ALWAYS,
            FILE_ATTRIBUTE_NORMAL, NULL);
        if (h == INVALID_HANDLE_VALUE) { return EXCEPTION_CONTINUE_SEARCH; }
        SetFilePointer(h, 0, NULL, FILE_END);

        {
            char buf[4096];
            char* p = buf;
            CONTEXT* c = ep->ContextRecord;
            unsigned int exAddr = (unsigned int)(UINT_PTR)ep->ExceptionRecord->ExceptionAddress;
            DWORD wr;

            p = cl_str(p, "\r\n==== NATIVE CRASH LOGGER (SEH VEH) ====\r\n  code=");
            p = cl_hex(p, code);
            p = cl_str(p, " exAddr=");
            p = cl_sym(p, exAddr);
            p = cl_str(p, " tid=");
            p = cl_u(p, GetCurrentThreadId());
            {
                SYSTEMTIME st; GetLocalTime(&st);
                p = cl_str(p, " time=");
                p = cl_u(p, st.wHour);   p = cl_str(p, ":");
                if (st.wMinute < 10) { p = cl_str(p, "0"); } p = cl_u(p, st.wMinute); p = cl_str(p, ":");
                if (st.wSecond < 10) { p = cl_str(p, "0"); } p = cl_u(p, st.wSecond);
            }
            if ((code == 0xC0000005 || code == 0xC0000409) && ep->ExceptionRecord->NumberParameters >= 2) {
                unsigned int acc      = (unsigned int)ep->ExceptionRecord->ExceptionInformation[0];
                unsigned int dataAddr = (unsigned int)ep->ExceptionRecord->ExceptionInformation[1];
                p = cl_str(p, " access=");
                p = cl_str(p, acc == 1 ? "WRITE" : (acc == 8 ? "EXEC" : "READ"));
                p = cl_str(p, " dataAddr=");
                p = cl_hex(p, dataAddr);   /* the [null+X] value, e.g. 0xC for the parser race */
            }
            p = cl_str(p, "\r\n  eip="); p = cl_sym(p, c->Eip);
            p = cl_str(p, " esp=");      p = cl_hex(p, c->Esp);
            p = cl_str(p, " ebp=");      p = cl_hex(p, c->Ebp);
            p = cl_str(p, "\r\n  eax="); p = cl_hex(p, c->Eax);
            p = cl_str(p, " ebx=");      p = cl_hex(p, c->Ebx);
            p = cl_str(p, " ecx=");      p = cl_hex(p, c->Ecx);
            p = cl_str(p, " edx=");      p = cl_hex(p, c->Edx);
            p = cl_str(p, " esi=");      p = cl_hex(p, c->Esi);
            p = cl_str(p, " edi=");      p = cl_hex(p, c->Edi);
            p = cl_str(p, "\r\n  stack code pointers (esp -> up):\r\n");
            WriteFile(h, buf, (DWORD)(p - buf), &wr, NULL);

            __try {
                unsigned int* sp = (unsigned int*)c->Esp;
                int i, emitted = 0;
                for (i = 0; i < 4096 && emitted < 96; i++) {
                    unsigned int v = sp[i];
                    if (v > 0x00401000u && v < 0x7FFF0000u) {
                        HMODULE hm = NULL;
                        if (GetModuleHandleExW(0x4 | 0x2, (LPCWSTR)(UINT_PTR)v, &hm) && hm) {
                            char* q = buf;
                            q = cl_str(q, "    +");
                            q = cl_hex(q, (unsigned int)(i * 4));
                            q = cl_str(q, "  ");
                            q = cl_sym(q, v);
                            q = cl_str(q, "\r\n");
                            WriteFile(h, buf, (DWORD)(q - buf), &wr, NULL);
                            emitted++;
                        }
                    }
                }
            }
            __except (EXCEPTION_EXECUTE_HANDLER) {
                const char* m = "    <stack sweep faulted>\r\n";
                WriteFile(h, m, (DWORD)lstrlenA(m), &wr, NULL);
            }

            {
                const char* e = "==== END NATIVE CRASH ====\r\n";
                WriteFile(h, e, (DWORD)lstrlenA(e), &wr, NULL);
            }
        }
        CloseHandle(h);
    }
    return EXCEPTION_CONTINUE_SEARCH;
}

/* Install the VEH once. logPath = wide path to the dedicated crash log. */
__declspec(dllexport) void __cdecl
RC_InstallCrashLogger(const wchar_t* logPath)
{
    if (g_vehHandle) { return; }
    if (logPath) {
        int i = 0;
        for (; logPath[i] && i < MAX_PATH - 1; i++) { g_crashLogPath[i] = logPath[i]; }
        g_crashLogPath[i] = 0;
    }
    g_vehHandle = AddVectoredExceptionHandler(1, CrashVeh);
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
