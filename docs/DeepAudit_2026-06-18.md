# RynthCore / RynthAi Deep Code Audit — Final Report

> Generated 2026-06-18 by a 14-finder multi-agent audit with per-finding adversarial verification.
> 107 findings raised → 39 confirmed after adversarial re-verification → 33 entries below (near-duplicates merged).
> Each entry survived an independent skeptic that re-read the cited code and defaulted to refuting.

## Executive Summary

The audit surfaced 33 verified defects, overwhelmingly clustered in one root-cause theme: **off-thread access to AC's single-threaded native state** (raw pointer dereferences and AC-mutating calls running on the plugin pump, Avalonia UI, or ThreadPool threads instead of AC's main thread), where a fault is an uncatchable NativeAOT fail-fast (0xC0000602) that kills the live client. A second recurring theme is **missing defensive guards on reverse-P/Invoke detour bodies and hash-table/buffer walks** (no try/catch, no chain-length caps, no length-bounding), and a third is **unsynchronized shared collections** (Dictionaries/HashSets/Lists touched from two engine threads). Overall health is fair: the engine has already built the right primitives (`AcMainThreadQueue`, `MainThreadGuard`, `IsReadablePointer`, `SehTrampoline`, host-marshalled snapshots) and applied them broadly — most findings are *gaps* where a single call site, method, or subsystem was missed during otherwise-systematic hardening sweeps. The highest leverage is closing the handful of unguarded off-thread AC reads/mutations that can detonate the client.

---

## Critical & High

**1. FellowshipTracker reads live AC memory off-thread via raw `Marshal.ReadInt32` of hardcoded VAs**
`C:/Projects/RynthSuite/Plugins/RynthCore.Plugin.RynthAi/Combat/FellowshipTracker.cs:45-271` · *native-interop* · severity **critical**
Every public member dereferences hardcoded acclient VAs (`0x0087150C`, `0x00844C08`) and chases the CFellowship hash-table pointer chain with raw `Marshal.ReadInt32`, behind only managed try/catch. It runs on the plugin pump/meta thread (via `ExpressionEngine` Think and directly from `OnTick:433-435` for fellowship-follow), which is not AC's main thread. AC can free/rebuild the fellowship struct concurrently (join/leave/disband/relog), so a torn pointer chase produces a native AV that NativeAOT cannot catch — fail-fasting the client. This is the sole subsystem bypassing the host entirely (H1 + H2 + H5); hardcoded VAs also break under the engine's pattern-relocation regime.
*Fix:* Add a main-thread-marshalled host snapshot primitive (`HasGetFellowship`/`ReadFellowship`) mirroring `ReadKnownSpells`/`ReadPlayerEnchantments`, pattern/data-xref resolve the two VAs, and gate behind a `Has*` capability check. If a raw read must remain, wrap each pointer-chase in `SehTrampoline` and call only from a marshalled main-thread context.

**2. CommandInterpreter jump/recovery calls bypass the main-thread guard (off-thread motion-graph mutation)**
`C:/Projects/RynthCore/src/RynthCore.Engine/Compatibility/CommandInterpreterHooks.cs:231-328` · *race-threading* · severity **high** (adjusted from critical)
`DoJump`, `CommenceJump`, `TapJump`, `PlayerTeleported`, `TakeControlFromServer`, and `ClearAllCommands` call native CommandInterpreter members through `_boundCmdInterp` with no `MainThreadGuard.IsOnMainThread()` gate, while their siblings (`SetAutoRun`, `SetMotion`, `StopCompletely`, `TurnToHeading`) all marshal via `AcMainThreadQueue`. The jump trio is live-exercised off-thread in Decal-coexistence mode (plugin `Jumper.cs:107` → host → unguarded native call), mutating the same CSequence/motion graph implicated in the documented corruption-AV class. The recovery triplet is latent today (only reached from the main-thread-gated `ForceResetBusyCount`) but is public and unguarded.
*Fix:* Add `ActionKind` + Enqueue/Drain entries for the three jump mutators with an `IsOnMainThread` early-return mirroring `SetAutoRun`. Gate `PlayerTeleported`/`TakeControlFromServer`/`ClearAllCommands` defensively. Each jump call is an independent per-tick action, so simple enqueue preserves the CommenceJump→DoJump cross-tick ordering.

**3. ReadCurrentCombatMode does a raw off-thread pointer-chase (uncatchable AV)**
`C:/Projects/RynthCore/src/RynthCore.Engine/Compatibility/CombatModeHooks.cs:49-63` · *crash-AV* · severity **high** (adjusted from critical)
Reads the ClientCombatSystem singleton then `*(int*)(combatSystem + 0x1C)` behind only try/catch, exposed to plugins via `GetCurrentCombatModeFn` with no main-thread gate. During logout/teleport teardown the singleton can be a freed/torn non-null heap pointer; the chased deref faults at a non-null page — an uncatchable 0xC0000005. The sibling `BusyCountHooks.ReadRealBusy` already guards the identical pattern; this is the lone omission.
*Fix:* If `!MainThreadGuard.IsOnMainThread()` return `NormalizeCombatMode(_lastObservedCombatMode)` (already maintained by `SetCombatModeDetour`). Add `IsReadablePointer(combatSystem + CombatModeOffset)` before the chased deref as belt-and-suspenders. The first `.data` read is fine.

