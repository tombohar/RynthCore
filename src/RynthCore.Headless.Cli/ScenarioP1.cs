// ============================================================================
//  rynth-headless - ScenarioP1.cs
//  Live check of the P1 world model and actions (run with --scenario p1, an admin
//  character on a test server: it uses @create). Each step prints PASS / FAIL / SKIP:
//
//    world     PlayerDescription applied, attributes/vitals/skills, pack list matches objects
//    identify  appraise a pack item -> identify reply
//    use       open a side pack -> ViewContents
//    cast      wield a caster if needed, Magic mode, cast a beneficial self spell -> UseDone
//              plus an enchantment (or a fizzle error: the cast was processed either way)
//    attack    @create a monster, Melee mode (caster put away), attack -> AttackDone + combat notices
//    pickup    @create a pyreal on the ground, pick it up -> walk over, lands in the pack
//    give      give the pyreal to the monster -> the server answers (monsters refuse)
//    drop      drop it -> it leaves the pack
//  @create needs open ground in front of the player, so the scenario first teleports to
//  an open spot outside Holtburg (@teleloc) and at the end teleports back to where the
//  character was. The caster is wielded again and the mode is back to NonCombat.
// ============================================================================

using RynthCore.Headless.Dat;
using RynthCore.Headless.Messages;
using RynthCore.Headless.Net;
using RynthCore.Headless.Session;
using RynthCore.Headless.World;

namespace RynthCore.Headless.Cli;

internal sealed class ScenarioP1
{
    private readonly ClientSession _s;
    private readonly UdpLink _link;
    private readonly Action<string> _out;
    private readonly List<(string Step, string Result)> _results = new();
    private WorldState W => _s.World;
    private uint Me => _s.PlayerId;

    private uint _lastUseDone = uint.MaxValue, _lastAttackDone = uint.MaxValue;
    private readonly List<string> _errors = new();
    private readonly List<CombatNotice> _combat = new();
    private readonly List<Enchantment> _enchants = new();
    private readonly List<(uint Id, bool Ok)> _identified = new();
    private readonly List<uint> _opened = new();
    private readonly List<WorldObject> _created = new();

    public ScenarioP1(ClientSession s, UdpLink link, Action<string> output)
    {
        _s = s;
        _link = link;
        _out = output;
        W.UseDone += e => { _lastUseDone = e; _out($"  << UseDone 0x{e:X}"); };
        W.AttackDone += e => { _lastAttackDone = e; _out($"  << AttackDone 0x{e:X}"); };
        W.Error += (c, t) => { _errors.Add($"0x{c:X} {t}"); _out($"  << WeenieError 0x{c:X} {t}"); };
        W.InventoryFailed += (i, e) => { _errors.Add($"save failed 0x{i:X8} 0x{e:X}"); _out($"  << InventoryServerSaveFailed 0x{i:X8} err 0x{e:X}"); };
        W.TransientMessage += t => _out($"  << transient: {t}");
        W.Combat += c => { _combat.Add(c); _out($"  << combat 0x{c.EventType:X}: {c.Name} dmg {c.Damage} hp {c.HealthPercent:P0}"); };
        W.EnchantmentAdded += e => { _enchants.Add(e); _out($"  << enchantment +{e.SpellId} (layer {e.Layer}, {e.Duration:F0}s)"); };
        W.Identified += (id, ok) => _identified.Add((id, ok));
        W.ContainerOpened += c => _opened.Add(c);
        W.ObjectCreated += o => _created.Add(o);
    }

    public bool Run()
    {
        SpellTable? spells = null;
        string? ac = DatFile.FindAcFolder();
        if (ac != null) spells = SpellTable.Load(ac, out _);
        _out($"spell table: {(spells != null ? spells.ById.Count + " spells" : "not found")}");

        StepWorld();
        WorldPosition? home = W.PlayerObject?.Position;
        StepTeleport(OpenGround, "teleport");
        StepIdentify();
        StepUse();
        uint caster = StepCast(spells);
        uint monster = SpawnNear(o => o.IsCreature && o.Name == "Drudge Skulker", "drudgeskulker");
        uint pyreal = StepPickup();
        StepGive(monster, pyreal);
        StepDrop(pyreal);
        StepAttack(caster, monster);
        Restore(caster);
        if (home is { } h)
            StepTeleport($"0x{h.Cell:X8} {h.X:R} {h.Y:R} {h.Z:R} {h.Qw:R} {h.Qx:R} {h.Qy:R} {h.Qz:R}", "return");

        _out("");
        _out("P1 scenario results:");
        foreach (var (step, result) in _results) _out($"  {step,-9} {result}");
        return _results.All(r => !r.Result.StartsWith("FAIL"));
    }

