# Fix: a popped-out Mini Remote or Guardian crashed the client

Follows the menu changes (`2026-10-11-character-and-server-menus.md`). Pairs with the RynthSuite
change that draws `:popout` surfaces in the current context.

## The crash

Four crashes on 2026-10-11 had the same stack:

```
EndSceneDetour -> EngineFrameController.OnEndScene -> ImGuiPopOuts.RenderAll
  -> ImGuiPanelHost.DrawPopped -> MiniRemoteFace.Draw -> DrawRynthAi
  -> PluginManager.RenderEmbed -> (RynthAi) -> access violation in coreclr
```

- `ImGuiPopOuts` gives every popped-out panel its own ImGui context.
- `PluginManager.RenderEmbed` called RynthAi from inside it. RynthAi then switched
  to the main context, which isn't mid-frame there, and faulted.
- An access violation isn't an exception `RenderEmbed`'s try/catch can stop, so the client died.
- The "MAIN THREAD HANG DETECTED" line before each exit was the runtime writing the 700 MB crash
  dump.

## Fix

- `PluginManager.RenderEmbed` checks the current ImGui context. When it isn't the one plugins
  were given (a pop-out), the surface goes out as `name:popout`.
  - RynthAi with the matching RynthSuite change draws it in the current context.
  - Older plugins don't know the name and decline. The Mini Remote then shows the engine's own
    slot grid, and the Guardian panel its "not ready" state, instead of crashing.
- This covers every embedded surface: the Mini Remote window, the RynthAi dashboard's Mini Remote
  drawer, and the Guardian panel.
- It also returns "not ready" when no ImGui context is current.

## Files

- `src/RynthCore.Engine/Plugins/PluginManager.cs`: `RenderEmbed` (context check and the
  `:popout` name), with the export call moved to `RenderEmbedCore`.
- `src/RynthCore.Engine/ImGui/Panels/MiniRemoteFace.cs`: header note on the fallback.

## Testing

- The engine builds in Release, and ItemHudTests pass.
- Not yet checked in game: the popped-out Mini Remote, with a current and with an older RynthAi.
