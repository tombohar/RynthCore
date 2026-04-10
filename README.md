# RynthCore

RynthCore is a modern .NET 9 modding host for Asheron's Call. It combines an x86 NativeAOT in-process engine, an injector, a desktop launcher, and a plugin surface.

The project is currently focused on three things:

- launching and injecting the RynthCore engine into `acclient.exe`
- hosting in-game UI through an embedded overlay stack
- building a RynthCore-native plugin API with hooks for chat, combat, targeting, movement, object, and UI events

## Solution layout

The repository currently ships these main projects:

- `src/RynthCore.Engine` - x86 NativeAOT runtime injected into the game client
- `src/RynthCore.Injector` - process launch and DLL injection service
- `src/RynthCore.App` - Windows desktop launcher
- `src/RynthCore.App.Avalonia` - Avalonia launcher preview and profile manager
- `src/RynthCore.PluginSdk` - public host API surface for plugins
- `src/RynthCore.PluginCore` - runtime helpers for plugin bootstrap/lifecycle
- `Plugins/RynthCore.Plugin.HelloBox` - sample plugin used for end-to-end testing

## Current capabilities

- injects `RynthCore.Engine.dll` into the x86 Asheron's Call client
- hosts overlay panels inside the game process
- includes launcher/profile flows for servers, accounts, and saved launch targets
- exposes a growing plugin host API for chat, target, combat, movement, object, and UI-related hooks
- includes legal guardrails and hook planning docs

## Prerequisites

- Windows
- .NET 9 SDK
- Visual Studio 2022 with the .NET desktop and C++ desktop workloads

The engine project targets `win-x86` and uses NativeAOT. A bundled `cimgui.dll` is included under `src/RynthCore.Engine/Native/`.

## Build

From the repository root:

```powershell
$env:DOTNET_CLI_HOME = "$PWD\\.dotnet-home"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
dotnet build .\RynthCore.sln -c Release
```

To publish the main runtime pieces individually:

```powershell
dotnet publish .\src\RynthCore.Engine\RynthCore.Engine.csproj -c Release
dotnet publish .\src\RynthCore.App\RynthCore.App.csproj -c Release -r win-x86
dotnet publish .\src\RynthCore.App.Avalonia\RynthCore.App.Avalonia.csproj -c Release
```

For a clean local deployment, keep the launcher at the top level of `C:\Games\RynthCore` and place the injectable engine payload under `C:\Games\RynthCore\Runtime\`. The injector now resolves `Runtime\RynthCore.Engine.dll` by default, which keeps the game folder tidier while still allowing manual overrides.

You can produce that layout with:

```powershell
.\scripts\Deploy-RynthCore.ps1
```

## Repository notes

- local settings and machine-specific files are ignored through `.gitignore`
- generated `bin/`, `obj/`, `.vs/`, and `.dotnet-home/` content should not be committed
- launcher data such as profiles and runtime state is expected to live outside the repository
- compatibility planning and legal guardrails live in `docs/`

See:

- `docs/ACCLIENT_HOOK_INVENTORY.md`
- `docs/PLUGIN_HOOK_MATRIX.md`
- `docs/LEGAL_COMPATIBILITY.md`

## Security and secrets

This repository should not contain API keys, local secrets, or machine-specific credentials. If you add any local integrations later, keep them in ignored files such as `.env`, `appsettings.Local.json`, or other untracked local config.

## License

This project is released under the MIT License. See `LICENSE`.
