# ILT section: everything reads RynthILT

Pairs with RynthSuite `refactor/ilt-into-plugin` (the ILT Hub moved from RynthAi into the RynthILT
plugin).

## What changed

- The Guardian panel draws RynthILT's "guardian" surface and is registered with RynthILT
  installed (an older RynthAi still answers when RynthILT has none).
- The Skills panel's Progression tab reads `RynthILTGetProgressionJson` and sends its edits as
  `prog ...` through `RynthILTCommand` (an older RynthAi's export and remote command stay the
  fallback).
- `UI/Data/IltData.cs` asks RynthILT once a second (`RynthILTGetStatusJson`). The bar's ILT button
  and the ILT server menu need RynthILT loaded, and RynthILT's own verdict (its server list, or a
  character's "Treat this world as ILT") counts as Infinite Leaftide next to the host and
  world-name detection.
- Server menus: the Mini Remote's ILT tabs need RynthAi and RynthILT; Guardian, Bounties and
  Progression need RynthILT.
- Settings: the RynthAi "Server Features (ILT)" rows and the trace "ILT on" button are gone
  (RynthILT's Character tab, `/ra hub servers`, `/ilt trace`).
- Hotkeys (Settings > Hotkeys, `/rc hotkey`, from 2026.10.13.1): "Open/close Bounties" and
  "Open/close Guardian" (Infinite Leaftide) are listed, typed and run only where the bar shows
  its ILT button (Infinite Leaftide with RynthILT loaded); elsewhere a saved one says why and
  does nothing (it can still close a panel left open).
- The update feed describes RynthILT as the ILT Hub, Guardian, bounties and progression
  (`installer/Publish-Update.ps1`, which already ships it).
- No plugin API change.

## Tests

- `tools/IltTests`: RynthILT's verdict (`IltDetect.ParsePluginStatus`, `IltDetect.Combine`).
- `tools/HotkeyTests`: the ILT panels are offered only on ILT (`IltPanelsOnlyOnIlt`).

Not yet tested in game.
