# RynthCore

RynthCore is a modern .NET 10 modding host for Asheron's Call. It combines an x86 NativeAOT in-process engine, a hot-reload-capable loader, an injector, an Avalonia desktop launcher, and a plugin surface that replaces the legacy Decal + UBService + VTank stack with a self-contained RynthCore-native API.

The plugin code (RynthAi etc.) lives in a sibling repository: [RynthSuite](https://github.com/tombohar/RynthSuite).

## What it does

- Launches `acclient.exe` (suspended if needed) and injects the RynthCore loader
- Hosts in-game UI through an embedded ImGui overlay, with a parallel Avalonia overlay stack for floating panels (and for Decal coexistence mode where ImGui is disabled)
- Exposes a growing plugin host API with hooks for chat, combat, targeting, movement, object lifecycle, vendor/trade, vitals, enchantments, and UI events
- Hot-reloads plugin DLLs without restarting AC; hot-reloads the engine itself via the loader DLL
- Detects Decal/UBService/VTank coexistence and degrades gracefully (skips D3D9 path, drives plugin tick from a worker thread)

## Solution layout

```
RynthCore.sln
├── src/RynthCore.Loader            x86 NativeAOT DLL — injection target.
│                                    Owns RynthCoreInit; loads + reloads
│                                    the Engine.
├── src/RynthCore.Engine             x86 NativeAOT DLL — the runtime
│                                    injected into acclient.exe. Pattern-
│                                    scanned hooks, D3D9 + ImGui + Avalonia
│                                    overlays, plugin manager, crash logger.
├── src/RynthCore.Injector           x86 console — Win32 LoadLibrary +
│                                    CreateRemoteThread injector with a
│                                    suspended-launch / early-inject path.
├── src/RynthCore.App.Avalonia       Desktop launcher (Avalonia 11.2.3,
│                                    Fluent theme). Profile management,
│                                    server status, launches and injects.
├── src/RynthCore.App                Shared service classes linked into
│                                    both Engine and Avalonia launcher
│                                    via <Compile Include /> — not built
│                                    standalone.
├── src/RynthCore.PluginSdk          Public host API surface for plugins.
└── src/RynthCore.PluginCore         RynthPluginBase + lifecycle helpers.
```

## Prerequisites

- Windows 10 or 11
- **.NET 10 SDK (x86)**
- Visual Studio 2022 Build Tools with the **.NET desktop** and **C++ desktop** workloads (the C++ tools are required by the NativeAOT ILC linker)
- Asheron's Call client installed (default: `C:\Turbine\Asheron's Call\`)

The Engine, Loader, and plugin projects target `net10.0-windows` with `RuntimeIdentifier=win-x86` and `PublishAot=true`. A bundled `cimgui.dll` ships under `src/RynthCore.Engine/Native/`.

## Build

From the repository root:

```powershell
$env:DOTNET_CLI_HOME = "$PWD\.dotnet-home"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
dotnet build .\RynthCore.sln -c Release
```

To publish the runtime pieces individually (use **`publish`**, not `build`, for any NativeAOT project):

```powershell
dotnet publish .\src\RynthCore.Loader\RynthCore.Loader.csproj -c Release
dotnet publish .\src\RynthCore.Engine\RynthCore.Engine.csproj -c Release
dotnet publish .\src\RynthCore.App.Avalonia\RynthCore.App.Avalonia.csproj -c Release
```

For a clean local deployment, keep the launcher at the top level of `C:\Games\RynthCore\` and place the injectable engine and loader payloads under `C:\Games\RynthCore\Runtime\`. The injector's saved-path resolver prefers `Runtime\RynthCore.Loader.dll`, falls back to `Runtime\RynthCore.Engine.dll` for legacy direct-load setups, and walks up sibling directories so manual layouts still work.

You can produce that layout with:

```powershell
.\scripts\Deploy-RynthCore.ps1
```

For full deploy details, the Inno Setup installer, and gotchas, see [`BUILD.md`](BUILD.md).

## Documentation

- `BUILD.md` — full build, deploy, and installer instructions
- `CLAUDE.md` — project memory file (architecture orientation, technical facts, common pitfalls)
- `docs/ACCLIENT_HOOK_INVENTORY.md` — hook target inventory against the user's live `acclient.exe`
- `docs/PLUGIN_HOOK_MATRIX.md` — clean-room hook surface matrix
- `docs/LEGAL_COMPATIBILITY.md` — project policy on Decal/VTank compatibility
- `docs/archive/` — archived plans and one-off utilities

## Repository notes

- Local settings, machine-specific files, generated `bin/`, `obj/`, `.vs/`, `.dotnet-home/` content, and launcher data (profiles, runtime state) are kept out of source control via `.gitignore`
- Plugin code lives in the [RynthSuite](https://github.com/tombohar/RynthSuite) repo as a sibling clone

## Security and secrets

This repository should not contain API keys, local secrets, or machine-specific credentials. If you add any local integrations later, keep them in ignored files such as `.env`, `appsettings.Local.json`, or other untracked local config.

## License

This project is released under the MIT License. See `LICENSE`.
