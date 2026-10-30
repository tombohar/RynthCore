# Bar declutter: Item Count and Skills off the bar, Inventory buttons, Skills under Oracle

Branch `feature/bar-declutter`, from `status-export`. Not deployed, not released. The idea and the first
version are Silentkelpie's (rynth PR 6, "inventory bar declutter ... Skills under Oracle"); this takes the
parts the owner approved and changes what the owner decided differently.

## Bar

- Item Count no longer has a bar icon. It opens from Inventory's Count button, `/rc itemcount`, and the
  Character menu.
- Mini Remote keeps its bar icon (Silentkelpie removed it; it stays). Inventory gets a Remote button too, as
  an addition.
- Skills leaves the bar only while RynthOracle provides the Skills / Attributes / Progress tabs. The icon
  shows whenever it does not: no Oracle window registered, or an Oracle that has never sent
  `/rc oracle-skills` (an older Oracle). Nobody loses the way in.

## Skills under Oracle

- `/rc oracle-skills <skills|attrs|progress|off>`: Oracle says which Skills body to embed in its window
  (`OracleSkillsEmbed`; the contract is unchanged from the PR). Any valid form also tells the engine that
  Oracle has the tabs. An Oracle that wants the Skills icon hidden before the player first opens the tab can
  send `/rc oracle-skills off` once at startup.
- `/rc skills`, `/rc progression` and the Character menu's Skills line go to Oracle (`/ro skills`,
  `/ro progress`, `/ro attrs`) when its tabs are there, else open the standalone Skills panel as before.
  `/rc skills hide` closes Oracle only while it shows a Skills tab; `/rc skills toggle` closes it when it
  already shows Skills.
- Fixed from the PR: the embed re-ran `SkillsFace.OnShown()` every two seconds and nothing ever called
  `OnHidden`, so the Progression subscription stayed on after Oracle left the tab. Now the face is shown
  once when the embed first draws and hidden once when it stops (`ShownLatch`): `/rc oracle-skills off`,
  Oracle's window closed or removed, the Oracle plugin unloaded, engine reload / shutdown, or no draw for
  a second (idle sweep). `OnShown` / `OnHidden` themselves are idempotent, so subscribing twice never
  registers twice.

## Inventory

- Modern header and Classic title bar get Mini Remote, RynthSort (`/rsort`), Item Count and Global Inventory
  (`/ginv`) buttons. The Character menu gets a Global Inventory line.
- The PR's four text buttons were about 190 px wide: at the retail width (300, scale 1) they pushed the
  title to nothing and put Vault (Aelrynth) at about x=-44, off the panel. `UI/InventoryTitleLayout.cs`
  now lays the bar out: the size / tools / Modern toggles stay at the right, Vault next to them is always
  there, and the four buttons take the richest style that leaves the title 56 units: text (480 wide and
  up), icons (360, or retail without Vault), or one "..." menu (retail with Vault). The title is cut with
  an ellipsis to the room it has. The Modern header does the same with icons / menu / nothing and drops the
  item count text last.
- Settings > Inventory Management > Floating HUDs tooltip says the floating pack count HUD is not the Item
  Count panel.

## Not taken from the PR

The in-world hot-reload hold, the chat combat filters (already merged), the Enlightenment calculator UI, his
changelog files and version bumps.

## Files

- ImGui/Panels/ShownLatch.cs, OracleSkillsEmbed.cs, SkillsRouting.cs (new), SkillsFace.cs,
  ScriptWindowFace.cs, InventoryFace.cs, InventoryFace.Classic.cs, RynthAiFace.cs
- ImGui/ImGuiBar.cs, ImGui/ImGuiPanelHost.cs (one idle-sweep line)
- UI/InventoryTitleLayout.cs (new), UI/ScriptWindows/ScriptWindowRegistry.cs, UI/Data/SettingsData.cs
- Compatibility/RynthCoreChatCommands.cs
- Tests: `tools/BarDeclutterTests` (lifecycle balance, TabsAvailable rule, title layout grid)
