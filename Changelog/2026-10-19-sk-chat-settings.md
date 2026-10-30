# Silentkelpie PR 6, commit 7: engine settings, chat, Modern vault, launcher picker

Branch `feature/sk2-chat`, from `integration/pr-fixes-2026-10-18`. Not deployed, not released.

From SilentKelpie's RynthCore PR 6 (commit 7, "port SK test branch (2026.10.18.24) onto sk"). That commit
is a squash against his base, so it was taken piece by piece and adapted; none of its hunks that roll back
our newer code were taken (plugin API comments, display chrome, DatFileShareHooks, InventoryTitleLayout,
bar restructure, Skills embed, updater floor, installer / launcher DXVK).

## What

- **engine.json saves only the keys this client changed.** Every client kept the whole settings in memory
  and wrote them all back, so a client that never touched a setting put its stale value over another
  client's change (a Display option set in one client came back in all of them). `Save(key)` records the
  key and `EngineSettingsFile.Merge` takes only those from the engine; every other key keeps what the file
  holds. A changed key back at its default is still left out (removed); a missing file still gets the whole
  settings. Redone against our key set (TransitionGate, HoldReloadInWorld(+MaxMinutes),
  RememberAcWindowPosition, AutoIdMode, ...); his BorderlessFillMonitor key is not here.
- **Map coordinates in chat are underlined links** (hover: hand and tooltip). Click points RynthNav's arrow
  there, Ctrl+click walks there; a drag still selects and copies, right-click keeps the line menu.
- **Lines a chat filter hides stay out of AC's own chat window too.** The engine runs RynthChat's Hide rules
  and ticked standard filters on a held line before its verdict (`ChatModel.ShouldHideInRetail`,
  `ChatRetailVerdict`); a moved or copied line still prints in AC. Option under Standard filters, on by
  default (`hideInRetail`). The plugin pump's incoming chat queue holds 4096 lines (was 256) and logs drops.
- **RynthChat re-reads `rynthchat_settings.json` when another client saved it** (`SharedFileWatch`, every
  2 s): rules, standard filters, tabs and display options follow, this client's own saves are skipped, its
  selected tab is kept.
- **Modern Inventory has the Aelrynth Vault**: a chest button beside the Classic button, the vault in place
  of the grid, caption, Refresh, right-click Withdraw. The header positions are now `InventoryHeaderLayout`;
  the Vault takes its room first and is never dropped.
- **Launcher**: a Known server picker in the Server Profile dialog (17 servers) and the Launcher tab scroll
  fix (the profile cards no longer push Updates and Launch Targets out of reach).

## Left out

- The low-memory safeguard (turn Adaptive degrade on after an early address-space shortage): it is a part
  of his graphics profile code (AcGraphics, AcGraphicsPrefs, graphics.json, the launcher writing the
  profile into UserPreferences.ini), which is not taken.
- In his chat/PluginManager hunks: the EngineStatusMetrics call, the MemoryUse.Mark removal, "v85" ->
  "v81" comment edits and the ServerPlugins wording (rollbacks).
- His Classic title-bar "more" menu, and the DXVK toggle in the launcher.

## Files

- `src/RynthCore.Engine/Plugins/EngineSettings.cs`, `EngineSettingsFile.cs`
- `src/RynthCore.Engine/ImGui/Panels/ChatFace.cs`, `ChatFiltersFace.cs`, `InventoryFace.cs`, `InventoryFace.Classic.cs`
- `src/RynthCore.Engine/UI/Data/ChatCoords.cs`, `ChatData.cs`, `ChatSettingsDto.cs`, `ChatRetailVerdict.cs` (new), `SharedFileWatch.cs` (new)
- `src/RynthCore.Engine/UI/InventoryTitleLayout.cs`, `Plugins/PluginManager.cs`
- `src/RynthCore.App/KnownServerCatalog.cs` (new), `src/RynthCore.App.Avalonia/` (ServerProfileDialog, MainWindow, csproj)
- Tests: `tools/EngineSettingsTests`, `ChatCoordTests`, `ChatFilterTests`, `BarDeclutterTests`, `LauncherSettingsTests`

## Not verified

No live client: the underlined links and clicks, the retail-chat hide and the 4096 queue under a real burst,
the settings re-read between two running clients, the Modern vault button and grid (needs an Aelrynth
character), the launcher dialog and scroll.
