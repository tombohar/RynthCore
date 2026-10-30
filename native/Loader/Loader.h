#pragma once
#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#define LOADER_VERSION "2.1"

// Loader.c
void LoaderLog(const char* level, const char* fmt, ...);
long LoaderFreeAddressSpaceMb(long* largestMb);
const char* LoaderDirectory(void);
int LoaderGeneration(void);

// Hooks.c: MinHook facade (see the header comment there)
int HooksInit(const char* loaderDir);
void HooksBeginGeneration(int generation);
void HooksPassThroughAll(void);
void HooksFlush(void);

// Services.c: permanent window subclass + plugin API stubs
void ServicesReleaseAll(void);

// Clr.c: CoreCLR host for a managed engine
int ClrStart(const char* loaderDir);
int ClrLoadEngine(int generation, const char* stagedPath);
void ClrUnloadEngine(void);
void* ClrEngineExport(const char* name);
