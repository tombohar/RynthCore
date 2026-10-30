// TestClient: starts acclient.exe suspended, injects a DLL, optionally calls one of its
// exports on a remote thread (like RynthCore.Injector does with RynthCoreInit), resumes
// the client, then waits and (optionally) terminates it. For loader/engine tests that must
// not touch a real account: point the client at a closed port and no login happens.
//
//   TestClient.exe <acclient.exe> <dll> <export|-> <seconds> [acclient args...]
//     seconds > 0: terminate the client after that many seconds
//     seconds = 0: leave it running and exit (prints TEST_PID=<pid>)
//
// Environment variables set here are inherited by the client (e.g. RYNTH_SPIKE_AUTORUN).
#include <windows.h>
#include <stdio.h>
#include <string.h>

static DWORD RemoteCall(HANDLE process, void* fn, void* arg, DWORD timeoutMs, DWORD* exitCode)
{
    HANDLE t = CreateRemoteThread(process, NULL, 0, (LPTHREAD_START_ROUTINE)fn, arg, 0, NULL);
    if (!t) return GetLastError();
    DWORD r = WaitForSingleObject(t, timeoutMs);
    GetExitCodeThread(t, exitCode);
    CloseHandle(t);
    return r == WAIT_OBJECT_0 ? 0 : WAIT_TIMEOUT;
}

int main(int argc, char** argv)
{
    if (argc < 5) {
        printf("usage: TestClient <acclient.exe> <dll> <export|-> <seconds> [acclient args...]\n");
        return 1;
    }
    const char* exe = argv[1];
    const char* dll = argv[2];
    const char* exportName = argv[3];
    int seconds = atoi(argv[4]);

    char cmd[4096];
    _snprintf_s(cmd, sizeof cmd, _TRUNCATE, "\"%s\"", exe);
    for (int i = 5; i < argc; i++) { strcat_s(cmd, sizeof cmd, " "); strcat_s(cmd, sizeof cmd, argv[i]); }
    char dir[MAX_PATH];
    strcpy_s(dir, sizeof dir, exe);
    *strrchr(dir, '\\') = 0;

    // The export's RVA, from a local image-only mapping of the DLL.
    DWORD exportRva = 0;
    if (strcmp(exportName, "-") != 0) {
        HMODULE local = LoadLibraryExA(dll, NULL, DONT_RESOLVE_DLL_REFERENCES);
        if (!local) { printf("cannot map %s locally (%lu)\n", dll, GetLastError()); return 2; }
        FARPROC p = GetProcAddress(local, exportName);
        if (!p) { printf("%s has no export %s\n", dll, exportName); return 2; }
        exportRva = (DWORD)((char*)p - (char*)local);
        FreeLibrary(local);
    }

    // Marks the client as a test client in its log (the loader logs "TestClient: ...").
    if (GetEnvironmentVariableA("RYNTHCORE_TEST_CLIENT", NULL, 0) == 0)
        SetEnvironmentVariableA("RYNTHCORE_TEST_CLIENT", "tools\\TestClient (no login)");
    STARTUPINFOA si = { sizeof si };
    PROCESS_INFORMATION pi;
    if (!CreateProcessA(exe, cmd, NULL, NULL, FALSE, CREATE_SUSPENDED, NULL, dir, &si, &pi)) {
        printf("CreateProcess failed %lu\n", GetLastError());
        return 3;
    }
    printf("TEST_PID=%lu\n", pi.dwProcessId);
    fflush(stdout);

    // Its own launch context, naming a dummy account and NO character. Without one the engine
    // falls back to the shared %APPDATA%\RynthCore\launch_context.json - whatever the launcher
    // last launched (a real character) - and arms auto-login for it (2026-09-29).
    char ctxPath[MAX_PATH] = "";
    {
        char appdata[MAX_PATH];
        if (GetEnvironmentVariableA("APPDATA", appdata, sizeof appdata) > 0) {
            char dir[MAX_PATH];
            _snprintf_s(dir, sizeof dir, _TRUNCATE, "%s\\RynthCore\\launch_contexts", appdata);
            CreateDirectoryA(dir, NULL);
            _snprintf_s(ctxPath, sizeof ctxPath, _TRUNCATE, "%s\\launch_context_%lu.json", dir, pi.dwProcessId);
            FILE* f = NULL;
            if (fopen_s(&f, ctxPath, "w") == 0 && f) {
                fprintf(f, "{\"ProcessId\":%lu,\"AccountName\":\"test-client\",\"ServerName\":\"test-client\","
                    "\"TargetCharacter\":\"\",\"SkipLoginLogos\":false,\"OnLoginCommands\":null,\"OnLoginWaitMs\":0}",
                    pi.dwProcessId);
                fclose(f);
            }
        }
    }

    size_t len = strlen(dll) + 1;
    void* remote = VirtualAllocEx(pi.hProcess, NULL, len, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    WriteProcessMemory(pi.hProcess, remote, dll, len, NULL);
    DWORD base = 0;
    DWORD err = RemoteCall(pi.hProcess, (void*)GetProcAddress(GetModuleHandleA("kernel32.dll"), "LoadLibraryA"),
        remote, 30000, &base);
    if (err || !base) {
        printf("LoadLibrary in the client failed (err %lu, base 0x%08lx)\n", err, base);
        TerminateProcess(pi.hProcess, 1);
        return 4;
    }
    printf("loaded at 0x%08lx\n", base);

    if (exportRva) {
        DWORD rc = 0;
        err = RemoteCall(pi.hProcess, (void*)(ULONG_PTR)(base + exportRva), NULL, 60000, &rc);
        printf("%s -> %s rc=%lu\n", exportName, err ? "TIMEOUT" : "returned", rc);
    }
    ResumeThread(pi.hThread);
    fflush(stdout);

    if (seconds <= 0) return 0;
    DWORD r = WaitForSingleObject(pi.hProcess, seconds * 1000);
    if (r == WAIT_OBJECT_0) {
        DWORD code = 0;
        GetExitCodeProcess(pi.hProcess, &code);
        printf("client exited by itself, code 0x%08lx\n", code);
    } else {
        TerminateProcess(pi.hProcess, 0);
        printf("client terminated after %d s\n", seconds);
    }
    if (ctxPath[0]) DeleteFileA(ctxPath);
    return 0;
}
