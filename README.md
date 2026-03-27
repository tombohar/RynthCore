# NexCore

A modern .NET 9 framework for Asheron's Call client modding. Replaces Decal + UBService with a clean, self-contained injection system.

## Current Status (v0.2)

NexCore hooks D3D9's EndScene and renders a full ImGui overlay with docking support. Mouse and keyboard input are forwarded to ImGui, and when ImGui is focused, it eats the input so the game doesn't react.

## Architecture

```
NexCore.Injector.exe          (desktop console app)
  └─ LoadLibrary + CreateRemoteThread into acclient.exe

acclient.exe
  └─ NexCore.Engine.dll       (NativeAOT x86 native DLL)
      ├─ EntryPoint.cs         NexCoreInit export, preloads native DLLs
      ├─ D3D9/
      │   ├─ D3D9VTable.cs     temp device → vtable discovery
      │   ├─ EndSceneHook.cs   MinHook detour on EndScene
      │   └─ TestRenderer.cs   (v0.1 proof of life, kept for reference)
      └─ ImGui/
          ├─ ImGuiController.cs   context + frame orchestration
          ├─ DX9Backend.cs        renders ImGui draw data via D3D9
          └─ Win32Backend.cs      WndProc subclass for mouse/keyboard
```

## Prerequisites

- **.NET 9 SDK**
- **Visual Studio 2022** (17.12+) with:
  - .NET desktop development workload
  - Desktop development with C++ workload (NativeAOT linker)
- **minhook.x86.dll** — [MinHook releases](https://github.com/TsudaKageworthy/minhook/releases)
- **cimgui.dll** (x86) — see below

## Getting cimgui.dll (x86)

ImGui.NET wraps cimgui.dll. You need an x86 (32-bit) build. Options:

### Option A: Extract from ImGui.NET NuGet package
1. After building, find the NuGet cache:
   `%USERPROFILE%\.nuget\packages\imgui.net\1.91.6.1\`
2. Look in `runtimes\win-x86\native\` for cimgui.dll
3. If only x64 is available, use Option B

### Option B: Build from source
```bash
git clone --recursive -b docking https://github.com/cimgui/cimgui
cd cimgui
mkdir build && cd build
cmake .. -A Win32
cmake --build . --config Release
```
Output: `build\Release\cimgui.dll`

### Option C: ImGui.NET-nativebuild releases
Check [ImGui.NET-nativebuild releases](https://github.com/ImGuiNET/ImGui.NET-nativebuild/releases) for prebuilt x86 Windows binaries.

## Build & Deploy

```bash
# From solution root
cd src\NexCore.Engine
dotnet publish -c Release

cd ..\NexCore.Injector
dotnet publish -c Release
```

Create deploy folder:
```
C:\Games\NexCore\
├── NexCore.Injector.exe      ← from Injector publish
├── NexCore.Engine.dll        ← from Engine publish (big file, 5-15MB)
├── minhook.x86.dll           ← from MinHook releases
└── cimgui.dll                ← x86 build (see above)
```

## Run

1. Launch AC and log in
2. Run `NexCore.Injector.exe`
3. You should see the ImGui demo window and a "NexCore v0.2" info panel
4. Click/drag ImGui windows — input is captured by ImGui when hovering over it
5. Check `Desktop\NexCore.log` for diagnostics

## What's Next

| Phase | What |
|-------|------|
| v0.3 | Multi-viewport (undock ImGui windows outside AC) — requires cimgui with IMGUI_ENABLE_VIEWPORTS |
| v0.4 | Port NexAi UI panels from UBService to NexCore |
| v0.5 | Network message hooks (replace Decal's event system) |
| v0.6 | World state tracking + actions API |
