// ThreadPeek: where is a (32-bit) thread right now? Suspends it a few times and prints EIP,
// the module+offset it falls in, and the return-address-looking values on its stack.
//   ThreadPeek.exe <pid> <tid> [samples]
#include <windows.h>
#include <psapi.h>
#include <stdio.h>

static HMODULE g_mods[1024];
static MODULEINFO g_info[1024];
static char g_names[1024][64];
static int g_nmods;

static void LoadModules(HANDLE p)
{
    DWORD needed = 0;
    EnumProcessModulesEx(p, g_mods, sizeof g_mods, &needed, LIST_MODULES_ALL);
    g_nmods = (int)(needed / sizeof(HMODULE));
    for (int i = 0; i < g_nmods; i++) {
        GetModuleInformation(p, g_mods[i], &g_info[i], sizeof g_info[i]);
        GetModuleBaseNameA(p, g_mods[i], g_names[i], sizeof g_names[i]);
    }
}

static const char* Where(DWORD a, char* buf, size_t cap)
{
    for (int i = 0; i < g_nmods; i++) {
        DWORD b = (DWORD)(ULONG_PTR)g_info[i].lpBaseOfDll;
        if (a >= b && a < b + g_info[i].SizeOfImage) {
            _snprintf_s(buf, cap, _TRUNCATE, "%s+0x%lx", g_names[i], a - b);
            return buf;
        }
    }
    return NULL;
}

int main(int argc, char** argv)
{
    if (argc < 3) { printf("usage: ThreadPeek <pid> <tid> [samples]\n"); return 1; }
    DWORD pid = strtoul(argv[1], NULL, 0), tid = strtoul(argv[2], NULL, 0);
    int samples = argc > 3 ? atoi(argv[3]) : 5;
    HANDLE p = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, FALSE, pid);
    HANDLE t = OpenThread(THREAD_SUSPEND_RESUME | THREAD_GET_CONTEXT, FALSE, tid);
    if (!p || !t) { printf("open failed %lu\n", GetLastError()); return 2; }
    LoadModules(p);
    for (int s = 0; s < samples; s++) {
        SuspendThread(t);
        CONTEXT c = { 0 };
        c.ContextFlags = CONTEXT_CONTROL | CONTEXT_INTEGER;
        GetThreadContext(t, &c);
        DWORD stack[256] = { 0 };
        SIZE_T got = 0;
        ReadProcessMemory(p, (void*)(ULONG_PTR)c.Esp, stack, sizeof stack, &got);
        ResumeThread(t);
        char buf[128];
        const char* w = Where(c.Eip, buf, sizeof buf);
        printf("sample %d: eip=0x%08lx %s  esp=0x%08lx eax=%08lx ecx=%08lx\n", s, c.Eip, w ? w : "(not in a module)", c.Esp, c.Eax, c.Ecx);
        int shown = 0;
        for (int i = 0; i < (int)(got / 4) && shown < 16; i++) {
            const char* m = Where(stack[i], buf, sizeof buf);
            if (m) { printf("    [esp+%03x] %s\n", i * 4, m); shown++; }
        }
        Sleep(200);
    }
    return 0;
}
