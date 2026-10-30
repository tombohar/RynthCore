# Settings: Navigation > Dungeon Patrol

## What changed

- `UI/Data/SettingsData.cs`: new `DunPatrol*` properties and a "Dungeon Patrol" section on the
  Navigation page:
  - Coverage (combo): Whole dungeon (default) / Loops only / Everything;
  - Skip Dead Ends Up To (cells), 0-10, default 3 (hidden for Everything);
  - Avoid Lava/Acid Floors, default on;
  - Distance From Walls (yd), 0.5-2, default 1;
  - Walk Into Rooms From (yd), 3-40, default 10;
  - a note that changing them rebuilds a running patrol.
- RynthAi (RynthSuite feature/patrol-settings) builds the dungeon patrol with them and rebuilds a
  running patrol, without stopping the macro, once a change has been still for 1 s. The defaults
  are the patrol as it was before.
- An older RynthAi ignores the new fields; the matching RynthAi keeps its own values when an older
  engine doesn't send them.

Not yet tested in game.
