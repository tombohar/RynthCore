# RC / RynthAi UI + Avalonia Deep Dive — 2026-07-02

**Method:** 12-agent subsystem survey (engine overlay, all 16 panels, D3D9 compositor, plugin dataflow, input routing, launcher, Dcomp, cross-cutting hygiene) + completeness critic + adversarial verification. 111 raw findings → 89 after dedup; 13 verified line-by-line against the working tree before session limits cut the verify tail (all 13 **CONFIRMED**, 0 refuted — survey accuracy is high). Full per-finding evidence: [UI_DeepDive_2026-07-02_findings.md](UI_DeepDive_2026-07-02_findings.md).

**Config context (daily driver):** `EnableDcompOverlay:false`, `EnableImGuiBackend:false` ⇒ docked = custom Skia producer → D3D9 EndScene texture; undocked = GDI `UpdateLayeredWindow` software path.

---

## TL;DR — the ten biggest levers

| # | What | Where | Why it matters |
|---|------|-------|----------------|
| 1 | **AB-BA deadlock: game-thread `OnInput` blocking Invoke × UI-thread `SendMessage` RunOnGameThread** | AvaloniaOverlay.cs:3968 / Win32Backend.cs:267 | Plausible cause of intermittent whole-client hard freezes; also stalls the game thread on every hover mousemove over any popout. Fix = roadmap #3 (Invoke→Post) + SendMessageTimeout. Small. |
| 2 | **Unbounded ImGui input queue in the daily config** | Win32Backend.cs:811 | Every mouse/key/focus msg enqueued all session, never drained when `EnableImGuiBackend:false`. Tens–hundreds of MB on 32-bit VA. Small fix (gate + cap). |
| 3 | **RynthAi 33ms fat-snapshot pipeline** | LegacyDashboardRenderer.cs:2381 / RynthAiPanel.cs:779 | 10–30KB JSON built+marshaled+parsed 30×/s per client; 80% of payload discarded. Split lean/fat + version gate. |
| 4 | **Segmented HP bar allocates a new brush per segment per 33ms tick** | RynthAiPanel.cs:1064 | The one unconditional invalidator: keeps the ENTIRE docked compositor re-rastering + 3×~5.5MB copies at 30Hz even when idle. Tiny fix (15 cached brushes + lit-count gate). |
| 5 | **No dirty-rect in docked composite: 3 full-viewport CPU copies/frame** ✅CONFIRMED | OverlaySkiaPlatformGraphics.cs:242 | ~1GB/s memcpy with one small animating panel. Two-buffer swap (also kills tearing) + dirty-rect subrect upload. |
| 6 | **Dirty-gate fix #1 IS in source but keys off window-global LayoutUpdated** ✅CONFIRMED | AvaloniaOverlay.cs:3910 | Any docked panel churn re-dirties every popout during botting → near-60fps popout rendering returns. Scope to subtree bounds + MarkDirty. |
| 7 | **alwaysRender popouts (Radar/RynthAi) raster at 60Hz for 30Hz data** ✅CONFIRMED | AvaloniaOverlay.cs:4157/2140 | Wire the panels' own 33ms timers to `MarkDirty()` (exists, zero callers) — halves popout cost, honors invariant 5. |
| 8 | **BringPanelToFront detaches/re-adds the whole panel subtree on EVERY docked click** ✅CONFIRMED | AvaloniaOverlay.cs:3349 | Engine-side click lag stacking on MetaPanel rebuilds; also drops TextBox focus. Fix = ZIndex counter, no reorder. |
| 9 | **SettingsPanel 5s poll rebuilds the visible tab unconditionally (Dirty guard is dead code)** | SettingsPanel.cs:399 | Clobbers in-progress edits, leaks keystrokes to the game, reflashes. Must ship with conditional-row rebuild fix (SettingsPanel.cs:501). |
| 10 | **WM_NCHITTEST DIB alpha peek races EnsureDib DeleteObject — UAF on AC's main thread** ✅CONFIRMED | LayeredWindow.cs:446 | NativeAOT fail-fast class. Retire-not-free the old DIB. Small. |