**4. Off-thread AC chat-state mutation on the OnLogin command path (merged)**
`C:/Projects/RynthCore/src/RynthCore.Engine/Compatibility/OnLoginCommandRunner.cs:53-111` + `ChatCommandDispatcher.cs:144,205-208,293-327` · *race-threading* · severity **high**
*(Merges the two OnLogin-dispatch findings — same root cause.)* `OnLoginComplete` fires `Task.Run(DispatchAsync)`, whose post-`Task.Delay` continuation runs on a ThreadPool thread and calls `ChatCommandDispatcher.Dispatch`. That path is not network-only and does not marshal: it runs arbitrary plugin `OnChatBarEnter` handlers (`DispatchChatBarEnter`), constructs/destructs AC's native `PStringBase<char>` (alloc/free in AC's heap), and calls `Event_Talk`/`OutgoingChat` directly against AC's live chat-manager — all off AC's main thread (H1, the documented 0x00460D1D chat-buffer write-AV class). The inline "safe from any thread" comment is an inherited Decal/VTank assumption that contradicts the engine's own `AcMainThreadQueue` design.
*Fix:* Marshal the native dispatch (and the inter-command delay) onto `AcMainThreadQueue` (a new string-carrying chat slot, like the existing `EnqueueWriteToChat`), running each command on the EndScene tick. Apply the fix inside `ChatCommandDispatcher.Dispatch`/its leaves so it also covers the other off-thread callers (`ChatFileDispatcher`'s FileSystemWatcher thread, `Host.InvokeChatParser`).

**5. Raycast geometry caches shared across OnTick and OnRender threads without locking**
`C:/Projects/RynthSuite/Plugins/RynthCore.Plugin.RynthAi/Raycasting/GeometryLoader.cs` (+ `Shared/RynthCore.TerrainData/TerrainSampler.cs`, `DungeonLOS`) · *race-threading* · severity **high**
The same `GeometryLoader` instance caches parsed dat geometry in unsynchronized `Dictionary` fields read from two engine threads: combat LOS on the pump thread (`CombatManager.IsTargetBlocked` → `GetLandblockGeometry`) and dungeon-map render on AC's EndScene thread (`DungeonMapUi.RefreshMap` → `GetDungeonMap*`). `GetLandblockGeometry`/`GetSetupBoundingVolumes`/`LoadGfxObjMesh` both read and mutate (`TryGetValue`/`Remove`/`Add`/`Clear`); a concurrent read during a resize/`Clear()` corrupts bucket arrays (hang or AV), and an unguarded throw on the render thread fail-fasts NativeAOT. The same plugin already implements the safe pattern in RynthNav's `_reqGate`.
*Fix:* Force all geometry build+query onto the pump thread (consume an immutable snapshot in the renderer), or guard every cache read/insert/evict/`Clear` in `GeometryLoader` + `DungeonLOS` + `ScatterSystem` with a shared `lock(_cacheGate)`.

**6. RynthVision InspectTerrain races the unsynchronized `TerrainSampler._cache` Dictionary**
`C:/Projects/RynthSuite/Plugins/RynthCore.Plugin.RynthVision/RynthVisionPlugin.cs:144-174` (+ `TerrainSampler.cs:94-103`, `SlopeOverlay.cs:107`, `WaterOverlay.cs:114`) · *race-threading* · severity **high**
`RynthVisionInspectTerrain` is invoked synchronously from the Avalonia UI thread (`RynthVisionPanel.cs:128` Button.Click) and calls `LoadLandblock`, which mutates a plain `Dictionary` (`TryGetValue`/`Clear`/indexer-add) with no lock — while the AC tick thread hits the same `LoadLandblock` hundreds of times per Submit from the slope/water overlays. Two threads writing one Dictionary (with frequent `Clear()` at `MaxCache=30`) is classic bucket-array corruption on the AC main thread.
*Fix:* Marshal `RynthVisionInspectTerrain` onto OnTick via a pending flag (mirror RynthNav's `_reqGate`) — this also fixes its off-thread `TryGetPlayerPose` read. Alternatively lock/`ConcurrentDictionary` the cache, but marshalling is preferable.

**7. WaitForSingleObject timeout not detected — STILL_ACTIVE (259) used as module base, then a remote thread created at base+RVA**
`C:/Projects/RynthCore/src/RynthCore.Injector/EngineInjectionService.cs:248-294` · *native-interop* · severity **high** (adjusted from critical)
`InjectDllAndCallExport` discards the `WaitForSingleObject(loadThread, 10000)` return; the only failure gate is `loadLibResult == 0`. On timeout the thread keeps running and `GetExitCodeThread` yields STILL_ACTIVE (259); `259 != 0` passes, so `remoteBase = (IntPtr)259`, `remoteInitAddr = Add(259, exportRva)`, and `CreateRemoteThread` runs at ~0x103+RVA inside the live client — immediate AV/arbitrary execution. The init-export thread (283-294) repeats the missing check and mislogs a 259 as success.
*Fix:* `uint w = WaitForSingleObject(...); if (w != WAIT_OBJECT_0) return Failure(...)`; also explicitly reject `loadLibResult == 259`. Apply the same `WAIT_OBJECT_0` check before trusting the init-thread exit code.

**8. EndScene rehook frees the trampoline it is about to call through (use-after-free on rehook failure)**
`C:/Projects/RynthCore/src/RynthCore.Engine/D3D9/EndSceneHook.cs:257-278` · *crash-AV* · severity **high** (adjusted from critical)
On a first-frame vtable mismatch the detour does `MH_DisableHook`+`MH_RemoveHook` (freeing the trampoline `_originalEndScene` points at), sets `_installed=false`, then re-installs. If re-install throws (`MinHook.HookCreate`), `_originalEndScene` was never reassigned and still binds the freed trampoline; the code forces `_installed=true` and the function-bottom (`return _originalEndScene!(pDevice)`, outside the try/catch) invokes freed memory. Even on success the trampoline is removed with no in-flight drain (contrast `Uninstall`'s `Sleep(80)`). Narrow trigger (mismatch + create-fail) but fatal.
*Fix:* Capture the old original into a local; only `RemoveHook` after a successful new `CreateHook`; on failure re-enable the original instead of setting `_installed=true` with a dangling delegate. Ideally defer the rehook off the trampoline stack (record mismatch, return the original this frame, rehook next frame).

**9. Jumper leaves navigation permanently disabled when jump hooks fail to start**
`C:/Projects/RynthSuite/Plugins/RynthCore.Plugin.RynthAi/Jumper.cs:82-97,123-128,153-182,203-220` · *logic-bug* · severity **high**
`Start()` calls `PauseNav()` (disables `EnableNavigation`) before the jump. If `CommenceJump()` returns false while `HasCommenceJump` is true (busy/mid-air rejection — pointer-bound ≠ call-succeeds), `_charging` stays false, `_waitingForJump` is never set, the TapJump fallback is skipped, and all four busy flags end false. `IsBusy` is false → `Tick()` early-returns forever → `RestoreNav()` is never reached (only called from the `_waitingForJump` branch and `Cancel()`, which fires only at logout). Navigation stays off for the rest of the session ("bot stands there"). The TapJump branch leaks identically.
*Fix:* At the unified `if (!started)` site (line 180), call `RestoreNav()` and reset `_addW.._addShift` so the next `Start()` begins clean and nav resumes — covering both failure paths.

**10. `System.Diagnostics.Process` used in-process inside acclient.exe (host-destabilizing)**
`C:/Projects/RynthSuite/Plugins/RynthCore.Plugin.RynthAi/LegacyUi/LegacyDashboardRenderer.cs:115,958-991` · *native-interop* · severity **high**
`LaunchMonsterEditor` stores a `Process` handle and uses `HasExited`/`CloseMainWindow()`/`Process.Start(psi)` — the documented H6 hazard (these handle-touching accessors silently AV the host under NativeAOT). It runs synchronously on the ImGui/game thread from the "External Editor" button. The engine already abandoned this API for `OpenProcess`/`GetExitCodeProcess` in `PluginLoader.cs` (`IsPidAliveWin32`) for exactly this reason.
*Fix:* Replace with `ShellExecuteExW` (set `SEE_MASK_NOCLOSEPROCESS` to retain `hProcess`)/`CreateProcessW` to launch; track HANDLE+PID; liveness via `WaitForSingleObject(h,0)==WAIT_TIMEOUT`; close via `PostMessage(WM_CLOSE)` (or `TerminateProcess` fallback); `CloseHandle` on teardown.

**11. D3D9 bootstrap detours are unguarded reverse-P/Invoke callbacks that fail-fast on any throw**
`C:/Projects/RynthCore/src/RynthCore.Engine/D3D9/D3D9Bootstrapper.cs:133-172,195-226` · *native-interop* · severity **medium** (adjusted from high)
`Direct3DCreate9Detour` and `CreateDeviceDetour` are delegate-marshalled native callbacks with no try/catch. The genuinely unguarded managed-throw path is `Direct3DCreate9Detour` → `TryHookCreateDevice` → `MinHook.HookCreate`/`Enable` (which throw `InvalidOperationException`); an escaping exception fail-fasts the client. The `CreateDeviceDetour` MinHook exposure is already pre-contained by `InstallFromEndSceneAddress`'s try/catch. Both detours live in the rarely-taken d3d9-not-yet-loaded fallback (bootstrap normally runs at login-complete when d3d9 is loaded), hence medium.
*Fix:* Wrap `TryHookCreateDevice` (and both detour bodies for parity) in try/catch that logs and swallows; the original call already happens first so it can degrade to a no-op install. Ideally route through `SehTrampoline` to also contain a native AV.

**12. UpdateObjectInventoryDetour body is not exception-guarded**
`C:/Projects/RynthCore/src/RynthCore.Engine/Compatibility/UpdateObjectInventoryHooks.cs:92-99` · *crash-AV* · severity **low** (adjusted from high)
The managed detour calls `_originalUpdateObjectInventory!` then `PluginManager.QueueUpdateObjectInventory(objectId)` with no try/catch; an escaping throw (e.g. OOM during the queue's lock+Enqueue) fail-fasts the client. Every sibling server-dispatch detour (`UpdateObjectServerDispatchHooks`, `VectorUpdateServerDispatchHooks`, `SmartBoxHooks`) wraps the queue call. Downgraded to low: the body parses no native memory, so the only realistic throw is OOM (already process-fatal) — this is a defensive-consistency gap, not a triggerable crash.
*Fix:* `try { if (objectId != 0) PluginManager.QueueUpdateObjectInventory(objectId); } catch { }`, matching the siblings. Keep the call-through to the original outside the catch.

**13. AppraisalProfile hash-table walks have no chain-length guard or pointer validation**
`C:/Projects/RynthCore/src/RynthCore.Engine/Compatibility/AppraisalHooks.cs:262-272,304-313,346-368` · *crash-AV* · severity **high**
`CacheIntProps`/`CacheBoolProps`/`CacheStringProps` walk PackableHashTable bucket chains with `while (node != IntPtr.Zero) { ... node = ReadIntPtr(node+8); }` — no max-chain cap and no `IsReadablePointer` on `node`, the bucket slot, or the string buffer. The detour runs inline on AC's main thread, so a cyclic `next` spins forever (hang) and a torn pointer AVs (uncatchable under NativeAOT). The sibling walkers `TryReadSkillFromTable` (`guard++ < 512`) and `PrefetchKnownSpells` (`chainGuard++ < 1024`) already do this. (Note: the finding's references to in-file "H2/H5" notes are mis-attributed, but the code behavior is as described.)
*Fix:* Add `int guard = 0; && guard++ < 4096` to each inner loop; gate every `ReadIntPtr(node)`/`ReadInt32(node)`/buffer read with `ClientObjectHooks.IsReadablePointer`; validate `bucketArray + i*4` before reading the head.

**14. IsPortaling dereferences AC's CPlayerSystem pointer off the main thread with only try/catch**
`C:/Projects/RynthCore/src/RynthCore.Engine/Compatibility/TeleportStateHooks.cs:29-44` · *crash-AV* · severity **high**
Raw double-deref (`*(IntPtr*)_playerSystemPtrAddr`, then `*(byte*)(playerSystem + 0x238)`) behind only managed catch, exposed via `IsPortalingFn` and polled by NavigationEngine/RadarWallRenderer/NavMarkerRenderer/ExpressionEngine. In Decal-coexistence mode the pump runs on a ~30 Hz worker thread; during relog/teleport teardown the singleton or sub-fields can be torn down, and an off-thread read of a stale non-null pointer AVs (the documented 0x00416C86 plugin-tick crash). The `IntPtr.Zero` check is insufficient — the danger is a non-null but freed/mid-reassignment pointer.
*Fix:* Gate to the main thread, returning a main-thread-cached value (or false) off-thread; add `IsReadablePointer(playerSystem)` before the `+0x238` deref. (Minor doc nit: `PluginContract.cs:134` mislabels the source as SmartBox while this reads CPlayerSystem+0x238.)

**15. NativeAttack issues StartAttackRequest/EndAttackRequest with no main-thread marshalling**
`C:/Projects/RynthCore/src/RynthCore.Engine/Compatibility/ClientCombatHooks.cs:163-193` · *race-threading* · severity **medium**
`NativeAttack` resolves the combat singleton and calls four AC combat-state mutators directly with only try/catch — no `MainThreadGuard` gate, unlike `MeleeAttack`/`MissileAttack`/`ChangeCombatMode`. It is reachable off-thread via the host API, and with `UseNativeAttack` defaulting true, `CombatManager.FireAttack` calls a correctly-marshalled `SelectItem` immediately followed by the un-marshalled `NativeAttack` on the same pump thread — a genuine gap in the engine's marshalling sweep.
*Fix:* Add a single new `AcMainThreadQueue` `ActionKind` (NativeAttack carrying attackHeight + power) so the whole ordered sequence executes atomically after the paired `SelectItem` — do **not** split into four entries. `if (!IsOnMainThread()) return EnqueueNativeAttack(...)`. Gate `AutoTarget` (also a mutator) the same way; validate the singleton with `IsReadablePointer`.

**16. Profile-name lists read without `_profileListsLock` from export and refresh paths**
`C:/Projects/RynthSuite/Plugins/RynthCore.Plugin.RynthAi/LegacyUi/LegacyDashboardRenderer.cs:1932-1933,2283-2309` · *race-threading* · severity **medium**
`_profiles`/`_navFiles`/`_lootFiles`/`_metaFiles` are documented as lock-guarded, but `RefreshProfilesList` reads `_profiles.Contains`/`_profiles[0]` just after releasing the lock, and `SelectProfileAtIndex` (export, poll thread) indexes all four lists with no lock while the pump thread is mid-`Clear()`/`AddRange()`. A torn read or `ArgumentOutOfRangeException` results. Downgraded to medium because `SafeInvoke` and `OnTick`'s try/catch swallow the throw — practical impact is a silently-failed profile switch, not a fail-fast.
*Fix:* Move the `Contains`/`[0]` read inside the existing lock; in `SelectProfileAtIndex`, snapshot the relevant list under `_profileListsLock` before bounds-check/index (mirroring `BuildSnapshotJson`).

---

## Medium

**17. RynthNav goto-cancel mutates tick-thread route state off-thread (NRE window in StepGoto)**
`C:/Projects/RynthSuite/Plugins/RynthCore.Plugin.RynthNav/RynthNavPlugin.cs:341-356,411-471` · *race-threading* · severity **medium** (adjusted from high)
`DoMove()` (Avalonia UI/chat thread) sets `_gotoActive=false; _route=null; _portalWait=false` with no synchronization while the tick thread runs `StepGoto`/`OnSubGoalReached`, which read `_route` non-atomically (`_route.Count`/`_route[_routeIdx]`) — a TOCTOU NRE. Violates the class's own "panel actions only set a pending request" invariant (the other actions use `_reqGate`). Conditional: `_route` is only non-null when `_portalsEnabled` (default off, opt-in) and the throw is caught by the tick try/catch (no native crash).
*Fix:* Snapshot `_route` into a local at the top of `StepGoto`/`OnSubGoalReached`, or route the cancel through `_reqGate` (`_reqCancel` flag acted on in `ProcessRequests`). Do not mutate `_gotoActive`/`_route`/`_portalWait` directly from `DoMove`.

**18. ChatCommandDispatcher can re-enter AC's WndProc off-thread via SimulateChatInput fallback**
`C:/Projects/RynthCore/src/RynthCore.Engine/Compatibility/ChatCommandDispatcher.cs:293-327` · *native-interop* · severity **medium**
When no chat-manager `this` has been captured (the first send of a session, after every logout), `DispatchRaw` falls back to `SimulateChatInput`, which calls `Win32Backend.SendToGameWndProc` (`CallWindowProcA` into AC's WndProc) directly from the ChatFileDispatcher Timer thread / OnLoginCommandRunner Task thread — driving AC's chat-bar key handling from a non-window-owning thread. Realistic symptom is chat-bar UI misalignment / lost OnLogin command ("works once then stops"); the engine already restructured the in-game Enter path to post `WM_RYNTHCORE_CHAT` for exactly this reason.
*Fix:* Route `SimulateChatInput`'s `SendToGameWndProc` calls through `Win32Backend.RunOnGameThread` (inlines if already on the game thread, else marshals via `WM_RYNTH_RUN_ACTION`). Wrap inside `SimulateChatInput` to also protect future callers; guard the no-HWND case.

**19. TimeSyncHooks 64-bit fields written on the net thread and read off-thread without atomicity (torn reads on x86)**
`C:/Projects/RynthCore/src/RynthCore.Engine/Compatibility/TimeSyncHooks.cs:37-54,91-104` · *race-threading* · severity **medium**
`_lastServerTime` (double) and `_lastWallClockTicks` (long) use plain assignment in `HandleTimeSynchDetour` (net thread) and plain reads in `GetCurrentServerTime` (pump thread). On x86 a 64-bit load/store is non-atomic, so a reader can observe a torn `_lastWallClockTicks`, feeding a wildly wrong elapsed time into enchantment remaining-time. Sibling files (`ClientHelperHooks`, `BusyCountHooks`) already use `Interlocked.Read`/`Exchange`; this one missed the pattern. Dominant failure is the long tearing at the high-half rollover (~every 430s); worst case is a mis-timed rebuff.
*Fix:* Publish both via one immutable record reference (`Volatile.Write`/`Read`) or `Interlocked.Exchange`/`Read` the long; read both into locals once in `GetCurrentServerTime`.

**20. PluginManager `_plugins` list mutated from the pump thread while RenderAll iterates on the render thread**
`C:/Projects/RynthCore/src/RynthCore.Engine/Plugins/PluginManager.cs:961-1007` · *race-threading* · severity **low** (adjusted from medium)
`RenderAll` (AC render thread) iterates `_plugins` (plain `List`) with a separate Count/indexer access *outside* the per-plugin try/catch, while `RescanPlugins` → `UnloadAllPlugins`/`LoadPluginsFromDisk` (pump thread) does `Clear()`/`Add()` with no lock — a torn list can throw `ArgumentOutOfRangeException` mid-ImGui-frame. (Correction to the finding: `TickAll` runs on the pump thread too, serialized with rescan, so it does *not* race.) Trigger is an operator-only RL/rescan button, and the failure is a caught/logged exception aborting one frame, not a crash — hence low.
*Fix:* Have `RescanPlugins` build a new array and publish it via `Volatile`/`Interlocked` swap; `RenderAll`/`TickAll` snapshot the reference once and iterate that — mirroring the existing `Nav3DRenderer` atomic double-buffer.

**21. RynthTracker session-reset clears the kill-dedup HashSet from the UI thread while the AC thread mutates it**
`C:/Projects/RynthSuite/Plugins/RynthCore.Plugin.RynthTracker/RynthTrackerPlugin.cs:75-94,105-120` · *race-threading* · severity **low** (adjusted from medium)
`RynthTrackerReset` (Avalonia STA thread, no Dispatcher hop) calls `_killedIds.Clear()` while the AC pump thread does `_killedIds.Add`/`Remove` in `OnUpdateHealth`/`OnDeleteObject`. `HashSet<uint>` is not concurrency-safe; a `Clear()` racing `Add` can throw or corrupt internal arrays (kill-count drift). The runtime try/catch prevents a crash but not corruption. Low: trigger is a manual button click in a microsecond window; only a pure-managed cosmetic-stats object is shared.
*Fix:* Set a `_pendingReset` flag in the export and perform `ResetSession()` at the top of `OnTick()` (mirrors the engine's `DispatchQueued*` idiom), or lock all `_killedIds` access.

**22. Gesture-defer early-return in Drain() also stalls the chat and appraisal queues**
`C:/Projects/RynthCore/src/RynthCore.Engine/Compatibility/AcMainThreadQueue.cs:228-323` · *logic-bug* · severity **low** (adjusted from medium)
When the action ring is non-empty and a gesture is in flight, `Drain()` returns at line 232 before `DrainChat()` (319) and `DrainRequestIds()` (322) — yet those queues are documented as deliberately separate so appraisals "can never get gesture-deferred." So chat output and 0xC8 appraisal sends are deferred whenever any action is queued behind a gesture, contradicting the stated invariant. Low: gated on a non-empty ring, fail-open, capped at 250 ticks, and queues are bounded — worst case is delayed/dropped diagnostics, never corruption.
*Fix:* Drain the chat and request-id queues (gated only on their own non-emptiness) above the gesture-defer early return; keep the action ring gesture-gated independently. Neither perturbs AC motion/UI state.

**23. ReadPlayerEnchantments trusts KnownPlayerQualitiesPtr without the vtable-in-module validation the object path performs**
`C:/Projects/RynthCore/src/RynthCore.Engine/Compatibility/EnchantmentHooks.cs:75-92,144-167` · *memory-safety* · severity **low** (adjusted from medium)
`ReadObjectEnchantments` validates `IsMemoryReadable` + `IsPointerInModule(vtable)` on both the qualities object and registry; `ReadEnchantmentsFromQualities` (off-thread player path) only checks `TeardownActive` + per-node `IsMemoryReadable` — so a committed-but-stale qualities pointer after relog is walked over arbitrary data (use-after-free risk). The parallel skill path re-validates the same pointer via `LooksLikeAcHeapObject`, making this an inconsistent omission. Low: `KnownPlayerQualitiesPtr` is zeroed on logout, `TeardownActive` covers the dominant race, and the default EndScene path runs the read and reset serially — exposure is mainly Decal-coexistence mode.
*Fix:* In `ReadEnchantmentsFromQualities`, require the qualities object's vtable match the captured `_cacQualitiesVtable` (`IsCacQualitiesObject`) and validate the registry vtable-in-module before walking. Consider gating to the main thread.

**24. TryGetItem(Int/Double)Property fast paths call `_getWeenieObject` off the main thread before the guard**
`C:/Projects/RynthCore/src/RynthCore.Engine/Compatibility/ClientObjectHooks.cs:1754-1774,1838-1857` · *race-threading* · severity **medium** (adjusted from high)
The PWD fast paths run *before* the `MainThreadGuard.IsOnMainThread()` check (at 1779/1860) and dereference a cached weenie pointer (captured on a ≤100ms-stale main-thread walk) with raw `Marshal.ReadInt32`. `IsReadablePointer` catches an unmapped page but not a freed-then-reallocated one — a TOCTOU. (Correction: the realistic outcome is a *garbage property value* fed to loot/salvage logic, not the client AV the finding cited, since the raw read is in try/catch with `IsReadablePointer` and avoids the CObjectMaint walk / Inq* helpers — hence medium.)
*Fix:* Hoist the `IsOnMainThread` check to the top of both methods (after the safe appraisal-cache dict lookup) so the PWD fast path runs only on the main thread; off-thread callers fall through to the appraisal cache. `ReadObjectPositionLive` already shows the extra vtable/`LooksLikeAcHeapObject` guard these paths omit.

**25. Raw-packet opcode tracker allocates a byte[] per blob on the recvfrom reverse-P/Invoke path, unconditionally**
`C:/Projects/RynthCore/src/RynthCore.Engine/Compatibility/RawOpcodeTracker.cs:34-62` · *memory-leak* · severity **medium**
`Track` allocates `new byte[sampleLen]` for every non-zero-opcode blob of every inbound packet, called from `RecvFromDetour` (a reverse-P/Invoke detour) with no gate on whether the opcode-tracker UI is even open (only `Frozen`). Sustained per-packet managed allocation on a reverse-P/Invoke transition is the documented fail-fast hazard (`RhpReversePInvokeAttachOrTrapThread2`); on a busy multibox it is constant GC churn for a diagnostic feature. (Dictionary growth is bounded/one-time; the durable risk is the per-blob `byte[]`.)
*Fix:* Add a `volatile bool Active` set true only while the Packet Sniffer panel is visible, and early-return from `Track` (and ideally `RawPacketParser.Parse`/the call site) when false. Reuse a single grow-only per-entry buffer instead of a fresh `byte[]`.

**26. List higher-order functions clobber session variables $0/$1/$2 without restoring them**
`C:/Projects/RynthSuite/Plugins/RynthCore.Plugin.RynthAi/Meta/ExpressionEngine.cs:1185-1248` · *correctness* · severity **low** (adjusted from medium)
`listfilter`/`listmap`/`listreduce`/`listsort` write iteration state into the persistent `_variables` dictionary under keys "0"/"1"/"2" but never restore prior values (contradicting the section comment). Nested list ops clobber each other's `$0`/`$1`, and the user's own `$0/$1/$2` are silently changed; `listsort` leaves `$1/$2` set to the last-compared pair. (Correction: the claimed overlap with ChatMessageCapture `{0}/{1}` is false — those are a separate Match-based namespace.) Low: triggering needs numeric var names or nested ops, both uncommon.
*Fix:* `TryGetValue`-snapshot "0"/"1"/"2" before the loop and restore-or-`Remove` in a `finally` in each of the four functions (or evaluate templates in a child scope).

---

## Low

**27. RecursionGuard.Tick is a permanent no-op — the recursion diagnostic is silently disabled engine-wide**
`C:/Projects/RynthCore/src/RynthCore.Engine/Compatibility/RecursionGuard.cs:47-54` · *robustness* · severity **low** (adjusted from medium)
An unconditional `return;` at the top of `Tick()` (with a "TEMPORARILY DISABLED" comment) makes the entire body unreachable, so all ~24 detour call sites get zero recursion detection. The sibling `ThreadStackSampler.SampleAll()` is also dead (no caller), so neither recursion diagnostic is active. Low: this only ever *logged* on deep recursion — it never prevented it — so disabling it removes observability, not protection. The project already triages this as cosmetic/P3.
*Fix:* Either delete the dead scaffolding + its 24 call sites, or re-enable with an allocation-free `[ThreadStatic]` depth counter (increment/decrement via try/finally) that preserves the no-`Environment.StackTrace`-allocation intent.

**28. Character-list parser reads name bytes without bounding offset against blobSize (buffer overread)**
`C:/Projects/RynthCore/src/RynthCore.Engine/Compatibility/CharacterCaptureHooks.cs:339-374` · *crash-AV* · severity **low** (adjusted from high)
The loop advances `offset` (GUID, nameLen, `Marshal.Copy`, padding, timeout) and never compares `offset`/`offset+nameLen` against `blobSize`; only `nameLen` is range-checked (1..127). Low: `characterCount` is gated 0..20 so the overread is bounded to ~2.8KB into a committed heap buffer (garbage, rarely an AV), the input is the user's *own trusted login server* (not adversarial), and both call paths are effectively dead on the live client (InnerDispatcherHook disabled; SmartBox doesn't carry the pre-login 0xF658 packet).
*Fix:* Before each read verify `offset+4<=blobSize` (GUID), `offset+2<=blobSize` (nameLen), `offset+nameLen<=blobSize` (name bytes); break on overrun; cap `offset` to `blobSize`. Defensive hardening, no behavior change expected today.

**29. TryGetAccountName/WorldName validate only one page, then do a len-driven copy that can cross a page boundary**
`C:/Projects/RynthCore/src/RynthCore.Engine/Compatibility/AccountHooks.cs:190-205` · *crash-AV* · severity **low** (adjusted from medium)
`IsReadable(bufferPtr+20)` checks only the start page, then `PtrToStringAnsi(bufferPtr+20, len-1)` copies `len-1` bytes (no NUL stop) where `len` is only floor-checked — a corrupt/torn `len` reads past the committed region. Low: the read is gated behind a fully validated live-object chain (`len` comes from AC's own heap, not the wire), it caches after first success, and it is an established idiom shared with `ClientObjectHooks`/`AppraisalHooks`.
*Fix:* Use a length-aware readability check (`CrashLogger.IsReadable(addr, bytes)` already does `endRequested<=endRegion`) and clamp `len` (reject `>256`, and/or check against the capacity field at `bufferPtr+12`) before `PtrToStringAnsi`. Apply to both sibling readers.

**30. Multi-match disambiguation only inspects the SECOND occurrence; 3+ matches can mis-resolve a hook target**
`C:/Projects/RynthCore/src/RynthCore.Engine/Compatibility/HookResolver.cs:57-89` · *native-interop* · severity **low** (adjusted from high)
On a multi-match, `Resolve()` finds only the first and the immediately-next match and picks the nearer to the fallback VA; 3rd+ occurrences are never considered, and the log claims `pattern-multimatch` having examined only 2 of N. Low: the pattern generator only emits unique patterns (this branch is a defensive fallback for future binary drift), and the `distance==0` short-circuit already returns the correct site in the realistic "VA is the first match" case. Not reachable on the current shipped binary (all 120 patterns verified unique).
*Fix:* Enumerate all matches (loop `FindPatternInRegion` past each hit) and pick the global-nearest to the fallback VA (keeping the exact-VA short-circuit); log the true match count; optionally add the prologue sanity check the fallback path has.

**31. RaycastEngine arc Z reconstruction double-applies vertical offset, mis-sampling parabolic LOS**
`C:/Projects/RynthSuite/Plugins/RynthCore.Plugin.RynthAi/Raycasting/RaycastEngine.cs:178-208` · *correctness* · severity **low**
The launch angle is the flat-range solution (assumes equal start/end height), and the origin/target Z difference is then bolted on via an ad-hoc `verticalDist*t*(1-t)` blend. The result is neither the true ballistic curve nor a straight line: at `t=1` the last arc point sits at `origin.Z`, not `target.Z`, biasing the LOS verdict for sloped shots (false blocks/misses). Not a crash; the final explicit segment patches only the endpoint. Only active when `UseArcs=true`.
*Fix:* `Z = lerp(origin.Z, target.Z, t) + (vVertical*time - 0.5*g*time^2 - vVertical*totalTime*t)` — anchors the endpoint at `target.Z` while keeping a true parabolic sag. Apply the same fix to the mirrored copy in `RynthAiCommands.cs:428-429`.

**32. VirtualQuery readability check uses 32-bit int arithmetic for region-end/request-end (overflow can approve an unreadable range)**
`C:/Projects/RynthCore/src/RynthCore.Engine/Compatibility/SmartBoxLocator.cs:45-67` · *crash-AV* · severity **low** (adjusted from medium)
`regionEnd = BaseAddress.ToInt32() + RegionSize.ToInt32()` (signed 32-bit) can wrap; acclient.exe is LARGE_ADDRESS_AWARE so addresses above 0x7FFFFFFF are reachable. Low: modeling shows essentially all high-address cases produce fail-*closed* false negatives (safe); a true AV-causing false positive exists only in a razor-thin ~84-byte window straddling exactly 0x80000000 with unmapped memory above. The "size overflow" concern is moot — every caller passes a small compile-time constant.
*Fix:* Do the arithmetic in `long`/`ToInt64()` and compare as `long`. Skip the size-overflow guard.

**33. AppraisalHooks per-guid caches grow unbounded for the whole session (memory/VA leak across grinds and relogs)**
`C:/Projects/RynthCore/src/RynthCore.Engine/Compatibility/AppraisalHooks.cs:56-68,184-241,501-511` · *memory-leak* · severity **low** (adjusted from medium)
The eight session-scoped collections (`_appraisedGuids`, `_lastIdTime`, `_intCache`, `_boolCache`, `_stringCache`, `_spellIdCache`, `_failedRollLogged`) are populated per appraised guid and never cleared or bounded — no `ClearSession()`, and `AppraisalHooks` is conspicuously absent from `DispatchPendingLogout`'s reset pipeline. Guids don't survive a session, so a daily multi-boxer accumulates entries indefinitely (and across relogs). The peer `ObjectQualityCache` has both `MaxEntries=4096` and a logout `ClearSession()`. Low: entries are small and growth is slow, but matters given this stack's documented 32-bit VA-exhaustion sensitivity. (The finding's `AutoIdService._sent` sub-claim is unverifiable — that path didn't resolve.)
*Fix:* Add `ClearSession()` (lock `_cacheLock`, clear all eight collections incl. `_failedRollLogged`) and call it from `DispatchPendingLogout` alongside the other `ResetSession()` calls; optionally add a `MaxEntries` wholesale-clear cap.

---

## Suggested fix order (highest leverage first)

1. **FellowshipTracker → host snapshot primitive** (#1) — sole subsystem doing off-thread raw VA reads; uncatchable client kill, also fixes the H5 pattern-relocation break.
2. **Marshal NativeAttack via a single AcMainThreadQueue ActionKind** (#15) — default-config combat path mutates AC state off-thread every fight; small, high-frequency, closes the last marshalling-sweep gap.
3. **Add chain-length guards + IsReadablePointer to AppraisalHooks hash-table walks** (#13) — runs inline on AC's main thread; a corrupt chain hangs or AVs the client.
4. **Gate IsPortaling and ReadCurrentCombatMode to the main thread (cached value off-thread)** (#14, #3) — two near-identical off-thread teardown-AV reads polled constantly in coexistence mode; both fixes are one-liners against existing caches.
5. **Lock or single-thread the raycast geometry + TerrainSampler caches** (#5, #6) — two unsynchronized-Dictionary races that corrupt the AC main thread; reuse RynthNav's `_reqGate`.
6. **Detect WaitForSingleObject timeout / reject STILL_ACTIVE in the injector** (#7) — trivial guard preventing a remote thread at a garbage address inside the live client.
7. **Marshal the OnLogin/chat dispatch path onto AcMainThreadQueue + RunOnGameThread** (#4, #18) — off-thread AC chat-heap alloc/free and WndProc re-entry; fix once in the dispatcher to cover all three callers.
8. **Restore nav on jump-hook failure** (#9) — one-line `RestoreNav()` at the unified `!started` site that ends the session-long "bot stands there" hang.
9. **Replace System.Diagnostics.Process with Win32 P/Invoke in LegacyDashboardRenderer** (#10) — documented H6 host-crash API; copy the engine's existing `IsPidAliveWin32` idiom.
10. **Fix the EndScene rehook use-after-free** (#8) — capture the old original, RemoveHook only after a successful CreateHook; rare trigger but a fatal UAF in the render path.
