# Inventory: pick several items, then Drop, Give or Move them

**Date:** 2026-10-11
**Area:** Engine (`RynthCore.Engine`), Inventory panel (both looks: Modern and Classic) and
`InventoryModel`.

## What's new

The Inventory window can select several items and act on all of them at once.

- **Picking items** (in both looks):
  - **Click:** selects one item, as before.
  - **Ctrl+click:** adds an item to the selection, or removes it if it is already selected.
  - **Shift+click:** selects every item from the last clicked item to this one, in the order the
    grid shows them. **Ctrl+Shift+click** adds that run to the current selection.
  - **Right-click > Select all shown:** selects every item the grid shows, meaning the open pack or
    the search's matches. The menu item shows the count.
  - **Right-click > Select only this item** returns to a single item.
- **Acting on the selection** when more than one item is selected:
  - The zones read **Drop N** and **Give N: <target>**. Clicking a zone, or dragging any selected
    item onto it, acts on the whole selection.
  - Dragging a selected item onto a pack (the pack column, or a pack in the grid) moves the whole
    selection there. The drag ghost shows the count.
  - The right-click menu on a selected item shows *N items selected*, then **Drop N items**,
    **Give N items to <target>** and **Move N items to >** (with a list of your packs).
  - The status row shows the size of the selection and how to use it.
- **Confirmation:** Drop and Give of several items open a confirmation that lists the items by name
  (for example "Pyreal  x3", up to 10 lines). Give adds "Given items can't be taken back." Move
  starts straight away.
- **Progress and Stop:** while the items are sent, the status row shows progress such as
  *Giving 3 of 12 to Bob*, with a **Stop** button. When the run ends, the status row and the log
  show a summary such as *Gave 10 of 12 items to Bob; 2 failed (first: …).* or *Stopped: dropped
  3 of 12 items; 9 not sent.*

## Behaviour

- AC still gets one item action at a time. The batch passes each item to the same request path a
  single click uses, so every item is checked, sent and watched exactly as before, including the
  wait for AC's action gate. The next item goes only after the previous one has ended (done,
  refused, gone or timed out), with a short gap of 0.25 s.
- The batch keeps running with the Inventory window closed and ends by itself.
- Some items are left out of a run, and the confirmation or the status row says how many and why:
  - Worn and wielded items.
  - Packs and foci sent to a side pack (they only fit in the main pack).
  - Items that have already left the inventory.
- Items already in the target pack are skipped quietly, as a single item's drop on its own pack
  is. A group dropped back onto its own pack does nothing.
- A Give stops early, and counts the remaining items as failed, if the target disappears or you
  leave the world.
- At most 400 items per run.
- While a run is going, single item actions (Use, Split, Equip and so on) are refused with
  "Several items are being sent; wait, or press Stop." A run can't start while a single action is
  still waiting.
- Items that leave the inventory (given, dropped or merged) drop out of the selection
  automatically.
- A plain click on one item of a selection keeps the selection if you drag, and narrows it to that
  item if you release without dragging, as in Windows Explorer.
- No keyboard shortcuts were added (such as Ctrl+A or Delete). Keys may belong to AC, so selection
  uses mouse modifiers only.

## Files

- `src/RynthCore.Engine/ImGui/Panels/InventorySelection.cs` (new): the selection itself (ids, picked
  order, anchor, ranges, select all, prune). It has no engine state, so it is unit-tested.
- `src/RynthCore.Engine/ImGui/Panels/InventoryFace.MultiSelect.cs` (new): clicks, group actions,
  the confirmation, labels, the group menu, the status row's progress and Stop button, and the
  drag badge.
- `src/RynthCore.Engine/Compatibility/InventoryModel.Batch.cs` (new): `RequestBatch`,
  `CancelBatch`, `Batch` and `BatchRunning`, plus the main-thread runner that feeds items one at a
  time.
- `src/RynthCore.Engine/Compatibility/InventoryModel.cs`: the model is now a partial class. Single
  requests are refused during a run, and the tick drives the runner even with the panel closed.
- `src/RynthCore.Engine/ImGui/Panels/InventoryFace.cs` and `InventoryFace.Classic.cs`: grid clicks,
  selection highlight, zone labels, drag release, context menu and status row now use the
  selection.
- `tools/InventorySelectionTests` (new): 42 checks covering click, Ctrl+click, Shift and Ctrl+Shift
  runs in both directions, a missing anchor, select all, prune and id 0.
  Run with `dotnet run -c Release`.

## Verification

- `dotnet build -c Release` for the engine: no errors and no new warnings.
- `tools/InventorySelectionTests`: 42/42 checks passed.
- Not yet tested in game.
