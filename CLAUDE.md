# RynthCore — Claude Context

A modern .NET 10 NativeAOT framework for Asheron's Call client modding. Replaces Decal + UBService + VTank with a self-contained injection stack and a RynthCore-native plugin API. Hooks D3D9 EndScene to render an ImGui overlay inside `acclient.exe`, with a parallel Avalonia overlay stack for floating panels (and for Decal coexistence mode where the D3D9 path is disabled).

The plugin code (RynthAi etc.) lives in a sibling repo: **`C:\Projects\RynthSuite\`**.

---

## ⚠ Active Focus

**RynthAi Avalonia panel** at `src/RynthCore.Engine/UI/Panels/RynthAiPanel.cs`. Registered in `EntryPoint.cs:InitWorker` and bridged to the RynthSuite plugin DLL via C exports (`RynthPluginGetSnapshotJson`, `RynthPluginToggleMacro`, etc.).

The ImGui variant of RynthAi is **defunct** — not loaded at runtime. All in-AC UI is engine-side Avalonia panels. Do RynthAi UI work in the Avalonia panel file; the RynthSuite plugin DLL is the data/logic backend only.

---

## Architecture (three-tier injection)

```
acclient.exe
  └─ RynthCore.Loader.dll      ← injection target. Owns RynthCoreInit export.
       └─ RynthCore.Engine.dll  ← the actual engine (~26 MB NativeAOT x86 PE).
            └─ Plugins\*.dll    ← shadow-copied, hot-reloadable plugin DLLs.