    // ── steps ───────────────────────────────────────────────────────────────

    private void StepWorld()
    {
        Wait(() => W.Player.DescriptionReceived && W.PlayerObject != null, 5000);
        PlayerState p = W.Player;
        if (!p.DescriptionReceived) { Result("world", "FAIL no PlayerDescription"); return; }
        _out($"attributes: {string.Join(" ", Enumerable.Range(1, 6).Select(a => p.BaseAttribute((uint)a)))}");
        _out($"vitals: H {p.CurrentVital(Vital.Health)}/{p.BaseMaxVital(Vital.Health)} S {p.CurrentVital(Vital.Stamina)}/{p.BaseMaxVital(Vital.Stamina)} M {p.CurrentVital(Vital.Mana)}/{p.BaseMaxVital(Vital.Mana)} (base max)");
        int pack = p.MainPackOrder.Count + p.SidePackOrder.Count;
        int matched = p.MainPackOrder.Concat(p.SidePackOrder).Count(id => W.Get(id)?.ContainerId == Me);
        int carried = W.Inventory().Count();
        _out($"skills {p.SkillTable.Count}, spells {p.SpellBook.Count}, enchantments {p.ActiveEnchantments.Count}, pack {pack} (matched {matched}), equipped {p.EquippedItems.Count}, carried total {carried}, objects {W.Objects.Count}");
        bool ok = p.AttributeTable.Count == 6 && p.SkillTable.Count > 0 && p.CurrentVital(Vital.Health) > 0 && matched == pack && pack > 0;
        Result("world", ok ? $"PASS {pack} pack entries, {p.EquippedItems.Count} equipped, {carried} carried" : "FAIL model inconsistent");
    }

    private const string OpenGround = "0xA9B40029 133.603592 17.391838 94.005005 1 0 0 0";

    private void StepTeleport(string where, string step)
    {
        uint lb = Convert.ToUInt32(where.Split(' ')[0], 16) >> 16;
        WorldPosition? before = W.PlayerObject?.Position;
        if (before is { } b0 && b0.Cell >> 16 == lb && step == "teleport") { Result(step, "PASS already there"); return; }
        _out($">> @teleloc {where}");
        _s.Say($"@teleloc {where}");
        bool moved = Wait(() => W.PlayerObject?.Position is { } p && p.Cell >> 16 == lb, 15000);
        Wait(() => false, 3000);   // let the new landblock's objects arrive
        Result(step, moved ? $"PASS now at 0x{W.PlayerObject!.Position!.Value.Cell:X8}, {W.Objects.Count} objects known" : "FAIL no position update");
    }

    private void StepIdentify()
    {
        uint item = W.Player.MainPackOrder.FirstOrDefault();
        if (item == 0) { Result("identify", "SKIP no pack item"); return; }
        _out($">> identify {W.Get(item)}");
        _s.Identify(item);
        bool got = Wait(() => _identified.Any(x => x.Id == item), 5000);
        WorldObject? o = W.Get(item);
        Result("identify", got && o?.Identified == true
            ? $"PASS {o.Name}: {o.Ints().Count} ints, {o.Strings().Count} strings"
            : got ? "FAIL reply without success" : "FAIL no reply");
    }

    private void StepUse()
    {
        uint pack = W.Player.SidePackOrder.FirstOrDefault(id => W.Get(id)?.IsContainer == true);
        if (pack == 0) { Result("use", "SKIP no side pack"); return; }
        _out($">> use {W.Get(pack)}");
        _lastUseDone = uint.MaxValue;
        _s.Use(pack);
        bool answered = Wait(() => _lastUseDone != uint.MaxValue || _opened.Contains(pack), 5000);
        Wait(() => _opened.Contains(pack), 500);
        int inside = W.Contents(pack).Count();
        Result("use", answered && (_lastUseDone == 0 || _opened.Contains(pack))
            ? $"PASS used {W.Get(pack)?.Name}: UseDone 0x{_lastUseDone:X}{(_opened.Contains(pack) ? $", opened ({inside} items)" : "")}"
            : $"FAIL UseDone {(_lastUseDone == uint.MaxValue ? "never came" : "0x" + _lastUseDone.ToString("X"))}");
    }

