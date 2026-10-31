// ============================================================================
//  PlayerTests - the PLAYER slots (vitals, attributes, skills, spells, enchantments,
//  server time, pose) against the replayed test session, checked against the portal
//  dat formulas and against synthetic enchantment / vitae events.
// ============================================================================

using System.Buffers.Binary;
using RynthCore.Headless.Dat;
using RynthCore.Headless.Host;
using RynthCore.Headless.Messages;
using RynthCore.Headless.World;

namespace HeadlessHostTests;

internal static unsafe class PlayerTests
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Register() => Suites.Add("player", Run, order: 20);

    private const uint Me = 0x50000001;

    private static void Run()
    {
        Fixture f = Fixture.Shared;
        using var _ = f.Context.Enter();
        PlayerState p = f.Session.World.Player;
        SkillTable? table = SkillTable.Shared;
        T.Check(table != null, "portal dat skill table loads");
        if (table == null) return;

        DatFacts(table);
        Vitals(f, p, table);
        Attributes(f, p);
        Skills(f, p, table);
        SkillEnchantment(f, p);
        AttributeEnchantment(f, p, table);
        Vitae(f);
        Spells(f, p);
        Enchantments(f, p);
        ServerTime(f);
        Pose(f);
        EdgeCases(f);
    }

    // ── the API through its function pointers ───────────────────────────────

    private static bool Vitals(Fixture f, out uint h, out uint mh, out uint s, out uint ms, out uint m, out uint mm)
    {
        uint a = 0, b = 0, c = 0, d = 0, e = 0, g = 0;
        int ok = ((delegate* unmanaged[Cdecl]<uint*, uint*, uint*, uint*, uint*, uint*, int>)f.Api->GetPlayerVitalsFn)(&a, &b, &c, &d, &e, &g);
        h = a; mh = b; s = c; ms = d; m = e; mm = g;
        return ok == 1;
    }

    private static bool BaseVitals(Fixture f, out uint h, out uint s, out uint m)
    {
        uint a = 0, b = 0, c = 0;
        int ok = ((delegate* unmanaged[Cdecl]<uint*, uint*, uint*, int>)f.Api->GetPlayerBaseVitalsFn)(&a, &b, &c);
        h = a; s = b; m = c;
        return ok == 1;
    }

    private static uint? Attr2nd(Fixture f, uint obj, uint stype)
    {
        uint v = 0;
        return ((delegate* unmanaged[Cdecl]<uint, uint, uint*, int>)f.Api->GetObjectAttribute2ndBaseLevelFn)(obj, stype, &v) == 1 ? v : null;
    }

    private static uint? Attr(Fixture f, uint obj, uint stype, int raw)
    {
        uint v = 0;
        return ((delegate* unmanaged[Cdecl]<uint, uint, int, uint*, int>)f.Api->GetObjectAttributeFn)(obj, stype, raw, &v) == 1 ? v : null;
    }

    private static (int Buffed, int Training)? Skill(Fixture f, uint obj, uint skill)
    {
        int b = 0, t = 0;
        return ((delegate* unmanaged[Cdecl]<uint, uint, int*, int*, int>)f.Api->GetObjectSkillFn)(obj, skill, &b, &t) == 1 ? (b, t) : null;
    }

    private static int? SkillLevel(Fixture f, uint obj, uint skill, int raw)
    {
        int v = 0;
        return ((delegate* unmanaged[Cdecl]<uint, uint, int, int*, int>)f.Api->GetObjectSkillBuffedFn)(obj, skill, raw, &v) == 1 ? v : null;
    }

    private static float Vitae(Fixture f, uint id) => ((delegate* unmanaged[Cdecl]<uint, float>)f.Api->GetVitaeFn)(id);

    private static double ServerTimeNow(Fixture f) => ((delegate* unmanaged[Cdecl]<double>)f.Api->GetServerTimeFn)();

    private static (uint[] Ids, double[] Expiry, int Result) ReadEnchantments(Fixture f, int max = 512)
    {
        uint[] ids = new uint[max];
        double[] exp = new double[max];
        int n;
        fixed (uint* pi = ids)
        fixed (double* pe = exp)
            n = ((delegate* unmanaged[Cdecl]<uint*, double*, int, int>)f.Api->ReadPlayerEnchantmentsFn)(pi, pe, max);
        return (ids[..Math.Max(0, n)], exp[..Math.Max(0, n)], n);
    }

    // ── server messages ─────────────────────────────────────────────────────

    private static byte[] GameEvent(uint eventType, byte[] body)
    {
        var m = new byte[16 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(m, 0xF7B0);
        BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan(4), Me);
        BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan(8), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan(12), eventType);
        body.CopyTo(m, 16);
        return m;
    }

    private static byte[] EnchantmentBody(ushort spell, ushort layer, ushort category, uint power, double start, double duration,
        uint type, uint key, float value)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(spell); w.Write(layer); w.Write(category); w.Write((ushort)0);
        w.Write(power); w.Write(start); w.Write(duration); w.Write(Me);
        w.Write(0f); w.Write(0f); w.Write(0.0);
        w.Write(type); w.Write(key); w.Write(value);
        return ms.ToArray();
    }

    private static void AddEnchantment(Fixture f, ushort spell, ushort layer, ushort category, uint power, double start, double duration,
        uint type, uint key, float value)
        => f.Receive(GameEvent(0x02C2, EnchantmentBody(spell, layer, category, power, start, duration, type, key, value)));

    private static void RemoveEnchantment(Fixture f, ushort spell, ushort layer)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(b, spell);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), layer);
        f.Receive(GameEvent(0x02C3, b));
    }

    private const uint SingleStat = 0x1000, Multiplicative = 0x4000, Additive = 0x8000, Beneficial = 0x2000000;

    // ── expectations computed here from the dat ─────────────────────────────

    private static uint BaseAttr(PlayerState p, uint a) => p.AttributeTable.TryGetValue(a, out AttributeInfo i) ? i.Init + i.Ranks : 0;

    private static uint Formula(SkillFormula fm, Func<uint, uint> attr)
    {
        if (fm.X == 0) return 0;
        uint t = attr(fm.Attr1) + (fm.Attr2 != 0 ? attr(fm.Attr2) : 0);
        return fm.Divisor > 1 ? (uint)Math.Round((double)t / fm.Divisor, MidpointRounding.AwayFromZero) : t;
    }

    private static int PlayerInt(Fixture f, uint key)
        => f.Session.World.PlayerObject is { } o && o.TryGetInt(key, out int v) ? v : 0;

    // ── checks ──────────────────────────────────────────────────────────────

    private static void DatFacts(SkillTable t)
    {
        T.Eq(t.MaxHealth.ToString(), "a2 / 2", "dat: max health = Endurance / 2");
        T.Eq(t.MaxStamina.ToString(), "a2 / 1", "dat: max stamina = Endurance");
        T.Eq(t.MaxMana.ToString(), "a6 / 1", "dat: max mana = Self");
        T.Eq(t.Get(6)?.Formula.ToString(), "(a3 + a4) / 3", "dat: Melee Defense = (Quickness + Coordination) / 3");
        T.Eq(t.Get(15)?.Formula.ToString(), "(a6 + a5) / 7", "dat: Magic Defense = (Self + Focus) / 7");
        T.Eq(t.Get(34)?.Formula.ToString(), "(a5 + a6) / 4", "dat: War Magic = (Focus + Self) / 4");
        T.Eq(t.Get(6)?.Name, "Melee Defense", "dat: skill 6 name");
        T.Check(t.Skills.Count >= 45, $"dat: {t.Skills.Count} skills");
    }

    private static void Vitals(Fixture f, PlayerState p, SkillTable t)
    {
        T.Check(Vitals(f, out uint h, out uint mh, out uint s, out uint ms, out uint m, out uint mm), "GetPlayerVitals succeeds");
        T.Check(BaseVitals(f, out uint bh, out uint bs, out uint bm), "GetPlayerBaseVitals succeeds");
        T.Info($"vitals {h}/{mh} {s}/{ms} {m}/{mm}; base max {bh} {bs} {bm}; attributes {string.Join(" ", Enumerable.Range(1, 6).Select(a => BaseAttr(p, (uint)a)))}");
        T.Eq(h, p.CurrentVital(Vital.Health), "current health = PlayerState");
        T.Eq(s, p.CurrentVital(Vital.Stamina), "current stamina = PlayerState");
        T.Eq(m, p.CurrentVital(Vital.Mana), "current mana = PlayerState");
        T.Check(mh >= bh && ms >= bs && mm >= bm, "buffed max >= base max");

        int enlightenment = PlayerInt(f, 390);
        uint[] expected = new uint[4];
        for (uint v = 1; v <= 3; v++)
        {
            VitalInfo vi = p.Vitals[v];
            expected[v] = Formula(t.VitalFormula(v), a => BaseAttr(p, a)) + vi.Init + vi.Ranks;
        }
        expected[1] += (uint)(enlightenment * 2);
        T.Eq(bh, expected[1], "base max health = End/2 + init + ranks (dat formula)");
        T.Eq(bs, expected[2], "base max stamina = End + init + ranks");
        T.Eq(bm, expected[3], "base max mana = Self + init + ranks");
        T.Eq(Attr2nd(f, Me, 1), (uint?)bh, "Attribute2ndBaseLevel(MaxHealth) = base max health");
        T.Eq(Attr2nd(f, Me, 3), (uint?)bs, "Attribute2ndBaseLevel(MaxStamina) = base max stamina");
        T.Eq(Attr2nd(f, Me, 5), (uint?)bm, "Attribute2ndBaseLevel(MaxMana) = base max mana");
        T.Eq(Attr2nd(f, Me, 2), (uint?)h, "Attribute2ndBaseLevel(Health) = current health");
        T.Check(Attr2nd(f, 0x12345678, 1) == null && Attr2nd(f, Me, 7) == null && Attr2nd(f, Me, 0) == null, "Attribute2ndBaseLevel: other object / bad stype fail");

        // A multiplicative max-health buff (Asheron's Benediction style) and an additive one.
        uint before = mh;
        AddEnchantment(f, 4100, 1, 0xFFE0, 999, 0, 600, SingleStat | Multiplicative | 0x2 | Beneficial, 1, 1.10f);
        AddEnchantment(f, 4101, 1, 0xFFE1, 999, 0, 600, SingleStat | Additive | 0x2 | Beneficial, 1, 7f);
        Vitals(f, out _, out uint mh2, out _, out uint ms2, out _, out _);
        T.Eq(mh2, (uint)Math.Round(before * 1.10f + 7f, MidpointRounding.AwayFromZero), "max health: * 1.10 (multiplicative) then + 7 (additive)");
        T.Eq(ms2, ms, "max stamina unchanged by a max-health buff");
        BaseVitals(f, out uint bh2, out _, out _);
        T.Eq(bh2, bh, "base max health ignores enchantments");
        RemoveEnchantment(f, 4100, 1);
        RemoveEnchantment(f, 4101, 1);
        Vitals(f, out _, out uint mh3, out _, out _, out _, out _);
        T.Eq(mh3, before, "max health back after the buffs are removed");
    }

    private static void Attributes(Fixture f, PlayerState p)
    {
        var line = new List<string>();
        for (uint a = 1; a <= 6; a++)
        {
            uint? raw = Attr(f, Me, a, 1), buffed = Attr(f, Me, a, 0);
            T.Eq(raw, (uint?)BaseAttr(p, a), $"attribute {a} raw = init + ranks");
            T.Check(buffed != null && buffed >= raw, $"attribute {a} buffed ({buffed}) >= raw ({raw})");
            line.Add($"{a}:{raw}/{buffed}");
        }
        T.Info("attributes raw/buffed " + string.Join(" ", line));
        T.Check(Attr(f, Me, 0, 0) == null && Attr(f, Me, 7, 0) == null && Attr(f, 0x12345678, 1, 0) == null, "attribute: bad stype / other object fail");
    }

    private static void Skills(Fixture f, PlayerState p, SkillTable t)
    {
        T.Eq(p.SkillTable.Count, 38, "38 skills");
        int checkedBase = 0;
        var lines = new List<string>();
        uint[] attrBuffed = new uint[7];
        for (uint a = 1; a <= 6; a++) attrBuffed[a] = Attr(f, Me, a, 0) ?? 0;
        bool augs = PlayerInt(f, 365) != 0 || PlayerInt(f, 300) != 0 || PlayerInt(f, 301) != 0 || PlayerInt(f, 302) != 0
            || PlayerInt(f, 390) != 0 || PlayerInt(f, 326) != 0 || PlayerInt(f, 344) != 0;
        T.Info($"augmentation properties present: {augs}");
        foreach (SkillInfo si in p.SkillTable.Values.OrderBy(s => s.Id))
        {
            var r = Skill(f, Me, si.Id);
            int? raw = SkillLevel(f, Me, si.Id, 1), buffed = SkillLevel(f, Me, si.Id, 0);
            T.Check(r != null && raw != null && buffed != null, $"skill {si.Id} readable");
            if (r == null || raw == null || buffed == null) continue;
            T.Eq(r.Value.Training, (int)si.Training, $"skill {si.Id} training class");
            T.Eq(r.Value.Buffed, buffed.Value, $"skill {si.Id}: GetObjectSkill = GetObjectSkillBuffed(raw 0)");
            T.Check(buffed >= raw, $"skill {si.Id} buffed {buffed} >= base {raw}");
            SkillBaseInfo? sb = t.Get(si.Id);
            bool usable = si.Training >= 2 || (si.Training == 1 && sb?.MinLevel == 1);
            if (!augs && sb != null)
            {
                uint expectBase = (usable ? Formula(sb.Formula, a => BaseAttr(p, a)) : 0) + si.Init + si.Ranks;
                T.Eq(raw.Value, (int)expectBase, $"skill {si.Id} ({sb.Name}) base = formula + init + ranks");
                checkedBase++;
            }
            if (lines.Count < 8 || si.Id is 6 or 15 or 24 or 34)
                lines.Add($"{si.Id} {sb?.Name} sac{si.Training} init{si.Init} ranks{si.Ranks} -> {raw}/{buffed}");
        }
        foreach (string l in lines.Distinct()) T.Info(l);
        T.Check(checkedBase >= 30 || augs, $"base formula checked on {checkedBase} skills");
        T.Check(Skill(f, Me, 9999) == null, "unknown skill fails");
        T.Check(Skill(f, 0x12345678, 6) == null && SkillLevel(f, 0x12345678, 6, 0) == null, "other object's skill fails");

        // Melee defense, trained, specialised?
        SkillInfo md = p.SkillTable.TryGetValue(6, out SkillInfo x) ? x : default;
        T.Check(md.Training >= 2, "Melee Defense trained or specialized");
        T.Eq(SkillLevel(f, Me, 6, 1), (int?)(int)(Formula(t.Get(6)!.Formula, a => BaseAttr(p, a)) + md.Init + md.Ranks), "Melee Defense base = (Coord + Quick)/3 + init + ranks");
    }

    private static void SkillEnchantment(Fixture f, PlayerState p)
    {
        uint skill = p.SkillTable.Values.Where(s => s.Training >= 2).Select(s => s.Id).OrderBy(i => i).First();
        int baseBefore = SkillLevel(f, Me, skill, 1)!.Value;
        int before = SkillLevel(f, Me, skill, 0)!.Value;

        AddEnchantment(f, 4102, 1, 0xFFE2, 999, 0, 600, SingleStat | Additive | 0x10 | Beneficial, skill, 25f);
        int after = SkillLevel(f, Me, skill, 0)!.Value;
        T.Eq(after - before, 25, $"skill {skill}: +25 additive enchantment adds 25");
        T.Eq(SkillLevel(f, Me, skill, 1), (int?)baseBefore, $"skill {skill}: base unchanged by the enchantment");
        T.Eq(Skill(f, Me, skill)?.Buffed, (int?)after, $"skill {skill}: GetObjectSkill sees it");

        // Same category, lower power: does not stack (top layer only).
        AddEnchantment(f, 4103, 2, 0xFFE2, 10, 0, 600, SingleStat | Additive | 0x10 | Beneficial, skill, 50f);
        T.Eq(SkillLevel(f, Me, skill, 0), (int?)after, $"skill {skill}: a weaker spell in the same category is ignored");
        // Same category, higher power: replaces.
        AddEnchantment(f, 4104, 3, 0xFFE2, 2000, 0, 600, SingleStat | Additive | 0x10 | Beneficial, skill, 40f);
        T.Eq(SkillLevel(f, Me, skill, 0), (int?)(before + 40), $"skill {skill}: the stronger spell in the category wins");
        RemoveEnchantment(f, 4103, 2);
        RemoveEnchantment(f, 4104, 3);

        // Multiplicative (applies before the additive).
        AddEnchantment(f, 4105, 1, 0xFFE3, 999, 0, 600, SingleStat | Multiplicative | 0x10 | Beneficial, skill, 1.5f);
        int mult = SkillLevel(f, Me, skill, 0)!.Value;
        T.Check(mult > after, $"skill {skill}: * 1.5 raises it ({after} -> {mult})");
        // A different skill's key doesn't apply.
        AddEnchantment(f, 4106, 1, 0xFFE4, 999, 0, 600, SingleStat | Additive | 0x10, skill + 1000, 99f);
        T.Eq(SkillLevel(f, Me, skill, 0), (int?)mult, $"skill {skill}: another skill's enchantment doesn't apply");
        RemoveEnchantment(f, 4102, 1);
        RemoveEnchantment(f, 4105, 1);
        RemoveEnchantment(f, 4106, 1);
        T.Eq(SkillLevel(f, Me, skill, 0), (int?)before, $"skill {skill}: back to {before} when removed");
    }

    private static void AttributeEnchantment(Fixture f, PlayerState p, SkillTable t)
    {
        // +20 Quickness: buffed Quickness +20 and Melee Defense ((Coord + Quick)/3) up by about 20/3.
        uint q0 = Attr(f, Me, 3, 0)!.Value;
        int md0 = SkillLevel(f, Me, 6, 0)!.Value;
        int mdBase0 = SkillLevel(f, Me, 6, 1)!.Value;
        AddEnchantment(f, 4107, 1, 0xFFE5, 999, 0, 600, SingleStat | Additive | 0x1 | Beneficial, 3, 30f);
        uint q1 = Attr(f, Me, 3, 0)!.Value;
        T.Eq(q1 - q0, 30u, "Quickness +30 additive");
        T.Eq(Attr(f, Me, 3, 1), (uint?)BaseAttr(p, 3), "Quickness raw unchanged");
        int md1 = SkillLevel(f, Me, 6, 0)!.Value;
        T.Eq(md1 - md0, 10, "Melee Defense follows buffed Quickness (+30 / 3)");
        T.Eq(SkillLevel(f, Me, 6, 1), (int?)mdBase0, "Melee Defense base uses base attributes");
        // Endurance buff raises max health (End/2) and stamina.
        Vitals(f, out _, out uint mh0, out _, out uint ms0, out _, out _);
        AddEnchantment(f, 4108, 1, 0xFFE6, 999, 0, 600, SingleStat | Additive | 0x1 | Beneficial, 2, 40f);
        Vitals(f, out _, out uint mh1, out _, out uint ms1, out _, out _);
        T.Eq(mh1 - mh0, 20u, "max health follows buffed Endurance (+40 / 2)");
        T.Eq(ms1 - ms0, 40u, "max stamina follows buffed Endurance (+40)");
        RemoveEnchantment(f, 4107, 1);
        RemoveEnchantment(f, 4108, 1);
        T.Eq(Attr(f, Me, 3, 0), (uint?)q0, "Quickness back after removal");
    }

    private static void Vitae(Fixture f)
    {
        T.Eq(Vitae(f, Me), 1.0f, "no vitae: 1.0");
        T.Eq(Vitae(f, 0x12345678), 1.0f, "another object's vitae: 1.0");
        Vitals(f, out _, out uint mh0, out _, out uint ms0, out _, out uint mm0);
        BaseVitals(f, out uint bh0, out _, out _);
        int sk0 = SkillLevel(f, Me, 6, 0)!.Value;
        int skBase0 = SkillLevel(f, Me, 6, 1)!.Value;
        int count0 = ReadEnchantments(f).Result;
        AddEnchantment(f, 666, 0, 204, 0, 0, -1, 0x800000 | 0x2 | Multiplicative, 0, 0.9f);
        T.Eq(Vitae(f, Me), 0.9f, "vitae 10%: 0.9");
        Vitals(f, out _, out uint mh1, out _, out uint ms1, out _, out uint mm1);
        T.Eq(mh1, (uint)Math.Round(mh0 * 0.9f, MidpointRounding.AwayFromZero), "vitae scales max health");
        T.Eq(mm1, (uint)Math.Round(mm0 * 0.9f, MidpointRounding.AwayFromZero), "vitae scales max mana");
        T.Check(ms1 < ms0, "vitae lowers max stamina");
        BaseVitals(f, out uint bh1, out _, out _);
        T.Eq(bh1, bh0, "vitae doesn't change base max");
        // ACE / the client: (formula(buffed attributes) + init + ranks) * multipliers * vitae, then the additives.
        // Melee Defense has no multiplicative mods here, so the additive part is what the buffs add on top.
        SkillInfo md = f.Session.World.Player.SkillTable[6];
        uint mdPlain = (uint)Math.Round((Attr(f, Me, 3, 0)!.Value + Attr(f, Me, 4, 0)!.Value) / 3.0, MidpointRounding.AwayFromZero) + md.Init + md.Ranks;
        int mdAdd = sk0 - (int)mdPlain;
        T.Info($"Melee Defense {sk0} = {mdPlain} + {mdAdd} from additive buffs");
        T.Eq(SkillLevel(f, Me, 6, 0), (int?)(int)Math.Round(mdPlain * 0.9f + mdAdd, MidpointRounding.AwayFromZero), "vitae scales buffed skills before the additives");
        T.Eq(SkillLevel(f, Me, 6, 1), (int?)skBase0, "vitae doesn't change base skills");
        T.Eq(ReadEnchantments(f).Result, count0, "vitae is not listed by ReadPlayerEnchantments");
        RemoveEnchantment(f, 666, 0);
        T.Eq(Vitae(f, Me), 1.0f, "vitae removed: 1.0");
    }

    private static void Spells(Fixture f, PlayerState p)
    {
        uint[] buf = new uint[1024];
        int n;
        fixed (uint* b = buf) n = ((delegate* unmanaged[Cdecl]<uint*, int, int>)f.Api->ReadKnownSpellsFn)(b, buf.Length);
        T.Eq(n, 503, "ReadKnownSpells: 503 spells");
        T.Check(buf.Take(Math.Max(0, n)).Contains(24u), "spellbook has Armor Self I (24)");
        T.Check(buf.Take(Math.Max(0, n)).ToHashSet().SetEquals(p.SpellBook), "ReadKnownSpells = the spellbook");
        fixed (uint* b = buf) n = ((delegate* unmanaged[Cdecl]<uint*, int, int>)f.Api->ReadKnownSpellsFn)(b, 10);
        T.Eq(n, 10, "ReadKnownSpells stops at maxCount");
        var known = (delegate* unmanaged[Cdecl]<uint, uint, int>)f.Api->IsSpellKnownFn;
        T.Eq(known(Me, 24), 1, "IsSpellKnown(Armor Self I) = 1");
        uint unknown = Enumerable.Range(1, 7000).Select(i => (uint)i).First(i => !p.SpellBook.Contains(i));
        T.Eq(known(Me, unknown), 0, $"IsSpellKnown({unknown}) = 0");
        T.Eq(known(0x12345678, 24), -1, "IsSpellKnown(another object) = -1");
        T.Eq(known(0, 24), -1, "IsSpellKnown(0) = -1");
    }

    private static void Enchantments(Fixture f, PlayerState p)
    {
        double now = ServerTimeNow(f);
        var (ids, exp, n) = ReadEnchantments(f);
        int expected = p.ActiveEnchantments.Count(e => e.SpellId != 666);
        T.Eq(n, expected, "ReadPlayerEnchantments: every enchantment but vitae");
        T.Info($"{n} enchantments, server time {now:F1}; spells {string.Join(",", ids.Take(12))}...");
        int forever = 0, finite = 0;
        for (int i = 0; i < n; i++)
        {
            Enchantment e = p.ActiveEnchantments.First(x => x.SpellId == ids[i]);
            if (e.Duration <= 0) { forever++; T.Eq(exp[i], double.MaxValue, $"spell {ids[i]} (duration {e.Duration}): no expiry"); }
            else { finite++; T.Check(exp[i] > now && exp[i] <= now + e.Duration + 0.001, $"spell {ids[i]}: expiry {exp[i] - now:F1}s ahead, within its duration {e.Duration}"); }
        }
        T.Info($"{finite} timed, {forever} without duration (item spells)");
        T.Check(finite > 0, "some timed enchantments");
        int armor = Array.IndexOf(ids, 24u);
        T.Check(armor >= 0, "Armor Self I (24, cast during the capture) is active");
        if (armor >= 0)
        {
            Enchantment e = p.ActiveEnchantments.First(x => x.SpellId == 24);
            T.Info($"Armor Self I: start {e.StartTime:F1}, duration {e.Duration}, {exp[armor] - now:F1}s left");
            T.Check(Math.Abs(exp[armor] - now - (e.Duration + e.StartTime)) < 1.0, "Armor Self I expiry = now + duration + (negative) start");
        }

        // A fresh cast: StartTime 0, duration 100 -> expires 100 s from now; time passes; it gets closer.
        AddEnchantment(f, 4109, 1, 0xFFE7, 999, 0, 100, SingleStat | Additive | 0x10 | Beneficial, 6, 1f);
        double t0 = ServerTimeNow(f);
        var r1 = ReadEnchantments(f);
        int k = Array.IndexOf(r1.Ids, 4109u);
        T.Check(k >= 0 && Math.Abs(r1.Expiry[k] - (t0 + 100)) < 0.01, "new enchantment expires at server time + duration");
        f.Now += 30_000;
        double t1 = ServerTimeNow(f);
        T.Check(Math.Abs(t1 - t0 - 30) < 0.001, "server time advances with the clock");
        var r2 = ReadEnchantments(f);
        int k2 = Array.IndexOf(r2.Ids, 4109u);
        T.Check(k2 >= 0 && Math.Abs(r2.Expiry[k2] - r1.Expiry[k]) < 0.001 && r2.Expiry[k2] - t1 < 70.01, "expiry is fixed; remaining shrinks as time passes");
        // A refresh (the server re-sends it with a new StartTime) moves the expiry.
        AddEnchantment(f, 4109, 1, 0xFFE7, 999, 0, 100, SingleStat | Additive | 0x10 | Beneficial, 6, 1f);
        var r3 = ReadEnchantments(f);
        int k3 = Array.IndexOf(r3.Ids, 4109u);
        T.Check(k3 >= 0 && Math.Abs(r3.Expiry[k3] - (t1 + 100)) < 0.01, "a refreshed enchantment expires 100 s from the refresh");
        // An ACE resend of a running one: StartTime -40 (running 40 s).
        AddEnchantment(f, 4110, 1, 0xFFE8, 999, -40, 100, SingleStat | Additive | 0x10 | Beneficial, 6, 1f);
        var r4 = ReadEnchantments(f);
        int k4 = Array.IndexOf(r4.Ids, 4110u);
        T.Check(k4 >= 0 && Math.Abs(r4.Expiry[k4] - (t1 + 60)) < 0.01, "StartTime -40 with duration 100 expires in 60 s");
        RemoveEnchantment(f, 4109, 1);
        RemoveEnchantment(f, 4110, 1);
        T.Eq(ReadEnchantments(f).Result, expected, "removed enchantments are gone");

        // maxCount clips; ReadObjectEnchantments for the player = ReadPlayerEnchantments; others -1.
        T.Eq(ReadEnchantments(f, 5).Result, Math.Min(5, expected), "ReadPlayerEnchantments stops at maxCount");
        uint[] ids2 = new uint[512];
        double[] exp2 = new double[512];
        int n2, n3;
        fixed (uint* pi = ids2)
        fixed (double* pe = exp2)
        {
            n2 = ((delegate* unmanaged[Cdecl]<uint, uint*, double*, int, int>)f.Api->ReadObjectEnchantmentsFn)(Me, pi, pe, 512);
            n3 = ((delegate* unmanaged[Cdecl]<uint, uint*, double*, int, int>)f.Api->ReadObjectEnchantmentsFn)(0x12345678, pi, pe, 512);
        }
        T.Eq(n2, expected, "ReadObjectEnchantments(player) = the player's");
        T.Eq(n3, -1, "ReadObjectEnchantments(other) = -1");
    }

    private static void ServerTime(Fixture f)
    {
        double t = ServerTimeNow(f);
        T.Check(t > 0, $"server time known ({t:F1})");
        T.Check(t >= f.Session.ServerTime, "server time >= the last sync");
        f.Now += 2_500;
        T.Check(Math.Abs(ServerTimeNow(f) - t - 2.5) < 0.001, "server time = last sync + local time since");
    }

    private static void Pose(Fixture f)
    {
        WorldPosition pos = f.Session.World.PlayerObject!.Position!.Value;
        uint cell = 0;
        float x = 0, y = 0, z = 0, qw = 0, qx = 0, qy = 0, qz = 0;
        int ok = ((delegate* unmanaged[Cdecl]<uint*, float*, float*, float*, float*, float*, float*, float*, int>)f.Api->GetPlayerPoseFn)(&cell, &x, &y, &z, &qw, &qx, &qy, &qz);
        T.Eq(ok, 1, "GetPlayerPose succeeds");
        T.Info($"pose cell 0x{cell:X8} ({x:F2}, {y:F2}, {z:F2}) q({qw:F3}, {qx:F3}, {qy:F3}, {qz:F3})");
        T.Eq(cell, 0x61460249u, "pose cell = her dungeon spot");
        T.Check(cell == pos.Cell && x == pos.X && y == pos.Y && z == pos.Z && qw == pos.Qw && qz == pos.Qz, "pose = the world model's position");

        float heading = 0;
        T.Eq(((delegate* unmanaged[Cdecl]<float*, int>)f.Api->GetPlayerHeadingFn)(&heading), 1, "GetPlayerHeading succeeds");
        // The engine: compass = -2 atan2(qz, qw) (0 = north, clockwise); GetPlayerHeading = 90 - compass.
        double compass = ((-2.0 * Math.Atan2(qz, qw) * 180.0 / Math.PI) % 360.0 + 720.0) % 360.0;
        double expect = ((90.0 - compass) % 360.0 + 360.0) % 360.0;
        T.Info($"heading {heading:F2} (compass {compass:F2})");
        T.Check(Math.Abs(heading - expect) < 0.01 || Math.Abs(Math.Abs(heading - expect) - 360) < 0.01, "heading = 90 - compass of her quaternion");
        // Convention: facing north -> 90, east -> 0, south -> 270, west -> 180 (0 = east, counter-clockwise).
        static (float W, float Z) Facing(double compassDeg) { double yaw = -compassDeg * Math.PI / 180; return ((float)Math.Cos(yaw / 2), (float)Math.Sin(yaw / 2)); }
        foreach ((double c, float h) in new[] { (0.0, 90f), (90.0, 0f), (180.0, 270f), (270.0, 180f), (45.0, 45f) })
        {
            (float w, float zq) = Facing(c);
            float got = HostApi.DecalHeadingFromQuaternion(w, zq);
            float d = Math.Abs(got - h);
            T.Check(d < 0.01f || Math.Abs(d - 360f) < 0.01f, $"facing compass {c}: heading {got:F2}, expected {h}");
        }

        double ns = 0, ew = 0;
        T.Eq(((delegate* unmanaged[Cdecl]<double*, double*, int>)f.Api->GetCurCoordsFn)(&ns, &ew), 1, "GetCurCoords succeeds");
        double eEw = (((cell >> 24) & 0xFF) * 8.0 + x / 24.0 - 1019.5) / 10.0;
        double eNs = (((cell >> 16) & 0xFF) * 8.0 + y / 24.0 - 1019.5) / 10.0;
        T.Info($"coords {Math.Abs(ns):F2}{(ns >= 0 ? "N" : "S")} {Math.Abs(ew):F2}{(ew >= 0 ? "E" : "W")}");
        T.Check(Math.Abs(ns - eNs) < 1e-9 && Math.Abs(ew - eEw) < 1e-9, "coords = the landblock formula");
    }

    private static void EdgeCases(Fixture f)
    {
        uint u = 0;
        int i = 0;
        float fl = 0;
        double d = 0;
        T.Eq(((delegate* unmanaged[Cdecl]<uint*, uint*, uint*, uint*, uint*, uint*, int>)f.Api->GetPlayerVitalsFn)(null, &u, &u, &u, &u, &u), 0, "GetPlayerVitals(null) = 0");
        T.Eq(((delegate* unmanaged[Cdecl]<uint*, uint*, uint*, int>)f.Api->GetPlayerBaseVitalsFn)(&u, null, &u), 0, "GetPlayerBaseVitals(null) = 0");
        T.Eq(((delegate* unmanaged[Cdecl]<uint, uint, uint*, int>)f.Api->GetObjectAttribute2ndBaseLevelFn)(Me, 1, null), 0, "Attribute2ndBaseLevel(null) = 0");
        T.Eq(((delegate* unmanaged[Cdecl]<uint, uint, int*, int*, int>)f.Api->GetObjectSkillFn)(Me, 6, null, &i), 0, "GetObjectSkill(null) = 0");
        T.Eq(((delegate* unmanaged[Cdecl]<uint, uint, int, int*, int>)f.Api->GetObjectSkillBuffedFn)(Me, 6, 0, null), 0, "GetObjectSkillBuffed(null) = 0");
        T.Eq(((delegate* unmanaged[Cdecl]<uint, uint, int, uint*, int>)f.Api->GetObjectAttributeFn)(Me, 1, 0, null), 0, "GetObjectAttribute(null) = 0");
        T.Eq(((delegate* unmanaged[Cdecl]<uint*, int, int>)f.Api->ReadKnownSpellsFn)(null, 10), -1, "ReadKnownSpells(null) = -1");
        T.Eq(((delegate* unmanaged[Cdecl]<uint*, int, int>)f.Api->ReadKnownSpellsFn)(&u, 0), -1, "ReadKnownSpells(max 0) = -1");
        T.Eq(((delegate* unmanaged[Cdecl]<uint*, double*, int, int>)f.Api->ReadPlayerEnchantmentsFn)(null, &d, 4), -1, "ReadPlayerEnchantments(null) = -1");
        T.Eq(((delegate* unmanaged[Cdecl]<uint*, double*, int, int>)f.Api->ReadPlayerEnchantmentsFn)(&u, &d, 0), 0, "ReadPlayerEnchantments(max 0) = 0");
        T.Eq(((delegate* unmanaged[Cdecl]<uint, uint*, double*, int, int>)f.Api->ReadObjectEnchantmentsFn)(Me, &u, null, 4), -1, "ReadObjectEnchantments(null) = -1");
        T.Eq(((delegate* unmanaged[Cdecl]<uint*, float*, float*, float*, float*, float*, float*, float*, int>)f.Api->GetPlayerPoseFn)(&u, &fl, &fl, null, &fl, &fl, &fl, &fl), 0, "GetPlayerPose(null) = 0");
        T.Eq(((delegate* unmanaged[Cdecl]<float*, int>)f.Api->GetPlayerHeadingFn)(null), 0, "GetPlayerHeading(null) = 0");
        T.Eq(((delegate* unmanaged[Cdecl]<double*, double*, int>)f.Api->GetCurCoordsFn)(&d, null), 0, "GetCurCoords(null) = 0");
        T.Eq(((delegate* unmanaged[Cdecl]<uint*, int, uint*, int>)f.Api->GetCharacterTitlesFn)(null, 0, &u), -1, "GetCharacterTitles = -1 (not tracked)");
        T.Check(f.Api->GetPlayerVitalsFn != IntPtr.Zero && f.Api->GetVitaeFn != IntPtr.Zero && f.Api->GetServerTimeFn != IntPtr.Zero
            && f.Api->ReadKnownSpellsFn != IntPtr.Zero && f.Api->GetCharacterTitlesFn != IntPtr.Zero, "player slots filled");

        // No context at all (a plugin thread before any context exists can't happen here; with the
        // fixture's context current, an out-of-world question is answered by the "not the player" paths).
        T.Eq(((delegate* unmanaged[Cdecl]<uint, uint, int*, int*, int>)f.Api->GetObjectSkillFn)(0, 6, &i, &i), 0, "GetObjectSkill(0) = 0");
    }
}
