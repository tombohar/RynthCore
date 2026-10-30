# Build & Deploy

RynthCore is the injection framework. RynthSuite (separate repo) contains the plugins that run on top of it.

## Prerequisites

- Windows 10 or 11
- **.NET 10 SDK (x86)**
- Visual Studio 2022 Build Tools with the **.NET desktop** and **C++ desktop** workloads (required by the NativeAOT ILC linker)
- Asheron's Call client installed

## The unloadable engine (the default since 2026-09-29)

The engine now runs on CoreCLR, and a hot reload really frees the old engine and its plugins
(`docs/UNLOADABLE_ENGINE_PLAN.md`, Status section). What ships and deploys:

```
C:\Games\RynthCore\Runtime\
├── RynthCore.Loader.dll      ← native loader (native\Loader, C/cl.exe): injection target, hosts CoreCLR once
├── RynthCore.Shim.dll        ← the only RynthCore code in CoreCLR's default context
├── RynthCore.Engine.dll(+pdb)← the engine, a managed assembly (-p:EngineHost=coreclr, ReadyToRun)
├── Avalonia.*, ImGui.NET ... ← the engine's own dependencies (load and unload with each engine)
├── cimgui, minhook, Skia ... ← native libraries (loaded once)
└── dotnet\                   ← the app-local x86 .NET runtime (from the NuGet runtime pack)
C:\Games\RynthSuite\<Plugin>\ ← each plugin as a managed assembly + its dependency DLLs
```

