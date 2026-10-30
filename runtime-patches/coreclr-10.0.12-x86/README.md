# Patched CoreCLR 10.0.12 (x86)

`coreclr.dll`, `mscordaccore.dll` (the DAC) and `mscordbi.dll` built from dotnet/runtime **v10.0.12**
with one change, `tls-no-reuse.patch`. `scripts\Publish-EngineCoreClr.ps1` lays them over the
runtime pack's own files in `Runtime\dotnet` (the installer's staging and a full local deploy). The
official ReadyToRun `System.Private.CoreLib.dll` and `clrjit.dll` stay: their source is unchanged.

File version 10.0.1226.52301, product version `@Commit: f2f2e4642…` (the patch commit), so a crash
event or dump shows which runtime it was.

## Why

Clients that had hot-reloaded the engine crashed minutes later: random statics of live engine and
plugin types read null or garbage, then an access violation (.NET Runtime event 1023/1026, a 1 GB
`clr_<pid>_*.dmp`). It is a CoreCLR bug, open upstream as dotnet/runtime #135441 (also #135588,
#134928; Microsoft's fix, PR #134983, was unmerged on 2026-10-23 and in no released 10.0.x; 10.0.13
has the bug too).

When a thread exits, `FreeLoaderAllocatorHandlesForTLSData` frees each of its collectible
thread-static handles against whichever LoaderAllocator *now* owns that TLS index. Unloading a context
only marks its indices cleared, and `GetTLSIndexForThreadStatic` hands a cleared index to the next
type that asks. So a thread that touched a thread static before a hot reload (a ThreadPool worker,
retiring ~20 s after it goes idle) frees an unrelated live slot in the new generation's handle table
when it exits, and whatever was kept alive there is collected.

The patch: a cleared collectible index is never reused (always `g_NextTLSSlot++`). A thread's
leftover handles then only ever point at cleared indices, which `CollectibleEntries()` skips, and
they are dropped with the thread's arrays. Cost: the index space grows by the number of collectible
types with thread statics per generation (a few bytes per thread per type).

## Tested (2026-10-23)

- Offline repro `C:\Projects\reloadbug` (Host.dll + Victim.dll, `run.ps1`, `test-patched.ps1`): stock
  10.0.12 and 10.0.13 corrupt 16 statics per round or crash (AV); patched, 0 in 300 rounds in every
  mode (dedicated thread, statics in the reused slots, ThreadPool workers retiring); the control
  (thread exits before the unload) is clean on both.
- Freya (test client) on the patched runtime: 20 engine hot reloads 4 minutes apart, no crash, no
  dump, each old generation unloaded (+38 MB). On the stock runtime that morning both hot reloads
  crashed within 3-16 minutes.

## Build it again

```
git clone --depth 1 -b v10.0.12 https://github.com/dotnet/runtime C:\Projects\rt   (core.longpaths true)
git -C C:\Projects\rt am tls-no-reuse.patch
C:\Projects\rt\build.cmd -subset clr.runtime -arch x86 -c Release /p:NuGetAudit=false /p:OfficialBuildId=20261023.1
```

Output: `artifacts\bin\coreclr\windows.x86.Release\` (symbols in `PDB\`; kept on Tom's PC at
`C:\Projects\runtime-symbols\coreclr-10.0.1226.52301`). `NuGetAudit=false`: two build-time tasks use a
package with advisories published after the tag; nothing of them ships. Needs ~10 GB free.

## When to drop it

When a 10.0.x with the upstream fix ships: move the runtime pack to it and delete this folder.
A runtime pack of another version than 10.0.12 makes `Publish-EngineCoreClr.ps1` refuse (a coreclr
must match its CoreLib): rebuild the patch for that version, or drop it.