    /// <summary>Returns the caster that was wielded before the scenario (or that we wielded), 0 if none.</summary>
    private uint StepCast(SpellTable? spells)
    {
        WorldObject? caster = WieldedCaster();
        uint originalCaster = caster?.Id ?? 0;
        if (caster == null)
        {
            caster = W.Inventory().FirstOrDefault(o => o.Desc.ValidLocations is { } v && (v & 0x1000000) != 0);
            if (caster == null) { Result("cast", "SKIP no caster"); return 0; }
            _out($">> wield {caster}");
            _s.Wield(caster.Id, 0x1000000);
            Wait(() => W.Get(caster.Id)?.WielderId == Me, 5000);
        }

        if (spells == null) { Result("cast", "SKIP no spell table"); return originalCaster != 0 ? originalCaster : caster.Id; }
        // A cheap beneficial self enchantment the player knows.
        SpellInfo? spell = W.Player.SpellBook.Select(spells.Get).Where(s => s != null && s.Beneficial && s.IsEnchantment && s.SelfTargeted)
            .OrderBy(s => s!.Power).ThenBy(s => s!.BaseMana).FirstOrDefault();
        if (spell == null) { Result("cast", "SKIP no known self buff"); return originalCaster != 0 ? originalCaster : caster.Id; }

        _out(">> combat mode Magic");
        _s.SetCombatMode(CombatMode.Magic);
        Wait(() => CombatModeIs(CombatMode.Magic), 5000);
        _out($">> cast {spell.Name} ({spell.Id}) on self");
        _lastUseDone = uint.MaxValue;
        int enchBefore = _enchants.Count, errBefore = _errors.Count;
        _s.Cast(spell.Id, Me);
        bool done = Wait(() => _lastUseDone != uint.MaxValue, 20000);
        Wait(() => _enchants.Count > enchBefore, 1000);
        bool buffed = _enchants.Skip(enchBefore).Any(e => e.SpellId == spell.Id);
        string errs = string.Join("; ", _errors.Skip(errBefore));
        Result("cast", done && (buffed || _lastUseDone == 0 || errs.Length > 0)
            ? $"PASS {spell.Name}: {(buffed ? "enchantment applied" : "no enchantment")}, UseDone 0x{_lastUseDone:X}{(errs.Length > 0 ? ", " + errs : "")}"
            : $"FAIL UseDone {(done ? "0x" + _lastUseDone.ToString("X") : "never came")}");
        return originalCaster != 0 ? originalCaster : caster.Id;
    }

    private void StepAttack(uint caster, uint monster)
    {
        if (monster == 0 || W.Get(monster) == null) { Result("attack", "SKIP could not @create a monster"); return; }
        _out($">> target {W.Get(monster)} at {W.Distance(Me, monster):F1} m");
        _s.QueryHealth(monster);

        // Melee is refused with a caster wielded: put it in the pack first.
        if (caster != 0 && W.Get(caster)?.WielderId == Me)
        {
            _s.SetCombatMode(CombatMode.NonCombat);
            Wait(() => CombatModeIs(CombatMode.NonCombat), 3000);
            _out(">> put the caster away");
            _s.MoveItem(caster, Me, 0);
            Wait(() => W.Get(caster)?.WielderId == 0, 5000);
        }
        _out(">> combat mode Melee");
        _s.SetCombatMode(CombatMode.Melee);
        Wait(() => CombatModeIs(CombatMode.Melee), 5000);

        int notices = _combat.Count, swings = 0, dones = 0;
        for (int i = 0; i < 8 && W.Get(monster) != null && !Killed(); i++)
        {
            _lastAttackDone = uint.MaxValue;
            _out($">> melee attack #{i + 1}");
            _s.MeleeAttack(monster, AttackHeight.Medium, 1.0f);
            swings++;
            if (Wait(() => _lastAttackDone != uint.MaxValue, 10000)) dones++;
            Wait(() => false, 300);
        }
        bool Killed() => _combat.Skip(notices).Any(c => c.EventType == WorldEvent.KillerNotification);
        int got = _combat.Count - notices;
        Result("attack", dones > 0 && got > 0
            ? $"PASS {swings} swings, {dones} AttackDone, {got} combat notices{(Killed() ? ", killed it" : "")}"
            : $"FAIL swings {swings}, AttackDone {dones}, notices {got}");
        _s.SetCombatMode(CombatMode.NonCombat);
        Wait(() => CombatModeIs(CombatMode.NonCombat), 3000);
    }

    private uint StepPickup()
    {
        uint pyreal = SpawnNear(o => o.WeenieClassId == 273 && o.ContainerId == 0 && o.Position != null, "273");
        if (pyreal == 0) { Result("pickup", "SKIP could not @create a pyreal"); return 0; }
        float? d0 = W.Distance(Me, pyreal);
        _out($">> pick up {W.Get(pyreal)} at {d0:F1} m");
        _s.PickUp(pyreal);
        bool got = Wait(() => W.Get(pyreal)?.ContainerId == Me, 20000);
        Result("pickup", got ? $"PASS picked up from {d0:F1} m (walked {(_walked ? "yes" : "no")})" : "FAIL not in the pack");
        return got ? pyreal : 0;
    }

