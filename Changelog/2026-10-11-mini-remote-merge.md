# Mini Remote: one window, resizable quick-launch boxes

## What changed

RynthCore's "Mini Remote" window and RynthAi's floating "MINI REMOTE" are merged into one window.

- **`MiniRemoteFace` hosts RynthAi's Mini Remote** through the new plugin embed surface
  `"miniremote-window"`. That covers stats, target, pet, item slots, toggles, bank, rebuff,
  translate and the guardian answer, with RynthAi's own Options menu.
  - The window fits itself to the size the plugin asks for (`FitWindow`, capped to the screen
    height; popped-out windows are left alone).
  - RynthAi hides its floating copy while this window shows it.
  - Without RynthAi, or when its embed fails, the window falls back to the engine's own slot grid
    (per-character `item_huds.txt` slots, More/Fewer slots, drag-and-drop), as before.
- **Grow and shrink the quick-launch boxes** in the fallback grid:
  - Ctrl+mouse wheel over the grid changes the box size by 4 px per notch.
  - The right-click menu has "Bigger boxes (N px now)", "Smaller boxes" and "Default box size".
  - Saved per character in `item_huds.txt` as `slotsize=<px>` (24-72, default 36). The line is
    written only when the size isn't the default, so existing files don't change.
  - RynthAi's hosted grid has its own size control: Ctrl+wheel, or Options > Slot size.
- **Hub launcher** on the RynthAi dashboard: left-click now toggles this Mini Remote window instead
  of RynthAi's floating one. Right-click still opens the Mini Remote drawer, or the window when
  the dashboard is popped out.

## Files

- `src/RynthCore.Engine/ImGui/Panels/MiniRemoteFace.cs`: rewritten to host the plugin, with the
  fallback grid, the box size controls and the window fit.
- `src/RynthCore.Engine/UI/Data/ItemHudModel.cs`: `ItemHudCharacter.SlotSize`, `WithSlotSize`,
  and parsing and writing `slotsize=`.
- `src/RynthCore.Engine/ImGui/Panels/RynthAiFace.cs`: the Hub launcher opens the Mini Remote window.
- `tools/ItemHudTests/Program.cs`: new `SlotSizes` case covering clamping, edits, round-trip,
  default not written, and bad values.

Works best with the matching RynthAi change. An older plugin doesn't know `"miniremote-window"`, so
the window shows the engine grid instead.

## Tests

- ItemHudTests: PASS (63 assertions).
