# Skills > Progression: Enlightenment calculator

Branch `feature/enl-calculator`, from `integration/pr-fixes-2026-10-18`. Not deployed, not released.
The idea and the first version are Silentkelpie's (rynth PR 6, "Skills Progression: Enl calculator UI");
this takes only the calculator UI and its data, checked against the producer, with its failure modes
tightened.

## What it shows

On the Enlightenment section of the Progression tab, under the plan totals, an **Enl calculator**
collapsible (open by default), drawn from what RynthILT's `AppendEnlCalculatorJson` sends:

- **Per-level table**: Enl, Item, Qty, Lum for each step of the plan. Green while the simulated stock still
  covers that step, amber after. The first 12 steps show; "Show all N steps" lists up to 100 in a table
  that scrolls on its own, so a long plan never stretches the panel.
- **Still need / have**: luminance and each material.
- **Sigil craft chain (301-325)**: the eight chain rows (craft rows tagged), with RynthILT's note.
- Two lines under "Coins per token": the e-coins needed, and the luminance to buy them.

## Not changed

- Which luminance figure the Augmentation rows and the planners use. RynthILT's calculator simulates with
  its **banked** luminance (`Bank.Luminance`), not the character's; the footnote says "banked luminance"
  because that is what the numbers are (the PR's own text said "character", which the producer does not do).
- Nothing else of the PR's commit: hot reload hold, bar declutter, Oracle embed, chat filters and the
  changelog / version files are handled elsewhere. The "Plan to level" label and the "Totals to plan:"
  heading from the PR are left as they were.

## Absence and bad data

- An older RynthILT / RynthAi, or a server without ILT, sends none of the fields: the arrays are empty, the
  strings empty, and the section and the two e-coin lines are not drawn.
- The row arrays are read by tolerant converters (`UI/Data/EnlRowConverters.cs`). `null`, a non-array, a row
  that is not an object, a missing or mistyped field, unknown extra fields: that row (or just that field)
  falls back to its default and the rest of the snapshot parses. Before, any such `JsonException` would have
  turned the whole Progression snapshot into "unavailable" and hidden the Augmentation planner too. Rows
  are capped at 200 per array on parse; the table draws at most 100 level rows and 16 material / chain rows.
- `ProgressionSource.ParseJson` is the one parse (the poll uses it).

## Width

The tables are 16 less than the panel, with the label column stretching and the number columns fixed
(scaled down below the retail width of 300). Cell text wraps inside the cell instead of running off the
panel; the notes wrap at the panel's own wrap position, like the rest of the section.

## Lifecycle

No new subscription: the calculator draws the Progression snapshot the Skills face already subscribes to in
`ProgressionShown` / `ProgressionHidden`, so the `ShownLatch` show / hide balance of the embedded Skills face
is unchanged. Row text (level, count, chain numbers, the "(craft)" label) is formatted once per snapshot,
and the Show all button label only when its mode changes; nothing grows per frame.

## Files

- `UI/Data/ProgressionData.cs` (Enl calculator fields on `EnlPlanner`, `EnlLevelRow`, `EnlMaterialRow`,
  `EnlChainRow`, `EnlCalculatorView` limits, `ProgressionSource.ParseJson`)
- `UI/Data/EnlRowConverters.cs` (new)
- `ImGui/Panels/SkillsFace.Progression.cs`
- Tests: `tools/EnlCalculatorTests` (new; the sample JSON follows `AppendEnlCalculatorJson`'s shape)

## Not verified

No live client and no live RynthILT data: how the tables render (column widths, wrapping at 300, the
scrolling table) and the real numbers were not seen in game. The parse is tested against a sample built from
the producer's code, not captured output.
