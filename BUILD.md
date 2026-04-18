# Build & Deploy

RynthCore is the injection framework. RynthSuite (separate repo) contains the plugins that run on top of it.

## Prerequisites

- Windows 10/11
- .NET 9 SDK (x86)
- Visual Studio 2022 Build Tools with the .NET desktop and C++ desktop workloads (required by the NativeAOT ILC linker)
- Asheron's Call client installed

## Projects

| Project | Type | Output |
|---------|------|--------|
| `src/RynthCore.Engine` | NativeAOT x86 DLL | Injected into acclient.exe — hooks D3D9 EndScene, hosts ImGui overlay |
| `src/RynthCore.Injector` | Console app | Injects the engine DLL via LoadLibrary + CreateRemoteThread |
| `src/RynthCore.App.Avalonia` | Avalonia desktop app | Desktop launcher — server/account profiles, plugin management, launches + injects |
| `src/RynthCore.PluginSdk` | Class library | Public host API surface for plugins |
| `src/RynthCore.PluginCore` | Class library | Runtime helpers for plugin bootstrap/lifecycle |
| `src/RynthCore.App` | Shared source | Model and service files linked by both the Avalonia app and the Engine (not a standalone project) |

## Publish Commands

### Launcher (Avalonia)

```bash
cd C:\Projects\RynthCore\src\RynthCore.App.Avalonia
dotnet build -c Release
```

Output: `bin\Release\net9.0-windows7.0\RynthCore.App.Avalonia.dll`

### Engine (NativeAOT — slow first build, ~2 min)

Requires `vswhere.exe` on PATH. If the build fails with `'vswhere.exe' is not recognized`, add the VS Installer directory:

```bash
set PATH=%PATH%;C:\Program Files (x86)\Microsoft Visual Studio\Installer
```

```bash
cd C:\Projects\RynthCore\src\RynthCore.Engine
dotnet publish -c Release
```

Output: `bin\Release\net9.0-windows\win-x86\publish\RynthCore.Engine.dll` (~26 MB)

### RynthAi Plugin (NativeAOT — separate repo)

Always clean first — incremental builds can silently skip NativeAOT and produce a stale DLL.

```bash
cd C:\Projects\RynthSuite\Plugins\RynthCore.Plugin.RynthAi
rmdir /s /q obj\Release bin\Release 2>nul
dotnet publish -c Release
```

Output: `bin\Release\net9.0-windows\win-x86\publish\RynthCore.Plugin.RynthAi.dll` (~7 MB)

## Deploy

### Directory Layout

```
C:\Games\RynthCore\
├── RynthCore.exe                 ← Launcher (renamed from RynthCore.App.Avalonia.exe)
├── RynthCore.App.Avalonia.dll    ← Launcher assembly
├── RynthCore.App.Avalonia.deps.json
├── RynthCore.App.Avalonia.runtimeconfig.json
├── Avalonia.*.dll                ← Avalonia framework DLLs
└── Runtime\
    ├── RynthCore.Engine.dll      ← Engine (NativeAOT x86)
    ├── minhook.x86.dll           ← MinHook (x86), preloaded by EntryPoint
    ├── cimgui.dll                ← ImGui C bindings (x86, docking branch)
    └── Plugins\
        └── *.dll                 ← Plugin DLLs (built-in directory)

C:\Games\RynthSuite\RynthAi\
├── RynthCore.Plugin.RynthAi.dll  ← RynthAi plugin (recommended deploy location)
├── NavProfiles\                  ← Navigation profiles
├── LootProfiles\                 ← Loot profiles
└── Metas\                        ← Meta files
```

### Deploy Launcher

The launcher can stay open while you replace its DLL.

```bash
copy src\RynthCore.App.Avalonia\bin\Release\net9.0-windows7.0\RynthCore.App.Avalonia.dll C:\Games\RynthCore\
copy src\RynthCore.App.Avalonia\bin\Release\net9.0-windows7.0\RynthCore.App.Avalonia.deps.json C:\Games\RynthCore\
```

Restart the launcher after deploying.

### Deploy Engine

**AC must be closed** — the running client holds a file lock on the engine DLL.

```bash
copy src\RynthCore.Engine\bin\Release\net9.0-windows\win-x86\publish\RynthCore.Engine.dll C:\Games\RynthCore\Runtime\
```

### Deploy RynthAi Plugin

**AC can stay open** — plugins are shadow-copied at load time, so the file is not locked.

