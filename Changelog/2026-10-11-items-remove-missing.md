# Items panel: Remove Missing button

## What changed

- `ImGui/Panels/ItemsFace.cs`: a "Remove Missing" button at the top of the Items panel, above the
  lists (which can run long), with a tooltip. `AddSelected` takes an optional tooltip.
- `UI/Data/ItemsData.cs`: `ItemsCommands.CleanMissing()` calls RynthAi's `RynthPluginCleanItems`
  export, then refreshes the panel.
- What RynthAi does when the button is clicked:
  - removes listed weapons and consumables that are no longer in your inventory;
  - re-points an entry at an unlisted item of the same name, when your pack has one;
  - does nothing while your inventory is still loading;
  - reports in chat what it removed and re-pointed.
- With a RynthAi that lacks `RynthPluginCleanItems`, the click is logged and nothing happens.

The engine builds in Release. Not yet tested in game.
