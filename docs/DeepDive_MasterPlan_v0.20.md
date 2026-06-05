# RynthCore — Deep-Dive Master Plan (v0.20 refresh)

**Date:** 2026-06-03
**Scope:** RynthCore platform only — engine, launcher, injector/loader, plugin SDK, in-game
rendering. (RynthAi/RynthSuite plugin logic is out of scope here; see the per-manager
`*_Review.md` docs in `RynthSuite\Docs` for that.)
**Basis:** Code-grounded audit of the live tree at git tag **v0.20**, file:line verified.
Supersedes the engine/launcher portions of `RynthSuite\Docs\DeepDive_MasterPlan_2026-05-16.md`
(written at v0.13/v0.14 — now 6 versions stale; its hook inventory in particular lists
shipped hooks as "not implemented").

---

## VERDICT

1. **The crash era is over by design, not by luck.** The three worst crash classes from the
   May plan are fixed at the *architecture* level, not patched over:
   - Off-thread TOCTOU on AC's non-reentrant qualities tables → every `Inq*` accessor now
     fails closed off the main thread and serves main-thread snapshots
     (`ClientObjectHooks.cs:1406`, `TryGetQualitiesPtr` `:772`; snapshots driven from
     `EngineFrameController.cs:273-295`).
   - The `0x0055FA24` / `null+0x40` object-teardown AV (the long "⚠ ACTIVE" investigation)
     → root-caused via the new `AcMainThreadQueue`, which marshals **all** off-thread AC
     mutations onto AC's own threads (`AcMainThreadQueue.cs:14` names the exact crash;
     combat/move/jump/use drain on EndScene `EngineFrameController.cs:315`, casts drain on
     `Client::UseTime` `AcMainThreadQueue.cs:206-218`), backed by a per-callsite native SEH
     trampoline (`SehTrampoline.cs`, wired at `EntryPoint.cs:433-437`).
   - `OverlayTextureRenderer.UploadFrame` width/buffer desync → pixels + dimensions now
     travel as one immutable `OverlaySurfaceFrame` under a single lock.

2. **One thing to *verify*, not build.** The cast-path marshalling — the *last* off-thread
   AC mutator to be moved into the queue — landed **2026-06-03 (today)**
   (`EntryPoint.cs:519-525`). It is resolved in design but <24h old. **Run a multi-session
   combat/buff soak watching `native-crash.log` + heartbeat continuity +
   `AcMainThreadQueue.DroppedCount` before marking `rynthcore_crash_investigation.md`
   closed.** This is the single highest-value confirmation item.

3. **The question has shifted from "stop crashing" to "make it a platform."** The remaining
   work is platform maturation, not bug-chasing. It clusters into five themes below.

### Strategic lens — the RC2 fork

RynthCore2 (`RynthSuite2`) is in flight as the eventual *replacement* for the in-process
engine, but it is ~1 week into a ~4–5 month plan. **RynthCore is the daily driver now and
will be for months.** Therefore:

- **Durable v1 investments:** hardening you benefit from *every day* on v1, and
  *out-of-process launcher* features (RC2-agnostic, zero engine risk).
- **Probably RC2's job, not v1's:** deep SDK/plugin-platform re-architecture (plugin-owned
  UI, source-generated ABI). Cherry-pick only the cheap wins in v1.

This lens drives the recommended sequence at the end.

---

## Status delta vs the May 2026-05-16 plan

