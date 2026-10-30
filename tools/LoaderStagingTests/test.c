// Offline checks for Loader.c's staging: CleanStaleStagingFiles rules and CopyGuarded.
// Built with the real Loader.c included, so these are the shipped functions.
#define LOG_DIR "logs"   // anything LoaderLog writes goes here, not C:\Games
#include "Loader.c"

static int fails;
#define CHECK(c, ...) do { if (c) printf("ok   "); else { printf("FAIL "); fails++; } printf(__VA_ARGS__); printf("\n"); } while (0)

static char dir[MAX_PATH];

static void Touch(const char* name, const FILETIME* created)
{
    char p[MAX_PATH];
    _snprintf_s(p, sizeof p, _TRUNCATE, "%s\\%s", dir, name);
    HANDLE h = CreateFileA(p, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (created) SetFileTime(h, created, NULL, NULL);
    CloseHandle(h);
}

static int Exists(const char* name)
{
    char p[MAX_PATH];
    _snprintf_s(p, sizeof p, _TRUNCATE, "%s\\%s", dir, name);
    return GetFileAttributesA(p) != INVALID_FILE_ATTRIBUTES;
}

static volatile LONG stop;
static char racePath[MAX_PATH];
static volatile LONG deleted, refused32, refusedOther;
static DWORD WINAPI Deleter(LPVOID unused)
{
    while (!stop) {
        if (DeleteFileA(racePath)) InterlockedIncrement(&deleted);
        else if (GetLastError() == ERROR_SHARING_VIOLATION) InterlockedIncrement(&refused32);
        else if (GetLastError() != ERROR_FILE_NOT_FOUND) InterlockedIncrement(&refusedOther);
    }
    return 0;
}

int main(int argc, char** argv)
{
    InitializeCriticalSection(&g_logLock);
    GetFullPathNameA("stage", sizeof dir, dir, NULL);
    CreateDirectoryA(dir, NULL);
    DWORD self = GetCurrentProcessId();

    // A live other process (started now), a dead pid, and an old file for the live pid (pid reuse).
    STARTUPINFOA si = { sizeof si };
    PROCESS_INFORMATION pi;
    char cmd[] = "C:\\Windows\\System32\\ping.exe -n 30 127.0.0.1";
    CreateProcessA(NULL, cmd, NULL, NULL, FALSE, CREATE_NO_WINDOW, NULL, NULL, &si, &pi);
    Sleep(200);
    DWORD live = pi.dwProcessId;
    DWORD dead = 0;
    for (DWORD p = 60000; p < 200000; p += 4) {   // an id nothing is using
        HANDLE h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, p);
        if (!h && GetLastError() == ERROR_INVALID_PARAMETER) { dead = p; break; }
        if (h) CloseHandle(h);
    }
    FILETIME year2000 = { 0x0D53C000, 0x01BF53EB };   // older than any running process

    char n[12][96];
    _snprintf_s(n[0], 96, _TRUNCATE, "RynthCore.Engine.p%lu.gen1.dll", self);
    _snprintf_s(n[1], 96, _TRUNCATE, "RynthCore.Engine.p%lu.gen1.pdb", self);
    _snprintf_s(n[2], 96, _TRUNCATE, "RynthCore.Engine.p%lu.gen2.dll", self);
    _snprintf_s(n[3], 96, _TRUNCATE, "RynthCore.Engine.p%lu.gen3.dll", self);   // the one being staged now
    _snprintf_s(n[4], 96, _TRUNCATE, "RynthCore.Engine.p%lu.gen4.dll", live);
    _snprintf_s(n[5], 96, _TRUNCATE, "RynthCore.Engine.p%lu.gen4.pdb", live);
    _snprintf_s(n[6], 96, _TRUNCATE, "RynthCore.Engine.p%lu.gen2.dll", dead);
    _snprintf_s(n[7], 96, _TRUNCATE, "RynthCore.Engine.p%lu.gen2.pdb", dead);
    _snprintf_s(n[8], 96, _TRUNCATE, "RynthCore.Engine.p%lu.gen9.dll", live);   // older than the live process: a reused id
    _snprintf_s(n[9], 96, _TRUNCATE, "RynthCore.Engine.p4.gen1.dll");          // System (pid 4): access denied = leave it
    _snprintf_s(n[10], 96, _TRUNCATE, "RynthCore.Engine.dll.notours");
    for (int i = 0; i < 11; i++) Touch(n[i], i == 8 ? &year2000 : NULL);

    CleanStaleStagingFiles(dir, 3);
    CHECK(!Exists(n[0]) && !Exists(n[1]) && !Exists(n[2]), "own older generations (dll + pdb) deleted");
    CHECK(Exists(n[3]), "own current generation kept");
    CHECK(Exists(n[4]) && Exists(n[5]), "a live client's files kept (pid %lu)", live);
    CHECK(dead != 0 && !Exists(n[6]) && !Exists(n[7]), "a closed client's files deleted (pid %lu)", dead);
    CHECK(!Exists(n[8]), "a file older than the process now using its id deleted");
    CHECK(Exists(n[9]), "pid that can't be opened (access denied) kept");
    CHECK(Exists(n[10]), "names not matching p<pid>.gen<N> untouched");
    TerminateProcess(pi.hProcess, 0);
    WaitForSingleObject(pi.hProcess, 5000);
    CloseHandle(pi.hProcess); CloseHandle(pi.hThread);
    CleanStaleStagingFiles(dir, 3);
    CHECK(!Exists(n[4]) && !Exists(n[5]), "that client's files deleted once it closed");

    // CopyGuarded: a native DLL (LoadLibrary path) copied under a guard, with a thread
    // hammering DeleteFileA the whole time.
    const char* src = argc > 1 ? argv[1] : "C:\\Windows\\SysWOW64\\version.dll";
    int clean = 0, trials = 50;
    for (int t = 0; t < trials; t++) {
        _snprintf_s(racePath, sizeof racePath, _TRUNCATE, "%s\\RynthCore.Engine.p%lu.gen%d.dll", dir, self, 100 + t);
        stop = 0; deleted = 0;
        HANDLE th = CreateThread(NULL, 0, Deleter, NULL, 0, NULL);
        HANDLE guard = CopyGuarded(src, racePath);
        // What CoreCLR does: open read-only, sharing read.
        HANDLE ro = CreateFileA(racePath, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL);
        HMODULE m = guard != INVALID_HANDLE_VALUE ? LoadLibraryExA(racePath, NULL, DONT_RESOLVE_DLL_REFERENCES) : NULL;
        LONG delWhileGuarded = deleted;
        if (guard != INVALID_HANDLE_VALUE) CloseHandle(guard);
        stop = 1;
        WaitForSingleObject(th, INFINITE);
        CloseHandle(th);
        // Mapped: still undeletable after the guard closes.
        BOOL delAfter = DeleteFileA(racePath);
        if (guard != INVALID_HANDLE_VALUE && ro != INVALID_HANDLE_VALUE && m && delWhileGuarded == 0 && !delAfter) clean++;
        if (ro != INVALID_HANDLE_VALUE) CloseHandle(ro);
        if (m) FreeLibrary(m);
        DeleteFileA(racePath);
    }
    CHECK(clean == trials, "guarded copy: %d/%d trials loaded with no delete getting through (refused %ld, other errors %ld)",
        clean, trials, refused32, refusedOther);

    // A copy that fails (missing source) leaves nothing behind and reports the error.
    _snprintf_s(racePath, sizeof racePath, _TRUNCATE, "%s\\RynthCore.Engine.p%lu.gen999.dll", dir, self);
    HANDLE bad = CopyGuarded("C:\\nope\\missing.dll", racePath);
    CHECK(bad == INVALID_HANDLE_VALUE && GetLastError() != 0 && GetFileAttributesA(racePath) == INVALID_FILE_ATTRIBUTES,
        "missing source: no guard, no partial file (error %lu)", GetLastError());

    printf(fails ? "\n%d FAILED\n" : "\nALL PASSED\n", fails);
    return fails;
}
