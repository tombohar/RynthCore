# Settings > Navigation: Vaulted Dungeons master switch

Branch `feature/vaulted-setting`, from `integration/pr-fixes-2026-10-18`. Not deployed, not released.
Pairs with RynthSuite `feature/vaulted-dungeons` (the keeper itself).

From SilentKelpie's RynthCore PR 6 (commit 3), the Vaulted Dungeons row only. The Display chrome and
the chat 0x1C part of that commit are already merged.

## What

- Settings > Navigation, section "Infinite Leaftide": **Vaulted Dungeons**, off by default. It is the
  master switch of RynthILT's Vaulted Dungeons keeper (Infinite Leaftide / ACECustom only): off, the
  keeper does nothing (no recalls, no `/rc quit`, no Mini Remote tab, a saved On or Auto Start ignored);
  turning it off while it runs stops it at once.
- The setting is **RynthAi's** (`EnableVaultedDungeons` in the RynthAi profile, per character). The
  engine keeps no copy: `RynthAiSettings.EnableVaultedDungeons` in `UI/Data/SettingsData.cs` is only
  the mirror of RynthAi's settings payload, like every other row. RynthILT reads the switch from RynthAi
  (`RynthAi.Hub` `VaultedDungeonsEnabled`), so there is exactly one source of truth.
- The mirror is nullable, as the Nav panel's switches are: a RynthAi that predates the setting omits it,
  the row stays hidden, and a save sends `null`, which RynthAi leaves alone (his version used a plain
  `bool`, which would have written `false` over the switch).

## Left out of the PR

- The rest of that commit (already merged, or separate decisions): Variable Power and Hitting the
  Environment rows, the changelog notes, the installer version files.

## Files

- `src/RynthCore.Engine/UI/Data/SettingsData.cs`
- `tools/VaultedSettingTests` (new, 22 checks): the mirror's JSON (absent, false, true, case), the
  Navigation row (one, a checkbox, hidden until RynthAi sends the value, reads Off by default, edits
  only that property, tooltip). It loads the built engine assembly and reaches the internal types by
  reflection (`SettingsData.cs` has no standalone compile seam).

## Not verified

No live client: the row in the Settings window, and the keeper's reaction to the switch (RynthSuite side).
