# Settings: Navigation > Getting Back On Route > Go To Closest Waypoint

## What changed

- `UI/Data/SettingsData.cs`:
  - new `NavResumeNearest` property (default true);
  - new "Getting Back On Route" section on the Navigation page, with a "Go To Closest Waypoint"
    checkbox.
- With the option on, RynthAi:
  - heads for the closest waypoint on the route at macro start, when a Circular or Linear route
    loads, and when nav takes over again after a fight, loot, buff or door that moved the
    character 10 yd or more;
  - skips a waypoint the character has already walked past.
- With the option off, nav carries on to the waypoint it was heading for, and a loaded route
  starts at its first point.
- An older RynthAi ignores the new field. The matching RynthAi keeps its own value when an older
  engine doesn't send it.

Not yet tested in game.
