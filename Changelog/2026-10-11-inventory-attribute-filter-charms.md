# Inventory: filter by item attributes; Charms moved to the ILT Mini Remote

## Inventory attribute filter

- **New button in the tools row:** a list-check icon between the search box and the sort picker.
  - In the Modern look it is always visible.
  - In the Classic look, open the tools row first (the funnel in the title bar).
- **The popup editor:**
  - Filter on/off;
  - "Keep items matching all rules / any rule";
  - up to 12 rules, each made of an attribute, an operator and a value;
  - Add rule, Clear rules, Close.
- **Attributes known without an identify:** Name, Value, Burden, Workmanship, Stack size, Material,
  Fits slot (Head ... Aetheria), Equipped, Casts a spell.
- **Attributes that need an identify (marked \*):**
  - Armor level, Max damage, Speed;
  - Attack bonus %, Melee / Missile / Magic defense %, Elemental damage %, Mana conversion %;
  - Damage, Damage resist, Crit, Crit resist, Crit damage, Crit damage resist, Healing boost and
    Vitality ratings;
  - Level to wield, Arcane lore, Spellcraft, Max mana, Times tinkered, Max item level;
  - Armor set (name text), Spells (any spell's name contains the text);
  - Imbued, Bonded, Attuned, Retained.
- **Operators:**
  - numbers: >=, <=, =, !=;
  - yes / no flags: yes, no;
  - text: contains, doesn't contain (case-insensitive);
  - slot: is, is not.
- **Missing values:** an attribute the item doesn't have counts as 0, no, or empty text. For
  example, "Level to wield <= 150" keeps items with no level requirement.
- **Items the server hasn't identified yet:** a rule that needs an identify can't judge them, so
  they are hidden and counted.
  - The count appears in the header ("N not ID'd"), the button's tooltip and the popup.
  - "Identify N" in the popup asks the server about them, 4 a second while the panel is open.
    "Stop" empties the queue.
  - Items already identified, or asked about in the last minute, are skipped.
  - While such a rule is on, the list refreshes once a second so new answers show up.
- **Saving and Clear:**
  - The rules are saved in `%LOCALAPPDATA%\RynthCore\inventory_settings.txt`, on the
    `attrfilter=` line, when the popup closes.
  - Clear (closing the Classic tools row) turns the filter off but keeps its rules.
  - In Classic, the filter only applies while the tools row is open, so nothing is ever hidden
    without a visible control.
- **Behaves like the search and category filters:**
  - Classic lists matches from all packs;
  - grid cells stop acting as slots, and drops refuse to reorder;
  - "Nothing matches." shows when no item matches.

## Charms

- Settings > Charms Tracking is removed. It is now the Charms tab in RynthAi's Mini Remote
  (the matching RynthSuite change). Its poller (`UI/Data/CharmsData.cs`) and its `UiSources` registration are
  removed.
- The ILT server menu (right-click the Mini Remote) gains Charms after Gear. It opens with
  `/rc miniremote tab charms`.

## Files

- `Compatibility/InventoryAttributeFilter.cs` (new): the attributes, operators, matching (Match /
  NoMatch / Unknown), material and set names, and the saved line. It has no dependencies, so the
  RynthSuite host tests link it.
- `Compatibility/InventoryFilterFacts.cs` (new): an item's values from the panel's item and the
  appraisal cache (render thread, no AC reads).
- `Compatibility/InventoryModel.cs`: the identify queue (`QueueIdentify`, `IdentifyQueueCount`,
  `ClearIdentifyQueue`), sent on AC's main thread and cleared on a character change.
- `ImGui/Panels/InventoryFace.AttrFilter.cs` (new): the button, the popup and the per-item check.
- `ImGui/Panels/InventoryFace.cs`, `InventoryFace.Classic.cs`: wiring, `FiltersOn`, and the
  `attrfilter` setting.
- `ImGui/Panels/SettingsFace.cs`, `UI/Data/UiSources.cs`, `UI/Data/CharmsData.cs` (deleted),
  `EntryPoint.cs`: the Charms move.

The engine builds in Release. The filter has 5 new host tests in RynthSuite
(`InventoryAttributeFilterTests`), and all of them pass. Not yet tested in game.