    private bool _walked;

    private void StepGive(uint monster, uint item)
    {
        if (item == 0) { Result("give", "SKIP nothing to give"); return; }
        uint target = monster != 0 && W.Get(monster) != null ? monster : 0;
        if (target == 0) { Result("give", "SKIP no living target"); return; }
        bool answered = false;
        int errBefore = _errors.Count;
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            // Busy (0x1D) right after the pickup: wait for the animation and ask again.
            errBefore = _errors.Count;
            _out($">> give {W.Get(item)} to {W.Get(target)}{(attempt > 1 ? $" (attempt {attempt})" : "")}");
            _s.Give(target, item, 1);
            answered = Wait(() => _errors.Count > errBefore || W.Get(item)?.ContainerId == target, 20000);
            Wait(() => false, 300);
            if (!_errors.Skip(errBefore).Any(e => e.StartsWith("0x1D"))) break;
            Wait(() => false, 1000);
        }
        bool given = W.Get(item)?.ContainerId == target;
        Result("give", answered
            ? $"PASS server answered: {(given ? "accepted" : "refused (" + string.Join("; ", _errors.Skip(errBefore)) + ")")}"
            : "FAIL no answer");
    }

    private void StepDrop(uint item)
    {
        if (item == 0 || W.Get(item)?.ContainerId != Me) { Result("drop", "SKIP nothing to drop"); return; }
        bool dropped = false;
        for (int attempt = 1; attempt <= 5 && !dropped; attempt++)
        {
            // Right after a pickup the player is still busy (the pickup animation's return): retry.
            int errBefore = _errors.Count;
            _out($">> drop {W.Get(item)}{(attempt > 1 ? $" (attempt {attempt})" : "")}");
            _s.Drop(item);
            dropped = Wait(() => (W.Get(item) is { } o && o.ContainerId == 0 && !W.Player.MainPackOrder.Contains(item)) || _errors.Count > errBefore, 10000)
                      && W.Get(item) is { } d && d.ContainerId == 0;
            if (!dropped) Wait(() => false, 1000);
        }
        Result("drop", dropped ? "PASS left the pack" : "FAIL still carried");
    }

    private void Restore(uint caster)
    {
        if (caster != 0 && W.Get(caster) is { } c && c.WielderId != Me)
        {
            _out($">> wield {c} again");
            _s.Wield(caster, 0x1000000);
            Wait(() => W.Get(caster)?.WielderId == Me, 5000);
        }
        _s.SetCombatMode(CombatMode.NonCombat);
        Wait(() => false, 1000);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private WorldObject? WieldedCaster()
        => W.Objects.Values.FirstOrDefault(o => o.WielderId == Me && (o.WieldedLocation & 0x1000000) != 0);

    private bool CombatModeIs(uint mode) => W.PlayerObject is { } me && me.TryGetInt(40, out int m) && m == mode;

    /// <summary>@creates something and returns the first new object matching, trying each name in turn.</summary>
    private uint SpawnNear(Func<WorldObject, bool> match, params string[] names)
    {
        foreach (string name in names)
        {
            int before = _created.Count;
            _out($">> @create {name}");
            _s.Say($"@create {name}");
            WorldObject? found = null;
            Wait(() => (found = _created.Skip(before).FirstOrDefault(match)) != null, 5000);
            if (found != null) return found.Id;
        }
        return 0;
    }

    private void Result(string step, string result)
    {
        _results.Add((step, result));
        _out($"== {step}: {result}");
    }

    /// <summary>Pumps the session until the condition holds or the timeout passes.</summary>
    private bool Wait(Func<bool> done, int timeoutMs)
    {
        long end = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < end && !_s.Finished)
        {
            if (done()) return true;
            _link.Wait(15);
            _s.Tick();
            if (_s.Moving) _walked = true;
        }
        return done();
    }
}

internal static class WorldObjectDebug
{
    public static IReadOnlyCollection<uint> Ints(this WorldObject o)
        => Enumerable.Range(0, 1000).Where(k => o.TryGetInt((uint)k, out _)).Select(k => (uint)k).ToList();
    public static IReadOnlyCollection<uint> Strings(this WorldObject o)
        => Enumerable.Range(0, 100).Where(k => o.TryGetString((uint)k, out _)).Select(k => (uint)k).ToList();
}
