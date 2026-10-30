# Settings: On Death > Recall On Death

## What changed

- `UI/Data/SettingsData.cs`:
  - new properties `DeathRecallMode` (0 off, 1 spell, 2 item), `DeathRecallSpellId` (default 48,
    Primary Portal Recall) and `DeathRecallItem`;
  - a new "On Death" section after Safety Stops, with three rows:
    - "Recall On Death": Off, Cast a recall spell, or Use a recall item;
    - "Recall Spell": the nav editor's recall spells (`NavCommands.RecallIds`), shown only for
      the spell choice;
    - "Recall Item": the item name, shown only for the item choice.
- The spell names fill in from the client's spell table as it loads; until then they show as
  "Spell N (N)".
- What RynthAi does with it: after a death while the macro runs, once respawned, it casts the
  recall or uses the item, retries a fizzle, then rejoins the nav route and starts Travel Back To
  My Corpse from the landing spot.
- An older RynthAi ignores the new fields.

The engine builds in Release. Not yet tested in game.
