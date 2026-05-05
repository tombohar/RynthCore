# RynthCore — Claude Context

A modern .NET 9 NativeAOT framework for Asheron's Call client modding. Replaces Decal + UBService with a self-contained injection system. Hooks D3D9 EndScene to render an ImGui overlay inside the AC client process.

---

## Project Layout

```
C:\Projects\RynthCore\
└── src\
    ├── RynthCore.Injector\       Console app — injects Engine into acclient.exe
    │   └── Program.cs          LoadLibrary + CreateRemoteThread injection
    └── RynthCore.Engine\         NativeAOT x86 DLL — lives inside acclient.exe
        ├── EntryPoint.cs       Exported RynthCoreInit, preloads native DLLs, spawns init thread
        ├── D3D9\
        │   ├── D3D9VTable.cs   Temp device to harvest vtable pointers
        │   ├── EndSceneHook.cs MinHook detour on EndScene
        │   └── TestRenderer.cs v0.1 proof-of-life, kept for reference
        └── ImGui\
            ├── ImGuiController.cs  Context creation, frame loop orchestration
            ├── DX9Backend.cs       Renders ImGui draw data via raw D3D9 vtable calls
            └── Win32Backend.cs     WndProc subclass for mouse/keyboard input
```

Deploy folder: `C:\Games\RynthCore\`

---

## Build

```bash
# Engine (NativeAOT — slow first build, ~2 min)
cd src\RynthCore.Engine
dotnet publish -c Release

# Injector
cd src\RynthCore.Injector
dotnet publish -c Release
```

Both projects target **x86** only. The Engine is NativeAOT (`PublishAot=true`) — it has no .NET runtime dependency and injects as a plain native DLL.

Deploy output:
```
C:\Games\RynthCore\
├── RynthCore.Engine.dll      ← Engine publish output
├── RynthCore.Injector.exe    ← Injector publish output
├── minhook.x86.dll         ← MinHook (x86), preloaded by EntryPoint
└── cimgui.dll              ← ImGui C bindings (x86, docking branch)
```

### Logging

Unified log file: `C:\Games\RynthCore\Logs\RynthCore.log`

All RynthCore components (Engine, Loader, Injector, plugins, CrashLogger) append to this single file. Each line is tagged with its origin — `[engine]`, `[loader]`, `[injector]` — so the source is unambiguous when the engine and loader DLLs are writing concurrently from inside acclient.exe. The active log is rotated to `RynthCore.log.old` on engine startup if it exceeds 5 MB. The path is centralised in `src/RynthCore.Engine/LogPaths.cs`; the Loader and Injector projects mirror the constants because they don't reference the Engine assembly.

Crash logging:
- `CrashLogger.cs` installs a Win32 Vectored Exception Handler that catches AVs and other SEH faults NativeAOT can't surface. Writes a `==== CRASH ====` banner with exception code, faulting module + RVA, register dump, EBP frame walk, and ESP stack sweep.
- `EntryPoint.InstallManagedExceptionHandlers` hooks `AppDomain.UnhandledException` and `TaskScheduler.UnobservedTaskException` so managed exceptions escaping a worker thread (or unobserved task continuations) get logged with full stack before the process terminates.

---

## Key Technical Facts

### NativeAOT / x86
- .NET 9, `net9.0-windows`, `PlatformTarget=x86`, `RuntimeIdentifier=win-x86`
- `AllowUnsafeBlocks=true`, `PublishAot=true`, `TrimMode=link`
- `ImGui.NET` assembly is trim-rooted to preserve P/Invoke bindings
- `SetWindowLongA` not `SetWindowLongPtrA` — the Ptr variant doesn't exist on x86

### Injection flow
1. `RynthCore.Injector.exe` calls `CreateRemoteThread(LoadLibrary, "RynthCore.Engine.dll")`
2. `RynthCoreInit` export fires under loader lock — immediately spawns background thread
3. Init thread sleeps 2s (lets AC finish D3D9 init), preloads minhook + cimgui, then installs EndScene hook

### D3D9 hook
- Vtable discovery: creates a throwaway device via `D3D9VTable.cs`, captures 119 vtable entries
- Hook installed via MinHook on `EndScene` (vtable index 42)
- All D3D9 calls go through cached delegates (stdcall, `this` as first param) — never via COM wrappers
- State save/restore is manual (get/set each render state) — D3D9 state blocks don't work reliably inside EndScene

### ImGui / cimgui version situation
- ImGui.NET NuGet: **1.91.6.1**
- cimgui.dll must match: use `%USERPROFILE%\.nuget\packages\imgui.net\1.91.6.1\runtimes\win-x86\native\cimgui.dll`
- Post-1.90 struct layout: `ImDrawData.CmdLists` is an **ImVector (12 bytes)**, not a raw pointer (4 bytes)
- `DX9Backend.cs` reads `ImDrawData` fields from native memory at confirmed offsets as a safety fallback:
  - `+4`  = CmdListsCount
  - `+16` = CmdLists array pointer
  - `+20/24` = DisplayPos (X, Y)
  - `+28/32` = DisplaySize (X, Y)

### ImGui input API
- Uses the **modern** API: `AddKeyEvent` / `AddMouseButtonEvent` / `AddMousePosEvent`
- `KeyMap` / `KeysDown` / `MouseDown` arrays were removed in ImGui 1.87 — do not use them
- WndProc subclass via `SetWindowLongA` (not Ptr) in `Win32Backend.cs`

### DX9Backend render notes
- `DrawIndexedPrimitiveUP` takes user-memory pointers directly — **do not** call `SetStreamSource(null)` before it, this crashes the driver
- Vertex format: `D3DFVF_XYZ | D3DFVF_DIFFUSE | D3DFVF_TEX1` — xyz (3 floats) + BGRA color (uint) + uv (2 floats)
- ImGui gives RGBA pixel data; D3D9 `A8R8G8B8` is BGRA in memory — swizzle R↔B on upload
- Color conversion per vertex: `(c & 0xFF00FF00) | ((c & 0x00FF0000) >> 16) | ((c & 0x000000FF) << 16)`
- Font texture: `POOL_MANAGED`, created once in `Init`, `TexID` set via `io.Fonts.SetTexID`

### Multi-viewport (docking/undocking outside AC window)
- `DockingEnable` is on
- `ViewportsEnable` is commented out — requires cimgui built with `IMGUI_ENABLE_VIEWPORTS`

---

## Common Pitfalls

| Symptom | Cause |
|---------|-------|
| `DisplaySize = <1, 1>` | cimgui.dll version mismatch (pre/post 1.90 layout) |
| Font cache crash on frame 2 | `io.Fonts.TexID` was zero after init (usually fixed by matching cimgui) |
| Crash in `SetupRenderStateNative` | Do not call `SetStreamSource(null, 0)` — invalid for UP drawing |
| Duplicate `vtxCount` compile error | Was present in early source — remove the second declaration |
| Hook not firing | acclient.exe must be running and past login before injection |
| `ChangePortalMode` event (future) | Crashes Decal — never use (RynthCore note carried from NexTank) |

---

## Current Status (v0.2)

- [x] D3D9 EndScene hook stable
- [x] ImGui context, font texture, WndProc subclass working
- [x] ImGui demo window + RynthCore info panel rendering
- [x] Mouse/keyboard input forwarded; ImGui captures input when hovered
- [ ] Multi-viewport (needs IMGUI_ENABLE_VIEWPORTS cimgui build)
- [ ] NexTank UI panels ported to RynthCore
- [ ] Network message hooks
- [ ] World state / actions API
