# Characters panel: your other RynthCore clients, with vitals and distance

UtilityBelt's networked character window, as an ImGui panel. It reads the RynthNet plugin's snapshot,
so it shows the other clients on this PC that run RynthNet.

## What changed

- **New panel: Characters.** Open it from its bar button (three people icon), the dashboard's Char
  menu, or `/rc characters` (alias `/rc chars`). It exists only when RynthNet is installed.
- **Rows.** This client comes first, dimmed. The others follow in alphabetical order, which stays
  stable as characters move: a distance order would reshuffle several times a second for characters
  standing together. Each row shows:
  - an arrow pointing to the character, turned to where you face;
  - the name, plus its server when that isn't yours;
  - what it is doing: botting (fighting, navigating, ...), idle, idle in a combat stance, dead,
    portal space, or no status yet;
  - a warning when its status is more than 5 s old (a frozen client);
  - distance in yards and a compass point (N, NE, ...);
  - health, stamina and mana bars with current/max;
  - where it is: map coordinates, or the dungeon's name from the Atlas.
- **Distance rules** (Sense's): a distance shows when you are both outdoors (or in surface
  buildings), or both in the same dungeon landblock. Otherwise the row says "elsewhere" and gives
  the dungeon name or the coordinates. A character on another server shows only its server.
  - A character this client can see is placed by its object position, which is fresher than its
    status.
- **Click** a row to select that character in game when this client can see it. This needs
  RynthNet 0.1.1 on that client, for the object id.
- **Right-click** a row for commands, sent as `/rn tell <name>, <command>`:
  - Follow me (`/ra follow <you>`);
  - Stop following (`/ra follow off`);
  - Start macro (`/ra start`);
  - Stop macro (`/ra stop`);
  - Everyone: follow me (`/rn followme`).

  The other client's RynthNet remote setting (`/rn remote`) decides whether they run. A refusal
  comes back as a chat line.
- The top line says how many characters there are, or why there are none (RynthNet not loaded or
  offline). It also has an "everyone follow me" button and a help tooltip.
- The panel refreshes twice a second while it is open. It works docked and popped out, and follows
  the panel text size.

## Files

- `UI/Data/CharactersModel.cs` (new): snapshot parsing, distance, compass, location, state text
  and row order. It has no engine dependencies, so the tests compile it in.
- `UI/Data/CharactersData.cs` (new): `CharactersSource` (2 Hz, RynthNet's
  `RynthPluginGetSnapshotJson`, plus this client's pose, vitals and object table) and
  `CharactersCommands` (`RynthPluginApplyRemoteCommand("rn", ...)`).
- `ImGui/Panels/CharactersFace.cs` (new): the face.
- `UI/Data/RynthNavAtlasData.cs`: `IsMapLandblock`. An indoor cell in a landblock with Atlas
  surface places counts as a building, not a dungeon.
- Wiring: `UiSources.cs`, `EntryPoint.cs` (registered when `RynthCore.Plugin.RynthNet.dll` is
  installed), `ImGuiBar.cs` (button and icon), `RynthAiFace.cs` (Char menu),
  `RynthCoreChatCommands.cs` (`/rc characters`).

Pairs with RynthSuite `feature/characters-panel` (RynthNet 0.1.1, which adds `id` and `cmode` to
the shared status). Older RynthNet clients still show up. They just can't be selected by a click,
and show no combat stance.

## Tests

`tools/CharactersPanelTests`: 84 checks, all passing. They cover:

- parsing the current snapshot and older entries (no id/cmode, no bot, null status, character
  select, wrong types);
- bad JSON;
- distance and compass within a landblock and across landblocks;
- the dungeon rules, surface buildings and the coordinates text;
- the state text, the stale marker and the stable order.

An offscreen render with fake characters checked the layout at text sizes 0 and 3, a narrow window,
the tooltip and the menu.

Not yet tested in game.
