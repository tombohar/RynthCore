# Char and Mini Remote menus: Character on top, server plugin windows below

## What changed

- **New server-section registry** (`UI/ServerMenus.cs`).
  - Each server is defined once, with how to detect it and the command that forces it.
  - Each installed plugin with windows for that server adds its items.
  - A section shows only while its server is detected or forced, and only when it has at least
    one item. So it appears exactly when you're on that server with one of its plugins
    installed.
  - Infinite Leaftide is registered in `EntryPoint.RegisterServerMenus`:
    - detection: `IltInfo.IsIlt`;
    - force: `/rc ilt on`;
    - with RynthAi installed: the Mini Remote's Character, Quests, Pets, Banking, Gear and Games
      tabs, Guardian, and Progression;
    - with RynthILT installed: Bounties.
  - Items are `/rc` commands, so the engine and RynthAi run the same thing.
  - The engine pushes the sections to RynthAi as the `"servermenu"` remote command. It sends them
    whenever they change (checked once a second from the bar's draw) and again every 30 s.
- **RynthAi dashboard, Char right-click menu:**
  - Top, **Character**: Mini Remote, Spells, Skills, Inventory and Item Count.
  - Bottom: the active server sections (`ServerMenus.DrawImGuiMenu`).
  - When server plugins are installed but none matches the world, the bottom half shows a note
    and "Treat this world as <server>".
  - The menu's fixed list of ILT tabs, Guardian and Progression is replaced by the registry.
- **New and extended `/rc` commands:**
  - `/rc spells [show|hide|toggle]`: RynthAi's Spells window.
  - `/rc skills [show|hide|toggle]`: the Skills panel.
  - `/rc progression`: the Skills panel on its Progression tab.
  - `/rc miniremote tab <tab>`: opens the Mini Remote window on one of RynthAi's tabs.

## Files

- `src/RynthCore.Engine/UI/ServerMenus.cs`: new.
- `src/RynthCore.Engine/EntryPoint.cs`: `RegisterServerMenus`.
- `src/RynthCore.Engine/ImGui/ImGuiBar.cs`: calls `ServerMenus.Tick()` each frame.
- `src/RynthCore.Engine/ImGui/Panels/RynthAiFace.cs`: the Char menu (`CharMenu`,
  `PanelMenuItem`) and the launcher tooltip.
- `src/RynthCore.Engine/Compatibility/RynthCoreChatCommands.cs`: `/rc spells`, `/rc skills`,
  `/rc progression` and `/rc miniremote tab`.

## Testing

- The engine builds in Release, and ItemHudTests pass.
- In-game checks are listed in the matching RynthSuite changelog.