| Task | Command |
|---|---|
| Full local deploy (engine, loader, managed plugins, launcher) | `scripts\Deploy-RynthCore.ps1` (close every client first; `-SkipLauncher` while the launcher runs) |
| Engine change, clients running | `scripts\Publish-EngineCoreClr.ps1 -EngineOnly` — each running client hot-reloads onto it by itself |
| Loader/Shim change, clients running | `scripts\Publish-EngineCoreClr.ps1` — the in-use copies are renamed aside; new launches get the new ones |
| Installer | `installer\Build-Installer.ps1` (release plugins stay NativeAOT: the new engine loads them) |
| Hot-reload test on a local server | `scripts\Test-ClrWorld.ps1 -Reloads 3` (see its header) |
| Every offline test (RynthCore, RynthSuite, RynthServers) | `scripts\Run-AllTests.ps1` (`-Only <folder>` for one); `installer\Publish-Update.ps1` runs it and won't publish if one fails |
| RynthAi in game (buffing, a kill, looting, missile crafting) | `scripts\Run-GameTest.ps1` (logs a test character in, runs RynthSuite's `GameTests\RynthSelfTest.af`, prints PASS / FAIL / SKIP; see its header) |
| Why didn't a generation unload? | `tools\AlcRoots <pid>` (build it: `dotnet build tools\AlcRoots -c Release`) |

`-NativeAot` on `Deploy-RynthCore.ps1` and `Build-Installer.ps1` builds the old NativeAOT engine,
loader and plugins; the sections below describe that path. The engine and plugins use the same
source either way. Rules that keep an engine or plugin unloadable are in `CLAUDE.md` ("Unloadable engine").

## Projects

| Project | Type | Output |
|---------|------|--------|
| `src/RynthCore.Loader` | NativeAOT x86 DLL | Injected into acclient.exe — owns `RynthCoreInit` export and hot-loads the Engine. |
| `src/RynthCore.Engine` | NativeAOT x86 DLL | The runtime engine — pattern-scans `acclient.exe` hooks, hosts D3D9/ImGui overlay, runs plugins. |
| `src/RynthCore.Injector` | x86 console | Standalone CLI injector via `LoadLibrary` + `CreateRemoteThread`. The launcher embeds the same `EngineInjectionService` directly. |
| `src/RynthCore.App.Avalonia` | Avalonia desktop app | The launcher — server/account profiles, plugin management, suspended-launch + early-inject. |
| `src/RynthCore.PluginSdk` | Class library | Public host API surface for plugins. |
| `src/RynthCore.PluginCore` | Class library | `RynthPluginBase` + lifecycle dispatch helpers. |
| `src/RynthCore.App` | Shared source | Service classes linked into both Engine and Avalonia launcher (not a standalone build target). |

All projects target **`net10.0-windows`**. NativeAOT projects (Loader, Engine, plugins) are `x86` only with `RuntimeIdentifier=win-x86` and `PublishAot=true`.

## Publish Commands

### Whole solution

```powershell
dotnet build .\RynthCore.sln -c Release
```

This compiles every project including the Loader (which is now in the .sln). Note: `dotnet build` is enough for the launcher and SDK projects, but **NativeAOT projects require `dotnet publish`** to actually produce the native DLL with exports — see below.

### Loader (NativeAOT x86 — small, fast)

```powershell
dotnet publish .\src\RynthCore.Loader\RynthCore.Loader.csproj -c Release
```

Output: `src\RynthCore.Loader\bin\Release\net10.0-windows\win-x86\publish\RynthCore.Loader.dll`

### Engine (NativeAOT x86 — slow first build, ~2 min)

Requires `vswhere.exe` on PATH. If the build fails with `'vswhere.exe' is not recognized`, add the VS Installer directory:

```powershell
$env:PATH += ";C:\Program Files (x86)\Microsoft Visual Studio\Installer"
```

```powershell
dotnet publish .\src\RynthCore.Engine\RynthCore.Engine.csproj -c Release
```

Output: `src\RynthCore.Engine\bin\Release\net10.0-windows\win-x86\publish\RynthCore.Engine.dll` (~26 MB)

### Launcher (Avalonia)

```powershell
dotnet build .\src\RynthCore.App.Avalonia\RynthCore.App.Avalonia.csproj -c Release
```

Output: `src\RynthCore.App.Avalonia\bin\Release\net10.0-windows\RynthCore.App.Avalonia.dll`

### RynthAi Plugin (NativeAOT — separate repo)

Always clean first — incremental NativeAOT builds can silently skip recompilation and produce a stale DLL.

```powershell
cd C:\Projects\RynthSuite\Plugins\RynthCore.Plugin.RynthAi
Remove-Item -Recurse -Force obj\Release, bin\Release -ErrorAction SilentlyContinue
dotnet publish -c Release
```

Output: `bin\Release\net10.0-windows\win-x86\publish\RynthCore.Plugin.RynthAi.dll` (~7 MB)

The plugin's MSBuild target also copies the published DLL into the engine's `bin\...\Plugins\` folder so a freshly built engine picks it up automatically.

## Deploy

### Directory Layout

```
C:\Games\RynthCore\
├── RynthCore.exe                    ← Launcher (renamed from RynthCore.App.Avalonia.exe)
├── RynthCore.App.Avalonia.dll       ← Launcher assembly
├── RynthCore.App.Avalonia.deps.json
├── RynthCore.App.Avalonia.runtimeconfig.json
├── Avalonia.*.dll                   ← Avalonia framework DLLs
└── Runtime\
    ├── RynthCore.Loader.dll         ← Loader (NativeAOT x86) — injection target
    ├── RynthCore.Engine.dll         ← Engine (NativeAOT x86)
    ├── minhook.x86.dll              ← MinHook (x86), preloaded by EntryPoint
    ├── cimgui.dll                   ← ImGui C bindings (x86, docking branch; our build)
    └── Plugins\
        └── *.dll                    ← Built-in plugin directory

C:\Games\RynthSuite\RynthAi\
├── RynthCore.Plugin.RynthAi.dll     ← RynthAi plugin (recommended deploy location)
├── NavProfiles\                     ← Navigation profiles
├── LootProfiles\                    ← Loot profiles
└── Metas\                           ← Meta files
```

The injector's saved-path resolver prefers `Runtime\RynthCore.Loader.dll`. It falls back to `Runtime\RynthCore.Engine.dll` for legacy direct-load setups, and walks parent directories and sibling layouts (`Runtime\Engine\`, `Runtime\Native\`, etc.) so manual layouts continue to work.

### Deploy Launcher

The launcher can stay open while you replace its DLL.

```powershell
copy src\RynthCore.App.Avalonia\bin\Release\net10.0-windows\RynthCore.App.Avalonia.dll C:\Games\RynthCore\
copy src\RynthCore.App.Avalonia\bin\Release\net10.0-windows\RynthCore.App.Avalonia.deps.json C:\Games\RynthCore\
```

Restart the launcher after deploying.

### Deploy Loader + Engine

**AC must be closed** — the running client holds a file lock on the Loader and Engine DLLs.

```powershell
copy src\RynthCore.Loader\bin\Release\net10.0-windows\win-x86\publish\RynthCore.Loader.dll C:\Games\RynthCore\Runtime\
copy src\RynthCore.Engine\bin\Release\net10.0-windows\win-x86\publish\RynthCore.Engine.dll C:\Games\RynthCore\Runtime\
```

If you only changed Engine source, you can deploy just `RynthCore.Engine.dll` and rely on the Loader's hot-reload to swap it in (click **RL** on the overlay bar with AC running).

### Deploy RynthAi Plugin

**AC can stay open** — plugins are shadow-copied at load time, so the file is not locked.

**Plugin discovery is opt-in:** the engine no longer auto-scans `Runtime\Plugins\` for DLLs. Plugins load only from paths the user has added via the launcher's **Plugins** tab → "Add Plugin DLL". Those paths are persisted to `%AppData%\RynthCore\engine.json` as `PluginPaths` and read by the engine on each (re)init.

Deploy the plugin DLL to its canonical home and add the full path in the launcher:

```
C:\Games\RynthSuite\RynthAi\
├── RynthCore.Plugin.RynthAi.dll  ← add this path in launcher's Plugins tab
├── NavProfiles\
├── LootProfiles\
└── Metas\
```

```powershell
copy C:\Projects\RynthSuite\Plugins\RynthCore.Plugin.RynthAi\bin\Release\net10.0-windows\win-x86\publish\RynthCore.Plugin.RynthAi.dll C:\Games\RynthSuite\RynthAi\
```

After deploying the DLL:

1. Open the RynthCore launcher
2. **Plugins** tab → **Add Plugin DLL** → select `C:\Games\RynthSuite\RynthAi\RynthCore.Plugin.RynthAi.dll`
3. Launch AC (cold start) — the engine picks up the new path on init.

If AC is already running, click **RL** on the RynthCore overlay bar to hot-reload the engine; it re-reads `engine.json` and loads the newly-added plugin path.

## Verify

| File | Expected Size | If Wrong |
|------|--------------|----------|
| `Runtime\RynthCore.Loader.dll` | small (~1–2 MB) | NativeAOT didn't run — check for `Generating native code` in build output |
| `Runtime\RynthCore.Engine.dll` | ~26 MB | NativeAOT didn't run — same diagnosis |
| `RynthSuite\RynthAi\RynthCore.Plugin.RynthAi.dll` | ~7 MB | Built `dotnet build` instead of `dotnet publish` |
| `Runtime\cimgui.dll` | ~1.2 MB | Must be our build (`scripts\Build-Cimgui.ps1`); with ImGui on, the log shows `ImGuiSelfTest: OK` |

To confirm NativeAOT actually ran, check that `.lib` and `.exp` files exist alongside the DLL in the publish output's `native\` directory.

## Installer

The `installer/` directory contains an Inno Setup script and a PowerShell build script that publishes all required projects, stages the output, and produces a single `RynthCore-Setup.exe`.

### Prerequisites

- [Inno Setup 6](https://jrsoftware.org/isdl.php) installed to the default location (`C:\Program Files (x86)\Inno Setup 6\`)
- All build prerequisites listed above (.NET 10 SDK, VS Build Tools)

### Build the installer

```powershell
cd C:\Projects\RynthCore\installer
.\Build-Installer.ps1
```

This runs `dotnet publish` for the Launcher, Loader, Engine, and RynthAi plugin (from RynthSuite), stages everything under `installer\staging\app\`, then invokes `ISCC.exe` to produce the installer.

Output: `installer\Output\RynthCore-Setup.exe`

### Options

| Parameter | Default | Description |
|-----------|---------|-------------|
| `-Configuration` | `Release` | Build configuration |
| `-IsccPath` | `C:\Program Files (x86)\Inno Setup 6\ISCC.exe` | Path to the Inno Setup compiler |
| `-SkipBuild` | off | Skip `dotnet publish` steps and re-package using the existing staging directory |

### What the installer does

- Installs the launcher, loader, engine, and native dependencies to `C:\Games\RynthCore` (user-selectable)
- Creates data directories under `C:\Games\RynthSuite\RynthAi\` (NavProfiles, LootProfiles, MetaFiles, etc.) — these are preserved on uninstall
- Adds Start Menu and optional Desktop shortcuts
- Warns if `acclient.exe` is running (the engine + loader DLLs would be locked)
- Shows getting-started instructions on the finish page

## Multi-Client Coexistence (with ThwargLauncher etc.)

If you also run AC through ThwargLauncher, aclauncher, or any other launcher that may not patch AC's `OpenDataFile` for shared access, the two stacks will fight over the same DAT files. Once one launcher's clients have the DATs locked exclusively, the other can't open them — symptom is **"cannot access the data files"** when launching from the second launcher.

**Required setup**: a second AC client install for RynthCore launches.

```
C:\Turbine\Asheron's Call\           ← left untouched, used by ThwargLauncher etc.
C:\Games\RynthCore\AcClient\         ← full copy of the AC client (~1.4 GB), used by RynthCore
```

Setup:

```powershell
robocopy "C:\Turbine\Asheron's Call" "C:\Games\RynthCore\AcClient" /E /MT:8 /NFL /NDL /NJH /NJS /NP `
    /XF "acclient_*.log" "acclient.log"
```

Then in the RynthCore launcher:

1. Set **AC Client Path** to `C:\Games\RynthCore\AcClient\acclient.exe`
2. Click somewhere else / save so the change persists

The two installs share `Documents\Asheron's Call\UserPreferences.ini` (it's user-scoped), so `ComputeUniquePort=True` applies to all instances regardless of which install they came from.

If your AC client ever gets a real-world patch, apply it to both copies (or `robocopy /MIR` to re-sync from the canonical install).

## Gotchas

- **`dotnet publish`, not `dotnet build` for Loader, Engine, and plugins.** NativeAOT only runs during `dotnet publish`. A `dotnet build` produces a valid managed DLL that compiles fine but has no unmanaged exports and will be silently ignored. The Avalonia launcher is the exception — `dotnet build` is fine since it's a normal .NET app.
- **Clean before plugin publish.** Incremental NativeAOT builds can silently reuse stale output. Delete `obj\Release` and `bin\Release` before every plugin publish to guarantee a fresh compile.
- **Engine deploy path is `Runtime\`**, not `C:\Games\RynthCore\` directly. The injector resolves `Runtime\RynthCore.Loader.dll` (preferred) and `Runtime\RynthCore.Engine.dll` (legacy) by default.
- **Close AC before redeploying Loader or Engine.** Both are loaded into `acclient.exe` and locked while the game is running. Plugins are shadow-copied so they can be hot-swapped without closing AC.
- **cimgui.dll is built by us, not taken from NuGet.** `scripts\Build-Cimgui.ps1` fetches cimgui 1.91.6dock into `native\cimgui\_src` (git-ignored), builds it with MSVC (VS 2022 Build Tools, C++ workload) and writes `src\RynthCore.Engine\Native\cimgui.dll`, which is committed. The stock NuGet x86 DLL does not match ImGui.NET's structs (64-bit `ImTextureID`); the engine's `ImGuiSelfTest` refuses it and any other non-matching build. Only rerun the script when changing the cimgui build itself.
- **vswhere.exe must be on PATH** for the NativeAOT link step. Add `C:\Program Files (x86)\Microsoft Visual Studio\Installer` to PATH if missing.
- **Decal.** `src\RynthCore.DecalBridge` (net48) builds with the solution when Decal is installed (an empty assembly and a warning otherwise); every installer stages it (`Build-Installer.ps1` fails without Decal; `-SkipDecalBridge` for a dev build). The launcher registers it only for accounts set to "Decal + RynthCore (experimental)"; their choice is kept in `%APPDATA%\RynthCore\decal-accounts.json`, never in the shared `appsettings.json`. Bridge mode and in-game ImGui under Decal: `docs/DECAL_BRIDGE_PLAN.md`.
- **Decal coexistence mode.** If `decal.dll`, `UBLoader.dll`, `Decal.Adapter.dll`, or `phatacd.dll` is loaded into acclient.exe and neither bridge mode nor `DecalInGameImGui` applies, the engine skips the entire D3D9/ImGui path and runs plugins on a 30 Hz worker tick with Avalonia floating panels via `LayeredWindow` (GDI). This is intentional — two D3D9 hookers can't symmetrically save/restore render state. Check `RynthCore.log` for `D3D9: Decal coexistence` to confirm.
