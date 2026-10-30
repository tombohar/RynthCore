# Add-to-loot attribute checklist, Variable Power, Hitting the Environment and Kite rows

Branch `feature/sk2-loot-combat`, from `integration/pr-fixes-2026-10-18`. Not deployed, not released.
Pairs with RynthSuite `feature/sk2-loot-combat` (RynthAi's side of all of it).

From SilentKelpie's RynthCore PR 6 (commit 7, and the Settings rows of commit 6), adapted.

## What

- **Add to loot profile, attribute checklist** (`ImGui/Panels/LootAddDialog.cs`, `UI/Data/LootEditData.cs`).
  The popup lists every attribute the item has, grouped (Item, Weapon / Armor, Ratings, Spells, Pet
  essence, T11, Requirements). Tick a row to require it, pick `>=`, `=`, `<=` (spells: or better /
  exact / any tier) and type a value. The popup is wider with the list. "Items like this" pre-ticks what
  it always required.
  - Wire: the ask carries `Choices` (one per row the player changed, null until then) and the draft
    carries `AttributesSupported` and `Attributes`. These are the DTO mirrors of RynthSuite's
    `LootEditWire`.
  - **Fallback:** a RynthAi without the checklist never sets `AttributesSupported`, so the popup keeps
    its single "Include T11 attributes" box and nothing else changes. An older engine against the new
    RynthAi never sends `Choices`, and RynthAi then builds exactly what it built before.
- **Settings > Melee Combat:** "Variable Power (monster rules)" (Variable Min % / Max %), "Hitting the
  Environment" (Missile Response, Hits to trip, Window, Move closer by, Closest, Skip target) and
  "Pull Away (Kite)" (Pull Distance, Tag Before Fleeing). **Settings > Spell Combat:** "Hitting the
  Environment" (Magic Response).
- **Damage panel, a monster's detail:** a "Power" combo (Default, High, Reckless, Variable,
  Reckless + Variable) and a "Kite" combo (Default, Off, PullAway). A new rule copied from the DEFAULT line
  follows it; one copied from a name rule keeps that rule's choices.

## Defaults

Nothing changes until a player turns it on: every response is Off, every rule's Power and Kite are
Default (Default Power is the global Power settings, Default Kite on the DEFAULT line is Off).

## Compatibility

- The new RynthAi settings are **nullable mirrors** in `RynthAiSettings`, like the Vaulted Dungeons
  switch: a RynthAi that predates them omits them, the rows (and their section headers) stay hidden,
  and a save sends `null`, which RynthAi leaves alone. His version used plain ints and a bool, which
  would have written the engine's defaults over a RynthAi that did not have them.
- `Combo` rows gained an optional `when:` like the other kinds (needed to hide Missile / Magic Response).
- An older RynthAi ignores the Rule's new `AttackPower` and `Kite` fields.

## Not taken

The rest of that commit: the ring run-away and its engine side are decided separately; the chat, display
and EngineSettings pieces are on other branches.

## Tests

- `tools/CombatSettingsTests` (new): the mirrors, the rows' visibility and ranges, the schema defaults,
  the Power / Kite spellings and the rule's change detection.
- `tools/LootAddWireTests` (new): the popup's DTOs against what RynthAi's `LootEditWire` writes and
  reads, and the older-RynthAi fallback.
