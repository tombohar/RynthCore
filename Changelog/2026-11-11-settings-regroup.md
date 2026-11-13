# Settings panel regrouped: 14 tabs in three groups, 10 settings get rows

Branch `feature/settings-regroup`, from `rc/2026.11.next` f219759. Not deployed. RynthSuite's chat text and tooltips that
name a tab are on `feature/settings-regroup-text` (from `rc/2026.11.next` a91104fe). The phone app's own copy of the
groups is on RynthRemote's `feature/settings-regroup`. No plugin API change; any RynthAi works with it.

## What changed

- The tabs, in three sidebar groups:
  - **RynthCore · this PC:** Interface (Display + UI), Game Client (new), Hotkeys.
  - **RynthAi · saved in the profile:** General (was Misc), Recharge, Combat (was Melee Combat), Spell Combat, Ranges,
    Navigation, Buffing, Looting, Inventory (new), Vendoring.
  - **Support:** Diagnostics.
- What moved where:
  - **Interface:** the theme and the dashboard look (Look), text size, the RynthCore toolbar, the four "Hide retail"
    switches, your vitals plate and Show Target Stamina / Mana, the RynthDefective windows in the retail toolbar's
    place, and window layout.
  - **Game Client:** Borderless Windowed and Remember Game Window Position (Game Window); the FPS limit, the video
    patch and Background Identify (Performance); Close Client on Server Disconnect and the reload hold (Disconnects
    And Updates); the server picker (Server, from Diagnostics).
  - **Hotkeys:** Insert Key Releases the UI (from Display).
  - **General:** login and VTank switches (Starting Up), Peace Mode When Idle and Weapons Back After Heals (Between
    Fights), Escape Acid and Lava and the macro stops (Safety Stops, from Misc and Recharge), Recall On Death (from
    Recharge), fellowship management (from Spell Combat), Vaulted Dungeons (Server Features, from Navigation).
  - **Combat:** targeting (from Ranges) and the blacklist (from Misc) under Targeting; line of sight and the missile
    arcs (from Misc); both environment responses together (Magic Response from Spell Combat); missile ammo crafting
    (the old Crafting tab).
  - **Buffing:** Buff Spell Interval (from Spell Combat), Use Buff Items (from Recharge).
  - **Inventory:** packs (Autostack, Autocram, Tidy, Floating HUDs), aetheria, salvage and its timers (from Looting),
    Give Interval (from Misc), Item Info (from Display).
- Follow-up (`feature/settings-hunt-df-rows`, on `rc/2026.11.next` 1d6345a): **Hunt On Login** under General > Starting
  Up (one or the other with Patrol On Login: ticking one clears the other, as RynthAi keeps them), and the **Dirty
  Fighting rotation** under Combat > Attack Power & Height with its **Effect order** and **Then attack** (RynthAi's
  DfHeightRotation, DfHeightOrder, DfDefaultHeight; also its own Dirty Fighting window). Hidden until RynthAi sends them.
- Taken out: Misc's duplicate Auto Cram (Inventory > Packs > Enable Autocram is the same switch) and Recharge's three
  "Helper Settings — NOT WIRED YET" sliders (they do nothing; their values stay in the payload).
- New rows for settings RynthAi already had and sends (each hidden until RynthAi sends the value, so an older RynthAi
  shows nothing and a save leaves them alone):
  - Combat > Targeting: **Cast Settle (ms)**.
  - Combat > Weapon Swaps: **Weakness Swap Min Gain %**, **No Swap Under Target Health %**, **Wait After Target
    Switch (ms)**, **Prefer Dual Wield**, **Off-hand Default** (Auto, Shield, Weapon, None).
  - Buffing: **Idle Margin (minutes)** (with Rebuff When Idle on), **Skip buffs this character can't use**.
  - Looting: **Loot Mode** (RynthAi's loot messages already pointed here), **Chest relock wait (s)** (also on the
    Item Tool), and the **Loot By Name** section (Names, Radius; 399b519 rebuilt, never shipped before).
- Pins: My settings drawer pins are "Tab/Section/Label" keys. A pin saved under the old layout is found again by its
  label on the tabs its old tab's rows went to, and the file is rewritten with the new keys when it loads
  (`QuickSettings.MigrateKey`). Misc's Auto Cram pin goes to Enable Autocram. An engine older than this one won't
  draw the rewritten pins (they stay in the file).
- `/rc settings <tab>` and a plugin's link accept the old names too (`SettingsSchema.FindTab`: Display and UI open
  Interface, Misc opens General, Melee Combat and Crafting open Combat). `/rc diagnostics` and `/rc serverpick`
  open Game Client, where the server picker is now.
- Text that names a tab follows: chat replies, tooltips, the video patch banner and the comments.

## Tests

- `tools/SettingsLayoutTests` (new): the tab order and groups; every one of the 301 pin keys of the old layout
  (`OldPinKeys.txt`, dumped from f219759) leads to its row; the old tab names; where the moved rows are.
- `tools/CombatSettingsTests` and `tools/VaultedSettingTests` follow the rows to Combat and General.