```

The Loader exists so we can `FreeLibrary` the Engine and swap in a fresh build without restarting AC. The Injector's saved-path logic prefers `RynthCore.Loader.dll` and falls through to `RynthCore.Engine.dll` only for legacy direct-load setups.

---

## Project Layout

```
C:\Projects\RynthCore\
├── docs\
│   ├── ACCLIENT_HOOK_INVENTORY.md   Hook targets vs. live acclient.exe
│   ├── PLUGIN_HOOK_MATRIX.md        Clean-room hook matrix
│   ├── LEGAL_COMPATIBILITY.md       Project policy (no decompiling Decal/VTank)
│   └── archive\                     Archived plans + one-off utilities
├── installer\                       Inno Setup + PowerShell publish pipeline
├── scripts\Deploy-RynthCore.ps1     Local deploy automation
└── src\
    ├── RynthCore.Loader\            x86 NativeAOT DLL injected into AC.
    │                                 Owns RynthCoreInit; loads Engine; can
    │                                 unload + reload it for hot-swap.
    ├── RynthCore.Engine\            x86 NativeAOT DLL — the real engine.
    │   ├── EntryPoint.cs            Exports, init worker, native preload,
    │   │                             ImGui resolver, exception handlers.
    │   ├── EngineLifecycle.cs       Shutdown / hot-reload teardown.
    │   ├── CrashLogger.cs           VEH that catches AVs NativeAOT can't.
    │   ├── LogPaths.cs              Unified log path (synced w/ Injector).
    │   ├── Compatibility\           ~50 pattern-scanned acclient.exe hooks
    │   │                             (login, chat, smartbox, object,
    │   │                              combat, vendor, vitals, salvage,
    │   │                              radar, raw packets, …).
    │   │                             Includes DecalDetection for
    │   │                             coexistence mode.
    │   ├── D3D9\                    EndScene hook + Nav3D world-space
    │   │                             renderer + GameMatrixCapture for
    │   │                             world→screen.
    │   ├── ImGui\                   ImGui controller, DX9 backend, Win32
    │   │                             input, multi-viewport scaffolding.
    │   ├── Hooking\MinHook.cs       MinHook P/Invoke wrapper.
    │   ├── Plugins\                 PluginManager, PluginLoader (per-init
    │   │                             shadow-copy dirs for hot-reload),
    │   │                             PluginContract, EngineSettings.
    │   │                             Plugin discovery is opt-in: paths come
    │   │                             from %APPDATA%\RynthCore\engine.json
    │   │                             (managed by launcher's Plugins tab).
    │   │                             No auto-scan of any bundled folder.
    │   └── UI\                      Parallel Avalonia overlay stack:
    │                                 LayeredWindow (GDI), shared-texture
    │                                 D3D9 publisher, OverlayHost panels
    │                                 (Status, Log, RynthAi, Monsters,
    │                                  Items, Settings, Nav, Meta, Radar).
    ├── RynthCore.Injector\          x86 console — Win32 LoadLibrary +
    │                                 CreateRemoteThread injector. Has a
    │                                 "launch suspended + early inject"
    │                                 path for pre-startup hook installs.
    ├── RynthCore.App\                Shared service classes (NOT a
    │                                 standalone build target). Linked
    │                                 into both Engine and Avalonia
    │                                 launcher via <Compile Include />.
    │                                 Profile/account/launch services,
    │                                 INI editing for AC's
    │                                 UserPreferences, intro-video parking.
    ├── RynthCore.App.Avalonia\      Desktop launcher (Avalonia 11.2.3,
    │                                 Fluent theme). The real launcher.
    │                                 Server/account profiles, plugin
    │                                 management, suspended launch + inject.
    ├── RynthCore.PluginSdk\          Public host API surface for plugins.
    │                                 RynthCoreApiNative + RynthCoreHost
    │                                 wrap ~100 native function pointers.
    │                                 API version: see RynthCoreHost
    │                                 (currently >= 40, version-gated).
    └── RynthCore.PluginCore\         RynthPluginBase + lifecycle helpers
                                      (Initialize/Shutdown/OnTick/
                                       OnLoginComplete/OnChatBarEnter/
                                       OnDeleteObject/OnEnchantment*/etc.).
```

Deploy folder: `C:\Games\RynthCore\` (launcher + Avalonia DLLs at the root, engine + native libs + plugins under `Runtime\`).

---

## Build

All projects target **`net10.0-windows`**. Engine, Loader, and Plugins are **x86 NativeAOT** (`PublishAot=true`, `NativeLib=Shared`). The launcher and SDK projects are normal managed builds.

```powershell
# Whole solution (note: Loader is now in the .sln)
dotnet build .\RynthCore.sln -c Release

# Engine — slow first build (~2 min, NativeAOT ILC link step)
dotnet publish .\src\RynthCore.Engine\RynthCore.Engine.csproj -c Release

# Loader (small, fast)
dotnet publish .\src\RynthCore.Loader\RynthCore.Loader.csproj -c Release

# Launcher
dotnet publish .\src\RynthCore.App.Avalonia\RynthCore.App.Avalonia.csproj -c Release
```

Use **`dotnet publish`** for any NativeAOT project. `dotnet build` produces a managed-only DLL with no exports and the engine silently rejects it.

`vswhere.exe` must be on PATH for the NativeAOT link step:

```powershell
$env:PATH += ";C:\Program Files (x86)\Microsoft Visual Studio\Installer"
```

Full deploy layout, installer pipeline, and gotchas live in `BUILD.md`.

---

## Plugin Loading (opt-in)

Plugins are loaded **only** from paths the user explicitly adds via the launcher's **Plugins** tab → "Add Plugin DLL". The launcher persists those paths to `%APPDATA%\RynthCore\engine.json` as a `PluginPaths` array, which `EngineSettings` reads at engine init. There is **no auto-scan** of `Runtime\Plugins\` or any other bundled folder — historically that caused stray DLLs to load on next start (or after hot-reload) without the user knowing.

Implications:

- The installer no longer drops `RynthCore.Plugin.RynthAi.dll` into `C:\Games\RynthCore\Runtime\Plugins\`. It deploys the plugin to `C:\Games\RynthSuite\RynthAi\` and the user adds the path in the launcher.
- `RynthCore.Plugin.RynthAi.csproj` no longer has a `CopyPublishedPluginToEngineOutputs` MSBuild target; building the plugin only emits to its own `bin\...\publish\`. Use `Deploy-RynthCore.ps1` for staged deploys.
- After adding/removing a plugin path in the UI, the engine picks up the change on next AC client launch. To pick it up live in a running client, click the **RL** button on the RynthCore overlay bar to hot-reload the engine — `EngineSettings` re-reads `engine.json` on each engine init.

## Disabling the ImGui shell + plugins

`%APPDATA%\RynthCore\engine.json` has two boolean flags read by `EngineSettings`:

- `EnableImGuiShell` — when `false`, the in-AC ImGui surface draws nothing: both `RynthCoreShell.Render` (the overlay bar) **and** `PluginManager.RenderAll` (any plugin-drawn ImGui windows) are skipped. D3D9/ImGui still init so `Win32Backend.GameHwnd` is available for Avalonia owner-window binding, and plugins still load/init/tick — they just don't draw, so Avalonia panels can keep driving them through the plugin's C exports (`RynthPluginToggleMacro`, etc.).
- `EnablePlugins` — when `false`, `PluginManager.LoadPlugins` is skipped (no plugin loads/inits/ticks/renders). The Decal-coexistence plugin pump is also skipped. **Avalonia panels that wrap a plugin (RynthAi) become inert** — their `GetProcAddress` lookups resolve to null because the plugin DLL was never loaded.

Both default to `true` if the field is missing. Avalonia floating panels and all `Compatibility/` hooks (login, chat, vitals, radar, packets, …) run regardless of these flags.

**Common configurations:**

| `EnableImGuiShell` | `EnablePlugins` | Result |
|---|---|---|
| `true`  | `true`  | Default. Bar visible, plugins load + draw their own ImGui windows, Avalonia panels work. |
| `false` | `true`  | "Avalonia-only" mode. Bar + plugin ImGui hidden, plugins still tick, Avalonia RynthAi panel drives macros via plugin exports. ← *current dev mode* |
| `false` | `false` | Pure engine mode. No bar, no plugin logic. Avalonia panels that wrap a plugin (RynthAi) won't function. |
| `true`  | `false` | Bar visible but no plugins. Mostly useless. |

**To re-enable everything:** set both to `true` (or delete the lines — missing = `true`):

```json
{
  "PluginPaths": [ "..." ],
  "EnableImGuiShell": true,
  "EnablePlugins": true
}
```

Picked up on next AC launch. Hot-reload via the **RL** button doesn't help when the bar is hidden — relaunch the client.

## Logging

Unified log: **`C:\Games\RynthCore\Logs\RynthCore.log`**

Loader, Engine, Injector, plugins, and CrashLogger all append to this single file with origin tags (`[engine]`, `[loader]`, `[injector]`, `[plugin]`, `[compat]`). On Engine cold start the log rotates to `RynthCore.log.old` if it exceeds 5 MB. Path is centralised in `src/RynthCore.Engine/LogPaths.cs`; Loader and Injector mirror the constants.

**Crash logging:**
- `CrashLogger.cs` installs a Win32 Vectored Exception Handler. Catches AVs and other SEH faults that NativeAOT can't surface as managed exceptions. Dumps a `==== CRASH ====` banner with exception code, faulting module + RVA, register dump, EBP frame walk, and ESP stack sweep.
- `EntryPoint.InstallManagedExceptionHandlers` hooks `AppDomain.UnhandledException` and `TaskScheduler.UnobservedTaskException` so managed exceptions escaping a worker thread (or unobserved task continuations) get logged with full stack before the process terminates.

---

## Key Technical Facts

### NativeAOT / x86

- .NET 10, `net10.0-windows`, `PlatformTarget=x86`, `RuntimeIdentifier=win-x86`
- `AllowUnsafeBlocks=true`, `PublishAot=true`, `NativeLib=Shared`, `TrimMode=link`, `InvariantGlobalization=true`
- `ImGui.NET`, `Avalonia`, `Avalonia.Base`, `Avalonia.Desktop`, `Avalonia.Win32`, `Avalonia.Skia`, `Avalonia.Themes.Simple` are trim-rooted in the Engine csproj so the AOT trimmer doesn't strip them
- `SetWindowLongA` not `SetWindowLongPtrA` — the Ptr variant doesn't exist on x86

### Injection flow

1. `RynthCore.Injector.exe` (or the launcher's in-process injection service) calls `CreateRemoteThread(LoadLibraryA, "RynthCore.Loader.dll")` against acclient.exe
2. Loader's `RynthCoreInit` export fires under loader lock — immediately spawns a background thread that LoadLibrarys the Engine and calls `RynthCoreEngineInit`
3. Engine's init thread sleeps briefly to let AC finish D3D9 init, preloads `minhook.x86.dll` + `cimgui.dll`, runs the long list of `RunInitStep` calls in `EntryPoint.InitWorker`, then arms the D3D9 EndScene hook
4. Hot reload: the Loader can `FreeLibrary` the Engine, copy a new DLL into a per-init shadow path (`Runtime\.engine_loads\RynthCore.Engine.gen2.dll` etc.), `LoadLibrary` it, and call `RynthCoreEngineInit` again with `initCount >= 2`. The Engine detects the hot-reload count and skips the LoginComplete gate, reseeds the player vitals quality pointer, and synthesises the LoginComplete signal so subscribers re-wire.

### D3D9 hook

- Vtable discovery: creates a throwaway device via `D3D9VTable.cs`, captures 119 vtable entries
- Hook installed via MinHook on `EndScene` (vtable index 42)
- All D3D9 calls go through cached delegates (stdcall, `this` as first param) — never via COM wrappers
- State save/restore is manual (get/set each render state) — D3D9 state blocks don't work reliably inside EndScene

### Multi-client coexistence with other launchers (ThwargLauncher etc.)

`MultiClientHooks` patches two `acclient.exe` functions: `Client::IsAlreadyRunning` (always returns false) and `CLBlockAllocator::OpenDataFile` (ORs in the shared-access flag). That makes RynthCore-launched clients shareable. **But existing clients launched by other tools may have opened the DAT files exclusively** — once those locks exist, no new opener can read the DATs even with the shared flag.

The bulletproof solution is **a second AC client install** for RynthCore launches:

- Default AC install at `C:\Turbine\Asheron's Call\` — used by ThwargLauncher and other tools
- RynthCore-private copy at `C:\Games\RynthCore\AcClient\` — full directory copy of the AC client (~1.4 GB, mostly DAT files)
- The launcher's `AcClientPath` setting points at the private copy's `acclient.exe`

Different physical files = different lock domains, so the two stacks never contend on DAT access.

### Decal coexistence mode

When `decal.dll`, `UBLoader.dll`, `Decal.Adapter.dll`, or `phatacd.dll` is loaded into the same acclient.exe, the Engine **skips** the entire D3D9 / ImGui path and runs in coexistence mode:

- No EndScene hook (two independent D3D9 hookers can't symmetrically save/restore state)
- No ImGui overlay
- Avalonia floating panels still render via the separate `LayeredWindow` (GDI) path
- Plugin lifecycle (`InitPlugins`, `OnLoginComplete`, world-state replay) is driven manually after a 10s warm-up delay
- A 30 Hz worker thread drives `PluginManager.TickAll()` in place of the EndScene-driven tick

### ImGui / cimgui version

- ImGui.NET NuGet: **1.91.6.1**
- cimgui.dll must match: use `%USERPROFILE%\.nuget\packages\imgui.net\1.91.6.1\runtimes\win-x86\native\cimgui.dll` (or the bundled copy under `Native\`)
- Engine's MSBuild Target stages cimgui.dll into the publish output as both `cimgui.dll` and `RynthCore.cimgui.dll` so the resolver can find a private copy
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

### Pattern scanning

`Compatibility/PatternScanner.cs` is the canonical seam discovery. The `docs/ACCLIENT_HOOK_INVENTORY.md` file tracks every hook target with:
- Source seam (from the AC client decompile, used for *semantics only*)
- Live-binary plan (the pattern we actually scan for in the user's `acclient.exe`)
- Status (live / ready for patterning / investigating)
- Priority

Do **not** ship fixed RVAs from the decompile. Always pattern-scan against the live binary.

---

## Common Pitfalls

| Symptom | Cause |
|---------|-------|
| `DisplaySize = <1, 1>` | cimgui.dll version mismatch (pre/post 1.90 layout) |
| Font cache crash on frame 2 | `io.Fonts.TexID` was zero after init (usually fixed by matching cimgui) |
| Crash in `SetupRenderStateNative` | Do not call `SetStreamSource(null, 0)` — invalid for UP drawing |
| Hook not firing | acclient.exe must be running and past login before injection (or use the suspended-launch + early-inject path) |
| Plugin loaded but inert | You ran `dotnet build` instead of `dotnet publish` — managed-only DLL, no exports |
| Plugin in coexistence mode never ticks | EngineLifecycle's coexistence mode runs its own 30 Hz tick pump; check `RynthCore.log` for `DecalCoexistence: starting plugin tick pump` |
| `ChangePortalMode` event | Crashes Decal — never use (note carried from NexTank) |
| `vswhere.exe is not recognized` | Add `C:\Program Files (x86)\Microsoft Visual Studio\Installer` to PATH for the NativeAOT link step |

---

## Legal Guardrails

See `docs/LEGAL_COMPATIBILITY.md`. In short:

- We may build compatibility with publicly documented or behaviorally observed plugin interfaces
- We may use the AC client decompile for semantics and the live binary for hook placement
- We do **not** decompile, disassemble, patch, or copy code from Decal, VTank, or any other closed plugin
- We do **not** redistribute Decal, VTank, or third-party closed plugin binaries
- Prefer RynthCore-native plugins over legacy binary compatibility whenever possible
