# Creature sizes

The monster HUD anchors a plate above a creature's head, so it needs the creature's height. The client keeps
that in memory (CPhysicsObj scale, CPartArray, CSetup height and selection sphere), but the engine does not map
those layouts, so the HUD used to guess from the creature type and name. This builds the real numbers from data.

A creature's top = max(model height, selection sphere top) x its default scale. The model (Setup, 0x02xxxxxx)
comes from `client_portal.dat`; the weenie's setup and default scale come from the ACE world database.

Output: `src/RynthCore.Engine/ImGui/MonsterHud/CreatureSizes.g.cs`, used through `CreatureSizes.TryGetTop(wcid)`.

## Regenerate

```powershell
# 1. creature-like weenies with a setup (add the Aelrynth custom weenie .sql exports after the world dump)
python parse_world.py C:\Projects\ACE-World-Database\ACE-World-Database-v0.9.293.sql -o out

# 2. each setup's height / radius / selection sphere / sorting sphere, from a folder holding client_portal.dat
dotnet run --project SetupDims -c Release -- <dat dir> out\setups.json out\setups_dims.json

# 3. the table
python gen_cs.py out\creatures.json out\setups_dims.json ..\..\src\RynthCore.Engine\ImGui\MonsterHud\CreatureSizes.g.cs
```

Pass full Windows paths to step 2 (it is a Windows program). A creature with no row (a custom weenie that was not in
the input, or a model with no height) falls back to the HUD's old estimate by creature type and name.

`tools/CreatureSizesTests` checks the lookup against the generated table.
