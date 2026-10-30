# RynthAi dashboard: the Classic (retail) look

Branch `feature/rynthai-classic`, from `status-export`. Pairs with RynthSuite
`feature/rynthai-classic-settings` (four VTank options on the settings bridge).

Tom 2026-10-19: players asked for a VTank-style panel next to the dashboard; implement the
AC-style design as an option. Design: the RynthAi Classic canvas (claude.ai artifact
2VCb8Lk7vGjBfoX73PJKVL, "Classic" page). Our own layout and wording; the retail pictures are
the player's own `client_portal.dat`, read at runtime (RetailSprites), so nothing ships.

## What

- **A second look for the dashboard.** `RynthAiDashboardState.Classic` (line 14 of
  rynthai_avalonia.cache, "classic|modern"; line 15 the open tab). Switched by the new Scroll
  chip on the Modern title row, the Modern button on the Classic title bar, Settings > Display
  > RynthAi Dashboard Look, and `/rc dashboard classic|modern|tab <name>`. Modern stays the
  default. The dashboard's size is kept and put back when switching back.
- **One retail window, eight tabs** (`ImGui/Panels/Classic/`):
  - **Main**: every setting of VTank's options page (ranges, autostack/autocram, peace
    mode and rebuff when idle, navigation/looting first, rare corpses only, fellowship,
    dispel items/self, M. charges when off, pets), Stop/Start, Rebuff all/Cancel, the
    settings profile, the five systems with a live line, and the recharge meters (retail
    vitals bars) with the heal / restam / get-mana trigger to drag and the top-off mark.
  - **Monsters**: the monster rules (Damage panel's source), weapon/damage/priority,
    debuffs and spell shapes as gems, Add target, Remove.
  - **Gear**: the Items lists (weapons with element, consumables with kind), add the
    selected item, clean missing, shield and mana tapping.
  - **Buffs**: buff profile, buffing on/off, every spell on you with its icon and time left.
  - **Route**: route file, route type, the points with your distance to each, add/remove,
    the overlay switches.
  - **Loot**: looting on/off, the profile in use, the edited profile's rules (on/off,
    move, copy, delete, add, save, reload).
  - **Meta**: meta on/off, file, state, the rules grouped by state.
  - **Tuning**: every Settings row, a tab at a time or searched; toggles, numbers, choices
    and headers drawn retail, the special rows with the Settings panel's drawing.
  Deep editing (loot conditions, meta rules, Damage-panel extras) opens the existing panel.
- **Settings**: four VTank options on the bridge (`UseDispelItems`, `AutoFellowMgmt`,
  `MChargesWhenOff`, `CustomPetRange`), nullable like EnableVaultedDungeons: hidden (dimmed
  on the Main tab) until a RynthAi that sends them.
- Minimized (the dashboard's shared flag) shows the title bar over the systems, Stop/Start
  and the three vitals meters.

## Tested

Local ACE test client (Buffi, +Buffi @ ACEmulator) on a test runtime: every tab drawn with
the retail art, the window kept on the screen when a tab is taller, settings both ways
(RynthAi `/mt opt set usedispelitems true` showed on the Main tab; the profile on disk
followed).

## Files

- `src/RynthCore.Engine/ImGui/Panels/Classic/*` (new)
- `src/RynthCore.Engine/ImGui/Panels/RynthAiFace.cs` (switch, Scroll chip)
- `src/RynthCore.Engine/UI/RynthAiDashboardState.cs` (Classic, ClassicTab)
- `src/RynthCore.Engine/UI/Data/SettingsData.cs` (Dashboard Look row, the four options)
- `src/RynthCore.Engine/Compatibility/RynthCoreChatCommands.cs` (`/rc dashboard`)