| May-plan item | v0.20 status |
| --- | --- |
| P0-1 `UploadFrame` width/buffer desync | **FIXED** — `OverlaySurfaceFrame` carries W/H with pixels under one lock |
| P0-2 `ClientObjectHooks` cross-thread TOCTOU (~25 reads) | **FIXED** — all `Inq*` accessors gate on `MainThreadGuard.IsOnMainThread()`, fail closed, serve snapshots |
| P0-3 SmartBox/packet reverse-P/Invoke watch-item | Mitigated by the `AcMainThreadQueue` marshalling architecture |
| 0x0055FA24 teardown AV (crash investigation) | **RESOLVED in design** (today) — soak pending |
| P1-4 launcher `SessionStateStore` hard-`return null` | **FIXED** — reads correctly (`SessionStateStore.cs:44-86`) |
| P1-5 `DecalDetection` `Process.Modules` NativeAOT pitfall | **STILL OPEN** (`DecalDetection.cs:52-53`) |
| P2 fixed-RVA hardening (no pattern-scan) | **PARTIALLY ADDRESSED** — teardown-critical hooks moved to `HookResolver`; the large `ClientObjectHooks`/`PlayerVitalsHooks`/`CObjectMaintHooks` VA surface still raw |
| P2 hot-reload module leak + fixed-sleep drains | **STILL OPEN** (intentional band-aid, unchanged) |
| P2 `RecursionGuard.Tick` no-op | **STILL OPEN** (cosmetic) |

Net: the worst items are gone; remaining debt is **binary portability** and **lifecycle
determinism** — classic "stable platform" maturation.

---

## Theme 1 — Finish the last hardening mile (pays off on v1, daily)

The platform is structurally sound, but its stability today is really **"stable on *your*
`acclient.exe`,"** which is not the same as stable.

**1a — [P0] Pattern-scan the hardcoded VA surface. THE #1 latent risk.**
~25 function VAs + several data VAs are still hardcoded with **no pattern-scan and no
signature verify**, bound straight via `Marshal.GetDelegateForFunctionPointer`:
- `ClientObjectHooks.cs:574-612` (the whole Inq* table; only 3 gated by `IsPointerInModule`
  at `:578-586`)
- `PlayerVitalsHooks.cs` (Inq* by fixed VA)
- `CObjectMaintHooks.cs:46` (`S_PC_INSTANCE_VA = 0x00842ADC`)
- `RawPacketHooks.cs:21` (`RecvFromPtrAddr = 0x007935AC`)

This **violates the CLAUDE.md "never ship fixed RVAs" rule.** On any AC patch drift or ACE
rebuild these bind to the wrong address → hard AV *inside AC*, not a graceful skip. The
correct pattern already exists in-tree: `HookResolver.Resolve` (used by 11 hooks incl.
`DbCacheTeardownHooks.cs:80-82`, `CombatModeHooks`, `RadarHooks`, `ChatHooks`,
`PowerbarHooks`, `DoMotionHooks`, `AccountHooks`, `BusyCountHooks`, `LogoutLifecycleHooks`,
`MultiClientHooks`, `SalvageHooks`), and a middle tier (fixed VA + `PatternScanner.VerifyBytes`
fail-safe) at `CreateObjectHooks.cs:11-50` / `DeleteObjectHooks.cs:11-48`. Pattern-gen
tooling exists under `tools/HookResolver.Core/Symbols/AllSymbols.cs`.
**Effort: 2–4 days (~25 patterns to author + verify against the live binary). Impact: very
high — makes the engine survivable across AC patch levels and ACE rebuilds.**

**1b — [P0] Replace `DecalDetection`'s `Process.Modules` walk. ~1–2h, pure downside removal.**
`DecalDetection.cs:52-53` still does `Process.GetCurrentProcess().Modules` — the exact
`System.Diagnostics.Process`-AV pitfall from `rynthcore_nativeaot_pitfalls.md` — and it
gates the *entire* D3D9-vs-coexistence init path (`EntryPoint.cs:575,623`). It's try/caught,
but an AV is not a managed exception. Fix: 4 `GetModuleHandleW` probes against the known
Decal DLL names (`DecalDetection.cs:18-24`), same technique as `AcClientModule.cs:34-45`; no
`Process` object at all. Also sweep the lower-risk `SessionStateRegistry.cs:141`
(`Process.StartTime`).