The recommended deploy location is `C:\Games\RynthSuite\RynthAi\` — alongside the plugin's data files. Add this path in the launcher's **Plugins** tab. Paths are persisted to `%AppData%\RynthCore\engine.json`.

```bash
copy C:\Projects\RynthSuite\Plugins\RynthCore.Plugin.RynthAi\bin\Release\net9.0-windows\win-x86\publish\RynthCore.Plugin.RynthAi.dll C:\Games\RynthSuite\RynthAi\
```

Alternatively, deploy to the engine's built-in plugin directory:

```bash
copy ... C:\Games\RynthCore\Runtime\Plugins\
```

After deploying a plugin with AC running, click **RL** on the RynthCore overlay bar to hot-reload.

## Verify

| File | Expected Size | If Wrong |
|------|--------------|----------|
| `Runtime\RynthCore.Engine.dll` | ~26 MB | NativeAOT didn't run — check for `Generating native code` in build output |
| `RynthSuite\RynthAi\RynthCore.Plugin.RynthAi.dll` | ~7 MB | If ~1 MB, you built the stale stub at `C:\Projects\RynthCore\Plugins\` instead of `C:\Projects\RynthSuite\Plugins\` |
| `Runtime\cimgui.dll` | ~1.5 MB | Must match ImGui.NET 1.91.6.1 |

To confirm NativeAOT actually ran, check that `.lib` and `.exp` files exist alongside the DLL in the `native\` directory.

## Installer

The `installer/` directory contains an Inno Setup script and a PowerShell build script that publishes all three projects, stages the output, and produces a single `RynthCore-Setup.exe`.

### Prerequisites

- [Inno Setup 6](https://jrsoftware.org/isdl.php) installed to the default location (`C:\Program Files (x86)\Inno Setup 6\`)
- All build prerequisites listed above (.NET 9 SDK, VS Build Tools)

### Build the installer

```powershell
cd C:\Projects\RynthCore\installer
.\Build-Installer.ps1
```

This runs three `dotnet publish` steps (Launcher, Engine, Plugin), stages everything under `installer\staging\app\`, then invokes `ISCC.exe` to produce the installer.

Output: `installer\Output\RynthCore-Setup.exe`

### Options

| Parameter | Default | Description |
|-----------|---------|-------------|
| `-Configuration` | `Release` | Build configuration |
| `-IsccPath` | `C:\Program Files (x86)\Inno Setup 6\ISCC.exe` | Path to the Inno Setup compiler |
| `-SkipBuild` | off | Skip `dotnet publish` steps and re-package using the existing staging directory |

### What the installer does

- Installs the launcher, engine, and native dependencies to `C:\Games\RynthCore` (user-selectable)
- Creates data directories under `C:\Games\RynthSuite\RynthAi\` (NavProfiles, LootProfiles, MetaFiles, etc.) — these are preserved on uninstall
- Adds Start Menu and optional Desktop shortcuts
- Warns if `acclient.exe` is running (the engine DLL would be locked)
- Shows getting-started instructions on the finish page

## Gotchas

- **`dotnet publish`, not `dotnet build` for Engine and Plugins.** NativeAOT only runs during `dotnet publish`. A `dotnet build` produces a valid managed DLL that compiles fine but has no unmanaged exports and will be silently ignored by the engine. The Avalonia launcher is the exception — `dotnet build` is fine since it's a normal .NET app.
- **Clean before plugin publish.** Incremental NativeAOT builds can silently reuse stale output. Delete `obj\Release` and `bin\Release` before every publish to guarantee a fresh compile.
- **Engine deploy path is `Runtime\`**, not `C:\Games\RynthCore\`. The injector resolves `Runtime\RynthCore.Engine.dll` by default.
- **Two RynthAi projects exist.** The real plugin is at `C:\Projects\RynthSuite\Plugins\RynthCore.Plugin.RynthAi\`. There is a stale stub at `C:\Projects\RynthCore\Plugins\RynthCore.Plugin.RynthAi\` — do not build or deploy from there.
- **Engine publish copies a stale plugin** into the engine's publish output. Always deploy the plugin from the RynthSuite path last.
- **cimgui.dll version must match ImGui.NET NuGet.** Post-1.90 struct layouts changed. A mismatched cimgui.dll causes `DisplaySize = <1, 1>` or font crashes.
- **vswhere.exe must be on PATH** for NativeAOT link step. Add `C:\Program Files (x86)\Microsoft Visual Studio\Installer` to PATH if missing.
