// Console driver: SpikeHostExe.exe [cycles] [mode]. LoadLibrary's SpikeHost.dll the
// way an injected loader would and calls SpikeRun.
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>

typedef int (__stdcall *spike_run_fn)(int cycles, int mode);

int main(int argc, char** argv)
{
    int cycles = argc > 1 ? atoi(argv[1]) : 20;
    int mode = argc > 2 ? (int)strtol(argv[2], NULL, 0) : 0;
    HMODULE h = LoadLibraryA("SpikeHost.dll");
    if (!h) { printf("LoadLibrary SpikeHost.dll failed %lu\n", GetLastError()); return 1; }
    spike_run_fn run = (spike_run_fn)GetProcAddress(h, "_SpikeRun@8");
    if (!run) run = (spike_run_fn)GetProcAddress(h, "SpikeRun");
    if (!run) { printf("SpikeRun export missing\n"); return 2; }
    return run(cycles, mode);
}
