# SK engine and UI work through 2026-10-10, rebased on 2026.10.12.1

Replaces the closed per-feature PRs RynthCore #1-#5 and the earlier `sk` commits. This commit
carries everything from those PRs that main doesn't already have, merged onto release 2026.10.12.1
with the conflicts resolved. Work from 2026-10-11 follows in separate commits.

Nothing here changes what 2026.10.12.1 already does unless a setting below says so.

## Merge notes for 2026.10.12.1

- **API v81:** 2026.10.12.1 took v80 for `GetContainerOrder` and `PutItemInContainer`, so
  `GetMergeStackResult` moves to v81, after them (engine table, SDK table,
  `RynthCoreSdkApiVersion`, `HasGetMergeStackResult`, and the manifest test's slot order).
  Plugins built against the earlier SK SDK must be rebuilt.
- **Chat routing:** `ChatRouter` now lives in `UI/Data/ChatRouting.cs`. The standard-filter step
  (`RynthChatPresets`) is added there instead of in `ChatData.cs`.
- **Settings:** `LoggingLevel` and `EnablePluginOverlayWindows` sit next to the new
  `CloseOnServerDisconnect`; the Items payload keeps both `missingIds` and `autoEquipShield`.

## Launcher and logging

- **Logging level** in the launcher (Error, Warning, Info, Debug, Trace), saved as `LoggingLevel`
  in `engine.json`. The engine reads it at start and watches it while running
  (`LogSettings`, `LoggingSettingsStore`, `MainWindow.Logging`). Per-category trace switches are
  shown with a warning when a category is hidden by the level.
- The heartbeat line is always written, whatever the level, because the launcher uses it to tell
  a client is alive (`RynthLog.Heartbeat`).
- `RynthCore.DesktopLog` (new project): rolling launcher log and the .NET desktop x64 prerequisite
  check, with host diagnostics in the launcher (`LauncherHostDiagnostics`).
- Install-aware paths: the launcher, injector, loader and status agent find the install folder the
  installer recorded (`InstallerHandoff`, `LogPaths`) instead of assuming `C:\Games\RynthCore`.

## Plugin windows and ImGui

- **Plugin overlay windows** while the ImGui shell is off: a plugin that exports
  `RynthPluginRenderOverlay` draws its extra ImGui windows over the Avalonia UI.
  `engine.json` `"EnablePluginOverlayWindows": false` turns this off. Plugins get the engine's
  ImGui context for these windows.
- ImGui draw data is submitted after the Avalonia blit, so ImGui windows stay on top.
- Panel windows are destroyed on the thread that owns them, and a login pop-out waits until the
  game window is hooked.
- Docked panels open clear of popped-out windows (`LayeredWindow`).
- `CS0162` (unreachable code) is an error in the engine project: a misplaced statement after a
  `return` once left the plugin render snapshot unpublished.

## Items, drag and drop, object data

- **Item drag bridge** (`ItemDragBridge`, `DragDropHooks`): items dragged from the game or from a
  popped-out Inventory can be dropped on Mini Remote slots. Game-inventory drags carry the dragged
  item, not the previous selection.
- Object name and type are seeded when the object is created, so panels show them before the
  first update (`CreateObjectHooks`, `DeleteObjectHooks`).
- Off-thread int property reads are snapshotted from the PWD and kept current with server
  updates (`ClientObjectHooks`); IconOverlay and IconUnderlay DIDs are served from the PWD.
- **Merge-stack results** (API v81, `GetMergeStackResult`, after rynth's v80 slots): plugins can see whether a stack merge
  went through (`MergeStackResults`). The thunk is cdecl; stdcall corrupted plugin stacks.

## Native crash logging

- `SehTrampoline.c`: the native crash logger records only fatal faults by default, and logs
  access violations in the CoreCLR-hosted engine immediately rather than at exit.

## Panels

- **Nav:** route editing in the Nav panel, Breadcrumbs and Route overlay toggles,
  and the waypoint list no longer jumps back to the top on every poll.
- **Skills > Progression tab:** XP and augmentation planning laid out like UB's ILT Tools XP and
  Augs tabs, an attribute raiser, and per-server augmentation cost settings
  (`ProgressionData`). The Char launcher has a menu that opens it.
- **Chat filters:** UB-IT-style standard filters, plain-text custom rule modes, a line tester, and
  presets (`RynthChatPresets`).
- **RynthAi dashboard:** Char and Hub launcher buttons, Map and Lua buttons, a Mini Remote drawer,
  a Buffs picker in Loaded Files and a Spells window button.
- **Items panel (Avalonia):** Shields section and Add Selected Shield.
- **Inventory:** a menu item that adds an item to RynthAi's item count HUD.
- **Settings:** Charms Tracking tab, an Inventory HUDs button, the Server Features text row, and
  one "Describe Items When Selected" toggle (the duplicate "Item Info On Select" row is removed).
- **Loot:** "Include T11 attributes" in the Add to loot profile popup, with a tooltip that covers
  the stamped tier and gear grade.

## Build

- `RynthCore.sln` includes `RynthCore.DesktopLog`.
- One `Tmds.DBus.Protocol` 0.21.3 pin in the engine project (GHSA-xrw6-gwf8-vvr9).

## Testing

- Engine (CoreCLR host) and launcher build in Release.
- `tools/PluginManifestTests` pass.
- Each feature was run in game on the SK builds of 2026-10-04 to 2026-10-10.
