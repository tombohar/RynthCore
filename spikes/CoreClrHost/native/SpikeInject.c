// SpikeInject: starts acclient.exe suspended, LoadLibrary's SpikeHost.dll into it with
// RYNTH_SPIKE_AUTORUN set, resumes it, waits, then terminates it.
//   SpikeInject.exe <acclient.exe> <SpikeHost.dll> <cycles,mode,delayMs> <waitSeconds> [acclient args...]
#include <windows.h>
#include <stdio.h>
#include <string.h>

int main(int argc, char** argv)
{
    if (argc < 5) { printf("usage: SpikeInject <acclient.exe> <SpikeHost.dll> <cycles,mode,delayMs> <waitSeconds> [args]\n"); return 1; }
    const char* exe = argv[1];
    const char* dll = argv[2];
    int waitSeconds = atoi(argv[4]);
    SetEnvironmentVariableA("RYNTH_SPIKE_AUTORUN", argv[3]);

    char cmd[4096];
    _snprintf_s(cmd, sizeof cmd, _TRUNCATE, "\"%s\"", exe);
    for (int i = 5; i < argc; i++) { strcat_s(cmd, sizeof cmd, " "); strcat_s(cmd, sizeof cmd, argv[i]); }
    char dir[MAX_PATH];
    strcpy_s(dir, sizeof dir, exe);
    *strrchr(dir, '\\') = 0;

    STARTUPINFOA si = { sizeof si };
    PROCESS_INFORMATION pi;
    if (!CreateProcessA(exe, cmd, NULL, NULL, FALSE, CREATE_SUSPENDED, NULL, dir, &si, &pi)) {
        printf("CreateProcess failed %lu\n", GetLastError()); return 2;
    }
    printf("SPIKE_PID=%lu\n", pi.dwProcessId);

    size_t len = strlen(dll) + 1;
    void* remote = VirtualAllocEx(pi.hProcess, NULL, len, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    WriteProcessMemory(pi.hProcess, remote, dll, len, NULL);
    FARPROC loadLib = GetProcAddress(GetModuleHandleA("kernel32.dll"), "LoadLibraryA");
    HANDLE t = CreateRemoteThread(pi.hProcess, NULL, 0, (LPTHREAD_START_ROUTINE)loadLib, remote, 0, NULL);
    if (!t) { printf("CreateRemoteThread failed %lu\n", GetLastError()); TerminateProcess(pi.hProcess, 1); return 3; }
    WaitForSingleObject(t, 30000);
    DWORD mod = 0;
    GetExitCodeThread(t, &mod);
    printf("LoadLibrary in target -> 0x%08lx\n", mod);
    CloseHandle(t);
    ResumeThread(pi.hThread);

    DWORD r = WaitForSingleObject(pi.hProcess, waitSeconds * 1000);
    if (r == WAIT_OBJECT_0) {
        DWORD code = 0;
        GetExitCodeProcess(pi.hProcess, &code);
        printf("acclient exited by itself, code 0x%08lx\n", code);
    } else {
        TerminateProcess(pi.hProcess, 0);
        printf("acclient terminated after %d s\n", waitSeconds);
    }
    return 0;
}