**Strategic call (Dcomp):** do **NOT** fund Dcomp as the perf fix. The GPU compositing premise is empirically dead (source comment records AC dropping to ~8fps under DirectComposition contention; harness is pinned to software RedirectionSurface). Its remaining win is presentation-only and modest now that the dirty gate exists. Keep it as a future windowing/UX migration (per-client OS windows, multibox visibility); its blockers are catalogued in the findings doc (keyboard input structurally absent, hot-reload-fatal, no persistence, panel parity gaps).

---

## Status of the 2026-06-29 meta-panel roadmap (verified against working tree)

| Fix | Status |
|-----|--------|
| #1 dirty-gate floating Tick | ✅ **IN SOURCE, correctly shaped** (per-host `_dirty`, 200ms heartbeat, alwaysRender exemption). Still not runtime-verified. Two weaknesses found: global LayoutUpdated re-dirtying (TL;DR #6) and hover-mousemove re-dirtying at 60Hz + rendering into hidden windows (AvaloniaOverlay.cs:4185/4018). |
| #2 in-place collapse/expand | ❌ **MISSING** — MetaPanel.cs:452/515 still full-rebuild ~420 controls per click. ✅CONFIRMED |
| #3 OnInput Invoke→Post | ❌ **MISSING** — and now upgraded to P0 (deadlock, TL;DR #1). |
| #4 optimistic action buttons | ❌ **MISSING** — plus a real destructive bug: stale indexes for up to 2s mean double-delete can delete the WRONG rule (MetaPanel.cs:497). |
| #5 PayloadSig split | ◐ **PARTIAL** — LastFiredMs excluded (per-tick flash killed) but CurrentState/CurrentMetaPath still in sig (MetaPanel.cs:189): every bot state transition still forces an autonomous rebuild/flash. ✅CONFIRMED |
| #6 hoist per-row allocs | ❌ **MISSING** — per-rebuild Cursor/ColumnDefinitions/brush allocs remain (MetaPanel.cs:429/475/576). |

Also confirmed in source (previously "pending verify" in memory): the OverlayTextureRenderer cross-thread `_texture` race is **fixed** (all texture lifecycle on the AC render thread now); the RynthChatPanel lazy-brush dispatcher-poison fix; the undock-input-poisoning (stale PointerCapturingHost) fix; PanelStateStore writes are drag-end debounced (fine).

---

## P0 — Correctness & crash-risk (do these first)

