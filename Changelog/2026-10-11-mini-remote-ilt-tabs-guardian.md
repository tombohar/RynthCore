# Mini Remote gets the ILT tabs; Guardian moves under the ILT button

## What changed

- **The Mini Remote window hosts the ILT Hub's sections as tabs.** RynthAi draws them on its
  `"miniremote-window"` surface: Remote, then on ILT worlds Character, Quests, Pets, Banking, Gear
  and Games. The window already fits itself to the size the plugin asks for, so it refits when the
  tab changes.
- **New Guardian panel** (`GuardianFace`) in the ILT section.
  - It hosts RynthAi's Temple guardian tools through the new embed surface `"guardian"`.
  - It is registered when `RynthCore.Plugin.RynthAi.dll` is installed.
  - Like Bounties, it has no bar button of its own. The bar's ILT button lists it, on Infinite
    Leaftide only. With Bounties and Guardian both present, the ILT button opens a menu.
  - The panel is user-sized (540x520 by default), and the plugin fills its body.
  - Until RynthAi can draw it, the body says why: not loaded, not logged in, or the embed failed.
- **`/rc guardian [show|hide|toggle]`** is new. **`/rc bounties`** and **`/rc miniremote`** now
  take `show|hide|toggle` too; with no argument they toggle, as before. RynthAi uses these to open
  the panels from `/ra guardian window` and `/ra bounty`.
- **`/rc ilt`** also reports whether RynthAi's Guardian panel and ILT tabs are available.
- **RynthAi dashboard, Char launcher.**
  - Left-click opens the Mini Remote window on its Character tab.
  - Right-click lists the Mini Remote, its six ILT tabs, Guardian (when registered) and
    Progression. The old "Mini Remote (ILT Hub)" entry and the per-section windows are gone.
- **Wording.**
  - Settings > trace categories: "ILT Hub on" is now "ILT on".
  - The Mini Remote drawer tooltip now points to the window's ILT tabs.
  - The Skills panel's Progression message now says "ILT features".

## Files

- `src/RynthCore.Engine/ImGui/Panels/GuardianFace.cs`: new.
- `src/RynthCore.Engine/EntryPoint.cs`: registers `GuardianFace` with RynthAi.
- `src/RynthCore.Engine/ImGui/ImGuiBar.cs`: `IltPanels` now includes Guardian.
- `src/RynthCore.Engine/Compatibility/RynthCoreChatCommands.cs`:
  - `/rc bounties|guardian|miniremote [show|hide|toggle]`;
  - the helpers `TryPanelMode`, `SetPanelOpen` and `IltPanel`;
  - the extra `/rc ilt` line.
- `src/RynthCore.Engine/ImGui/Panels/RynthAiFace.cs`: the Char launcher and menu
  (`ShowMiniRemoteTab`).
- `src/RynthCore.Engine/ImGui/Panels/RynthAiFace.Drawers.cs`, `SettingsFace.cs`,
  `SkillsFace.Progression.cs` and `MiniRemoteFace.cs`: wording and comments.

## Testing

- ItemHudTests: 63 assertions pass.
- Engine and plugin both build in Release.
- In-game checks are listed in the matching RynthSuite changelog.