**1c — [P2] Hot-reload determinism ("Path A"). 3–5 days; dev-loop nicety, not player-facing.**
The Loader deliberately *leaks* the ~26 MB engine per generation (`Loader/EntryPoint.cs:456-472`)
and drains threads with fixed `Sleep(150)` (`:454`) / `Sleep(80)` (`EngineLifecycle.cs:115`)
instead of joins; `EngineLifecycle` does `MH_DisableHook(MH_ALL_HOOKS)` and skips
`MH_Uninitialize` (`:143-155`). The "holds ~7 reloads" ceiling is unchanged. Blocker is
thread sprawl (a dozen long-lived threads, only the plugin pump `:132` and `HeartbeatLogger`
`:141` joined). Prereq = a central `ThreadRegistry` + timeout-joined shutdown, which then
makes a real `FreeLibrary` (no leak) tractable. Mitigations already added: 1500 ms reload
cooldown (`Loader/EntryPoint.cs:80,372-381`) + CAS in-flight gate (`:387-391`).

**1d — [P3] Cheap hygiene + observability. ~½ day.**
- Delete `RecursionGuard.Tick` (permanent no-op, `RecursionGuard.cs:53`) and its 24 call
  sites across 16 hook files — dead scaffolding implying a safety net that doesn't exist.
- Add a throttled health heartbeat to the unified log (live thread count,
  `AcMainThreadQueue` depth + `DroppedCount`, SEH AV count/last site, prefetch ages). Today
  the rich `CrashLogger` managed dumper is dead code (`CrashLogger.cs:125-148`, proven
  fatal); live crash capture is the native trampoline VEH writing `native-crash.log`
  (`EntryPoint.cs:444`). Silent degradation (dropped bot actions, repeated SEH AVs) is
  currently invisible.

**1e — [P2, new finding] Snapshot-prefetch render-thread cost.** Five `CObjectMaint` table
walks per frame run in `OnEndScene` on AC's render thread (`EngineFrameController.cs:273-295`);
`PrefetchAttackable` recomputes attackable *per live object* every 500 ms via 3
SEH-trampolined native calls each (`ClientObjectHooks.cs:2142-2180`). Tuned hard during
crash isolation; now that the crash is fixed at the source, move to incremental
(compute-once-per-id, evict on `DeleteObject`) to cut steady-state render-thread load on
busy landblocks.

---

## Theme 2 — Multi-box command & control (launcher; out-of-process, near-zero risk)

Best fit for a daily multi-boxer and for the "out-of-process / won't affect the plugin"
preference. **All launcher-side (a normal managed app) → zero stability risk to the injected
engine, fully RC2-agnostic.** The May launcher items are mostly *fixed* now
(`SessionStateStore` reads correctly; auto-relaunch, window-placement, title-rewrite all
work — title format `Account / Character @ Server`, `ComputeDesiredWindowTitle`
`MainWindow.axaml.cs:2591-2623`). What's missing is new capability:

| Gap | Today | Opportunity / impact |
| --- | --- | --- |
| **Cross-client vitals dashboard** | Session grid = login/uptime/graphics-ready only (`SessionStateStore.cs:11-26`); no HP/mana/stam | One row/client with colorblind-safe bars + "this alt is dying" highlight. **No ThwargLauncher equivalent — a differentiator.** Impact: very high. |
| **Targeted, bidirectional command bus** | `dispatch.txt` is **global, one-way** (`ChatFileDispatcher.cs:44-47`) | ThwargLauncher has per-client + team-scoped + bidirectional commands (`CommandManager`). Per-PID command files + a launcher send box unlock follow/formation, synced buffing, mass-recall. **Biggest functional parity gap.** |
| **Teams / named launch sets** | One flat checked list; re-check boxes every session (no group/team/set field exists anywhere) | `LaunchSets` (name → profile IDs) + a `Team` tag on profiles. Pure settings model, no engine change. Also the scoping key the command bus needs. |
| **Window arrangement** | Restores saved per-account rects only (`ApplyOrPersistWindowPlacement` `:2400`); no tile/cascade/grid | "Tile / Cascade / NxM grid on monitor X" on top of the existing `SetWindowPos`/`EnumWindows`/`FindLargestVisibleWindowForProcess` plumbing. |