1. **Deadlock + input stall (TL;DR #1).** Convert `OnInput`, `OnRedockClicked`, `OnCloseClicked` to `Dispatcher.UIThread.Post` (AvaloniaOverlay.cs:3967–4002). Leave `OnMoved`/`OnResized` synchronous (invariant 2). Belt-and-braces: `RunOnGameThread` SendMessage → `SendMessageTimeout(~2s)` with logged fallback. Also make `LayeredWindow.Dispose` post its DestroyWindow rather than SendMessage-block.
2. **Unbounded `_pendingInput` queue (TL;DR #2).** Guard `EnqueueInput` on the ImGui backend actually running + cap at ~4096 entries (Win32Backend.cs:811).
3. **DIB UAF on hit-test (TL;DR #10).** Retire the previous DIB generation instead of `DeleteObject` at EnsureDib growth (LayeredWindow.cs:446/741/908).
4. **ItemsPanel silent edit loss.** `state.Data = fresh` every poll orphans row closures → element/type edits are shown in UI but never reach the plugin, then silently revert (ItemsPanel.cs:320). Move the swap inside the changed-branch.
5. **SettingsPanel poll clobber (TL;DR #9).** JSON string-gate the 5s rebuild + focus guard; ship together with rebuild-on-gating-toggle so conditional rows still appear (SettingsPanel.cs:399/501).
6. **MetaPanel Source-mode rebuild destroys the AvaloniaEdit editor mid-typing** ✅CONFIRMED (MetaPanel.cs:331). Gate the poll rebuild to List mode only. One-line-class change.
7. **Docked typing broken in MonstersPanel (5 TextBoxes) and MetaPanel (3)** — missing `AvaloniaTextInputActive` wiring; keystrokes leak to the game (MonstersPanel.cs:578…, MetaPanel.cs:718…). Best fix: one window-level GotFocus/LostFocus handler in RynthOverlayWindow sets the flag for any TextBox/TextArea — kills the whole "hand-wired per-panel dispatch" bug class.
8. **Stale-index delete misfire** (MetaPanel roadmap #4, MetaPanel.cs:497): optimistic local mutation + 250ms reconcile poll.
9. **UI-thread plugin exports mutate live bot state** (PluginExports.cs:204: SelectProfile/SetSubsystemEnabled/ToggleMacro/SendNavCommand/dunPatrol race the pump thread; dunPatrol even does raw AC pose reads off-thread). Extend the proven `_metaCmdQueue` ConcurrentQueue pattern to all of them.
10. **Latent dispatcher-poison class: 141 eager Avalonia brush statics across 12 UI classes** (census in findings doc). Mechanical conversion to the RynthChatPanel lazy pattern, or one `OverlayTheme` class initialized post-`AvaloniaOverlay.Start` + debug assert. Prevents a repeat of the 2026-06-30 total-UI-loss bug.
11. Smaller: MetaPanel `BuildSubRuleSection` checks `sub.Condition` but indexes with `sub.Action` (MetaPanel.cs:1033, IndexOutOfRange → dispatcher death); Dcomp RL button is hot-reload-fatal if that mode is ever enabled (DcompOverlayWindow.cs:139); `Process.Start` in LaunchMonsterEditor violates the NativeAOT invariant (LegacyDashboardRenderer.cs:1030, reachable only if ImGui shell re-enabled).

## P1 — Performance (ordered by expected win)

**A. Make the compositor idle when the UI is idle** — the single theme with the biggest payoff. Today three independent things keep the docked pipeline hot at 30–60Hz forever: the HP-bar brush churn (TL;DR #4), Radar's unconditional 33ms deserialize+InvalidateVisual even when the fetch failed or nothing moved (RadarPanel.cs:625), and the RynthAi fat-snapshot parse (TL;DR #3). Fix those three and an idle docked client's UI cost drops to ~zero; then the 3-copy/dirty-rect work (TL;DR #5) shrinks the cost of frames that DO render.

**B. Undocked path.** Scope the dirty gate to per-panel subtree (TL;DR #6); wire MarkDirty from the Radar/RynthAi timers and drop alwaysRender (TL;DR #7); throttle hover-mousemove dirt to ~15Hz and skip Tick when `!IsWindowVisible` (AvaloniaOverlay.cs:4185/4018); merge the four click-path visual walks into one and demote per-click file logging (AvaloniaOverlay.cs:4267; log sink itself is open-write-close per line under a global lock — EntryPoint.cs:1290 — give it a persistent buffered stream).

**C. Panel rebuild churn.** NavPanel rebuilds everything every 1s with scroll reset (NavPanel.cs:504 — worst panel; adopt the MonsterDamagePanel idiom: JSON gate + widget cache + in-place updates). MonstersPanel full-rebuilds ~27-controls-per-row on every captured-stats tick during combat and destroys focused TextBoxes (MonstersPanel.cs:442). MetaPanel roadmap #2/#5/#6 completion + skip Deserialize when raw JSON unchanged (MetaPanel.cs:1298) + drop per-keystroke `Document.Text` materialization (MetaSourceEditor.cs:127). MonsterDamagePanel: prune widget cache against data keys not filtered keys (MonsterDamagePanel.cs:398) + plugin-side change-counter export to skip 3 JSON marshals per 500ms.
   Big-meta safety valve: auto-collapse all non-current states when Rules.Count > ~250 (MetaPanel.cs:300) — >90% fewer realized controls for VTank-scale metas without an ItemsControl refactor.

**D. Hot-loop allocations.** RadarSurface.Render allocates ~25+ objects/frame incl. 10 FormattedText (8 are constant compass cardinals) — hoist/cache (RadarPanel.cs:755/883). Chat `MakeTextBlock` allocates FontFamily+brush per line (RynthChatPanel.cs:967). RefreshHitTestSnapshot: coalesce to once per tick + reuse collections (AvaloniaOverlay.cs:3683 ✅CONFIRMED, also fix the producer-stall 60Hz invalidate loop — `_captureDirty` never cleared by RenderCustomProducerFrame). Grow-only + dispose fixes: docked fallback RTT leak (AvaloniaOverlay.cs:3478), SKSurface/texture 1px recreate thrash (OverlaySkiaPlatformGraphics.cs:177, OverlayTextureRenderer.cs:265 — grow-only high-water + subrect), SKSurface draw-phase UAF window (refcount or grow-only kills it).
   **Sleeper win:** the FPS governor busy-spins `Sleep(0)` on the focused client (EndSceneHook.cs:361) — can burn ~80% of a core doing nothing. Sleep-in-1ms-chunks + short spin tail. Cheap, big for multibox CPU.

**E. Debounce the disk.** Per-slider-sample synchronous settings writes: RadarPanel Persist (1185), RynthVisionPanel push-to-plugin-which-persists + 2 log lines per sample (215), RynthTrackerPanel (89), chat filters editor per-keystroke save + full scrollback rebuild (RynthChatFiltersPanel.cs:109), chat search per-keystroke rebuild (RynthChatPanel.cs:713), RynthAi click-path SaveSettings on the UI thread (LegacyDashboardRenderer.cs:2541 — TickAutoSave already exists, just delete the inline calls), vital cache rewriting a file 1/s forever (RynthAiPanel.cs:846), chat log AutoFlush per line (1213).

**F. Launcher.** Background the 2s probe phase (process snapshot + per-client `Process.Modules` + window pings + JSON reads all on the UI thread — MainWindow.axaml.cs:1846) and cache monotonic per-PID facts; mtime-cache character detection (3283); start MemoryTabView timers on tab visibility (MemoryTabView.axaml.cs:102); cache sparkline geometry + decimate (MemorySparkline.cs:67); defer first probe past first paint (137); in-place session list updates (1911); hoist status brushes (3193); fix the UDP probe's unobserved task (ServerStatusProbeService.cs:68).

## P2 — UX polish

LogPanel Clear permanently blanks once the 256-line ring fills (LogPanel.cs:75). SettingsPanel conditional rows appear only on the next 5s poll (501, ships with P0-5). Radar wheel-after-Ctrl-release goes to the radar not the camera (Win32Backend.cs:1134). RynthNav d-pad tooltips trigger the documented whole-overlay flash pitfall (RynthNavPanel.cs:306). Undocked pointer dispatch table lacks wheel-scroll/ComboBox/ListBox handling. MetaPanel firing-row highlights go stale within a state (fix with #5 in-place updates). Launcher dead code cleanup (MainWindow.axaml.cs:3501).

## Suggested sequencing (one-per-test, per working style)

1. **Batch 1 (tiny, huge):** P0-1 Invoke→Post + P0-2 input-queue cap + TL;DR #4 HP-bar brushes + Radar JSON gate. Test: idle docked client → compositor goes quiet; hover popout → no game stutter; long session → flat memory.
2. **Batch 2:** MarkDirty wiring for popouts + dirty-gate subtree scoping + hover throttle. Test: undocked radar/RynthAi still animate; idle popouts ~5fps.
3. **Batch 3:** MetaPanel #2/#4/#5 completion + Source-mode gate + BringPanelToFront ZIndex. Test: meta clicks snappy, no botting flash, no stale-delete.
4. **Batch 4:** SettingsPanel poll gate + conditional-row rebuild + TextInput centralization + ItemsPanel edit-loss. Test: typing everywhere docked.
5. **Batch 5:** NavPanel/MonstersPanel rebuild rework; then compositor dirty-rect/two-buffer; then launcher; then P2s.

## Not covered (next dive candidates, from the critic)

D3D9 in-scene renderers (Nav3DRenderer/Injector, GameMatrixCapture, BitmapFont, VitalHud); EngineFrameController per-frame orchestration; plugin-side per-frame renderers (NavMarkerRenderer, RadarWallRenderer, TerrainPassabilityOverlay, RynthJuice, RynthVision overlays); dormant-ImGui residue beyond Win32Backend; launcher dialogs; DPI handling (none exists anywhere in the engine UI).
