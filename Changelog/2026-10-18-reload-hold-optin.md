# Opt-in: hold engine reloads while in the world

Branch `feature/reload-hold-optin`, from `status-export`. Not deployed, not released. No native
(`Loader.c`) change, so no loader version bump.

SilentKelpie's PR 6 (his 2026.10.16.3 / .5 "hold hot reloads while in-world") as a setting that is
**off by default**, so a reload behaves exactly as before unless the player turns it on.

## Why

On his machine, stacked automatic hot reloads (the loader's file watcher, engine deploys) while logged
in were crashing clients: the loader shut the engine down, then the CoreCLR re-init failed mid-flight.
His fix held every reload, forever, until logout, and even a reload asked for by hand waited. That is
too blunt as a default, so it is opt-in, bounded, and leaves hand reloads alone.

## What

- `engine.json` `HoldReloadInWorld` (default `false`; only a `true` is written; missing, or anything
  that is not a boolean, reads as `false`). Settings > Misc: "Hold reloads while in the world", with a
  tooltip, directly under "Background Identify".
- On: an automatic engine reload waits, with the game still running on the old engine, until the player
  logs out or reaches character select (`ReloadInWorldGate`, polled every 2 s, "still in the world"
  logged every 30 s, one chat line). It runs in `RynthCoreShutdown` before the plugin (combat) deferral,
  so old and new loaders behave the same.
- **Never forever.** `engine.json` `HoldReloadInWorldMaxMinutes` (default 10, valid 1-240; out of range
  is clamped, a non-number reads as 10; only a non-default value is written; Settings > Misc "Give up
  holding after (min)", shown while the hold is on). At the bound the hold logs once
  (`held N min, reloading anyway`) and the reload goes ahead.
- **Hand reloads are never held.** ERl, the bar RL button, `/rc rl` and the hotkey set the existing
  hand-reload mark; the gate now honours it (`ShouldHold(byHand, ...)`; his version ignored `byHand`).
  A hand reload that arrives while an automatic one is being held ends the hold at the next poll and also
  skips the plugin deferral, like any hand reload. Switching the setting off mid-wait also releases it.
- Fails open: a throwing query, setting read or gate counts as "do not hold".
- Off: none of this runs; no wait and no new log line.

## Left out of the PR

- `Loader.c` ReloadWatcher poll and `RynthCoreMayHotReload` export (loader 2.2): the wait is done in the
  managed `Shutdown`, which the loader already calls on its reload thread (the same place the plugin
  deferral waits), so the native poll is redundant. It would also have needed its own bound in C, which
  could not be built or tested here.
- `RequestManualReload` and the reroute of ERl / bar RL / `/rc rl` / hotkey: hand reloads are not held now.
- The rest of that commit (inventory bar declutter, Oracle Skills embed, ...).

## Files

- `src/RynthCore.Engine/ReloadInWorldGate.cs` (new), `EntryPoint.cs`
- `Plugins/EngineSettings.cs` (keys, owned-keys list), `UI/Data/SettingsData.cs` (Settings > Misc rows)
- `tools/ReloadInWorldGateTests` (new, 44 checks), `tools/EngineSettingsTests` (round trip, clamp, defaults)

## Not verified

No live client: the actual deferral in a running world, the chat line, and the Settings rows were not
exercised in game.