⚠ **Engine-side pitfall for the vitals feed:** the writer must **not** call
`TryReadForProcess` / `JsonDocument.Parse` on the injected thread — it re-enters
`DatFileShareHooks.CreateFile` and stack-overflows ~17 s post-login
(`SessionStateRegistry.cs:78-84`). Write-only from the engine (`vitals_{pid}.json` off the
30 Hz / EndScene tick); parse only launcher-side.

**Open bug to fix while in there:** `EngineInjectionService.FindTargetProcesses` returns
**undisposed `Process[]`** re-queried 3×/tick (`EngineInjectionService.cs:98-101`; called
`MainWindow.axaml.cs:1809,2158,2846`) — kernel-handle leak over a long multi-box day.
Correct dispose pattern already exists at `MemoryTabView.axaml.cs:267-271`. While there, add
an "all clients" row to the Memory tab (current Virtual-MB vs the x86 4 GB LAA ceiling per
PID) so you can spot the client about to hit the wall.

---

## Theme 3 — In-game UX features (high player-facing value)

Strong foundation already: radar with filtering (`RadarPanel.cs:49-138`), XP/Lum/Kills-per-hr
tracker (`RynthTrackerPanel.cs:125-132`), savable + detachable-to-OS-window panels
(`PanelStateStore.cs:31-39`). The high-value gaps:

1. **[high] World-space ESP text labels.** `WorldToScreen` exists (`PluginContract.cs:212`)
   but the Nav3D API has **no text/billboard sink** — only ring/line/triangle
   (`Nav3DAddRing` `:232`, `AddLine` `:238`, `AddTriangle` `:530`). So monster name/HP/level,
   item names, and distance labels over objects are impossible for plugins today. Add
   `Nav3DAddTextWorld(x,y,z, text, color, flags)` rendered via the already-loaded ImGui font
   atlas (or a screen-space text sink fed by `WorldToScreen`). **Highest-value missing
   overlay class. Effort: medium.**
2. **[high] Low-latency direct-draw combat HUD.** There is **no** direct-draw HUD anywhere
   (grep-confirmed); all HUD goes through the Avalonia composite path (render → buffer copy →
   blit a frame later) — fine for config, laggy for combat vitals/cast timers. The RC2 "Hot
   HUD" already proved the EndScene-detour direct-draw approach at ~2.8 ms median (the device
   is a standard `IDirect3DDevice9`, even queried as `…Ex` at `OverlayTextureRenderer.cs:21`).
   Port it for vitals + cast/swing timers + target HP; degrade to Avalonia under Decal (no
   EndScene hook there). **Effort: medium, reference impl exists.**
3. **[high] Alerts/notifications subsystem.** None today. Configurable toasts (low
   vital / rare drop / debuff landed / vitae), especially **cross-client** for multi-box.
4. **[low] Screenshots.** The compositor already produces a full BGRA frame each frame
   (`AvaloniaOverlay.cs:3362`) — "save annotated screenshot" is nearly free.
5. **[low] Damage meter / combat analytics** — Tracker covers XP/kills but not per-target
   DPS / damage-taken.

---

## Theme 4 — Plugin platform & SDK ergonomics (real, but weigh against RC2)

The RC2 lens matters most here. Findings are concrete (API version is **61**,
`RynthCoreHost.cs:8` / `PluginContract.cs:547`):

- **[blocker] Plugins can't own their UI.** Every plugin panel (`RynthAiPanel`,
  `RynthTrackerPanel`, …) lives *in the engine*, and each plugin is hardcoded by DLL
  filename at `EntryPoint.cs:699-717`. A third party literally **cannot ship a plugin with
  UI** without forking and rebuilding the 26 MB NativeAOT engine. The panel↔plugin link is
  bespoke JSON-over-`GetProcAddress` per plugin (e.g. `RynthTrackerGetSnapshotJson`).
- **[risk] The ABI is two hand-synced structs with no drift protection** —
  `RynthCoreApiNative` (SDK) ↔ `RynthCoreAPI` (engine). Adding one host API is a 5-site,
  2-version-bump ritual (`PluginManager.EnsureHostCallbacks` `:2004-2210` is one of them); a
  mis-ordered insert silently corrupts every pointer after it.
- **[friction] Onboarding is reverse-engineering.** No `dotnet new` template, no sample, no
  getting-started doc; the NuGet has `RepositoryUrl = example.invalid` and the real plugins
  ignore it for project-refs; `RynthCore.PluginCore` (the class you inherit) isn't packaged;
  `dotnet build` silently produces an inert DLL (must `publish`). Default
  `MinimumApiVersion => CurrentApiVersion` (`RynthPluginBase.cs:9`) makes every plugin
  brittle-by-default — breaks on any engine older than its build.
- **Good news:** nearly all the hook-inventory "desirable host APIs" are **already exposed**
  (UseObject/UseObjectOn/UseEquippedItem, MoveItemExternal/Internal, TurnToHeading/
  StopCompletely/SetAutoRun, GetCurCoords/GetPlayerId/GetGroundContainerId, GetItemName,
  ItemIsKnown). **Only two remain unaddressed: `IsStandingStill` and a true
  `GetScreenDimensions`** (`TryGetViewportSize` `:1028` gives only the D3D viewport).

**Recommendation:** plugin-owned UI + source-generated ABI + a template would genuinely make
RynthCore a platform others build on — but that's a multi-week re-architecture and **precisely
what RC2 is being built to solve differently.** Unless v1 is meant to be the long-term public
plugin platform, treat most of this as RC2's charter. Cheap v1 wins worth taking now: expose
`IsStandingStill` + `GetScreenDimensions`; surface *why* a plugin was auto-disabled
(`PluginManager.cs:879`) instead of a silent log line.

---

## Theme 5 — Cleanup

- **[medium] Decommission the ImGui multi-viewport scaffolding.**
  `ViewportPlatformBackend.cs` (829 lines) + `ViewportRendererBackend.cs` + probes (~1.5 k
  lines) are gated off (`ViewportsEnable` commented at `EngineFrameController.cs:154`; init
  conditional at `:173`), and the feature previously caused a login-time stack-overflow AV.
  **Avalonia already provides out-of-AC floating panels** (`FloatingPanelHost`,
  `AvaloniaOverlay.cs:3666`), and the whole ImGui *shell* runs `EnableImGuiShell:false` in
  dev mode. Either delete the scaffolding (recommended) or commit to the
  cimgui-with-`IMGUI_ENABLE_VIEWPORTS` rebuild + offset re-verification.
- **[low] Per-frame `NativeMemory.Alloc/Free` in the ImGui vertex path**
  (`DX9Backend.cs:630,699`) — safe (not the LFH heap) but avoidable churn; reuse a grow-only
  buffer like `_navTriBatch` (`:1041`). Only matters if the shell is re-enabled.
- **[low] Hardcoded `C:\Games\RynthSuite\RynthAi\` for `imgui.ini`/qualities paths**
  (`EngineFrameController.cs:134-135`) — derive from the engine dir / `LogPaths`.

---

## Recommended sequence

Weighted for: pays off on v1 daily · low risk · RC2-agnostic · matches the
out-of-process working style.

1. **Soak-verify today's cast-marshalling fix** → close `rynthcore_crash_investigation.md`.
   *(½ day monitored play — confirmation, not building.)*
2. **Theme 1a (pattern-scan the VA surface)** + the **1b `DecalDetection`** 2-hour fix
   alongside it. *(The hardening that makes "stable" actually portable; fixes the own-rule
   violation.)*
3. **Theme 2 (multi-box C&C):** vitals dashboard → targeted command bus + teams → window
   tiling. *(Out-of-process, zero engine risk, biggest day-to-day QoL.)*
4. **Theme 3 (in-game UX):** ESP world labels, then the low-latency combat HUD.
5. **Theme 4 SDK items only if** RynthCore stays the primary platform rather than ceding that
   role to RC2; otherwise take just the two cheap host-API wins.
6. **Theme 5 cleanup** opportunistically (delete viewport scaffolding + `RecursionGuard`).

---

## Appendix — audited item status (file:line evidence)

### Engine (8 items)
1. `UploadFrame` desync — **FIXED** (`OverlaySurfaceFrame.cs:24-44`,
   `SoftwareOverlaySurfaceBridge.cs:30-70`, `OverlayTextureRenderer.cs:215,262-306`).
2. `ClientObjectHooks` cross-thread TOCTOU — **FIXED** (gates at `:627,660,772,1166,1195,
   1406,1535,1692,1773,1815,1859,1905,1965,2049,2233`; `MainThreadGuard.cs:50-55` fails
   closed pre-TID).
3. `Nav3DRenderInjector.Detour` on render thread — **OPEN by design, low risk**
   (`Nav3DRenderInjector.cs:82-93`; required for Z-order at `EngineFrameController.cs:318-335`).
4. Fixed RVAs without pattern-scan — **PARTIALLY FIXED** (see 1a).
5. Hot-reload module leak + fixed-sleep drains — **OPEN** (see 1c).
6. `RecursionGuard.Tick` no-op — **OPEN, cosmetic** (`RecursionGuard.cs:53`, 24 call sites).
7. `DecalDetection.ProbeOnce` `Process.Modules` — **OPEN** (see 1b).
8. `0x0055FA24` teardown AV — **RESOLVED in design** (`AcMainThreadQueue.cs`,
   `SehTrampoline.cs`, `DbCacheTeardownHooks.cs:107-130`, `DeleteObjectHooks.cs:70-81`); cast
   path re-enabled today (`EntryPoint.cs:519-525`) — **soak pending.**

### Launcher (7 items)
1. `SessionStateStore.TryReadForProcess` hard-`return null` — **FIXED**
   (`SessionStateStore.cs:44-86`).
2. Auto-relaunch on mid-session crash — **WORKING** (`MainWindow.axaml.cs:2252,2284,2687,
   2726`; circuit breaker `:2698`). Caveat: only fires for *checked* launch targets.
3. Window-position placement — **WORKING** (`:2400`, gated on `IsLoggedIn` `:2467-2469`).
4. Title-rewrite — **WORKING** (`:2565`; format `Account / Character @ Server` `:2591-2623`).
5. `LaunchContextStore.WriteLegacy` race — **PRESENT but BENIGN** (per-PID file is
   authoritative; AC suspended until after the per-PID write).
6. Per-account UserPrefs swap serialization — **STILL A BOTTLENECK by necessity**
   (`_userPrefsSwapLock` `:86`, 2 s settle `:89,1236`); only bites accounts with per-account
   prefs stashes. True fix = per-process prefs isolation.
7. `FindTargetProcesses` undisposed `Process[]` — **OPEN** (`EngineInjectionService.cs:98-101`).

---

## Cross-references

- Superseded (engine/launcher portions): `RynthSuite\Docs\DeepDive_MasterPlan_2026-05-16.md`.
- Hook placement / portability rules: `docs/ACCLIENT_HOOK_INVENTORY.md` (stale — many listed
  "not implemented" hooks now ship), `docs/PLUGIN_HOOK_MATRIX.md`.
- Crash-class history & rules (memory): `rynthcore_crash_investigation.md`,
  `rynthcore_nativeaot_pitfalls.md`, `rynthcore_overlay_lfh_pitfall.md`,
  `rynthcore_hot_reload_architecture.md`.
- RC2 successor effort (memory): `rynthcore2_two_process_initiative.md`.
- Per-plugin (RynthAi) problem statements: the `*_Review.md` docs in `RynthSuite\Docs`.
