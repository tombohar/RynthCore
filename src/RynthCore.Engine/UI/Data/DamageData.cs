// ============================================================================
//  RynthCore.Engine - UI/Data/DamageData.cs
//  RynthAi's learned per-monster combat data (the Damage panel) and the
//  name-keyed monster rules (Monsters panel, Damage drawer), for both faces.
//
//  Every getter here frees its previous return buffer on the next call, so
//  the hub is the only caller: DamageSource (damage rows, selectable weapons,
//  pet choices) and MonsterRulesSource (rules). Before this, the Avalonia
//  Monsters and Damage panels both polled RynthPluginGetMonstersJson from the
//  UI thread. Setters run on the pump too (DamageCommands), so they no longer
//  race the plugin tick.
//
//  Difficulty tiers (2026-10-03; generic since 2026-10-21): on a server whose
//  plugin reports tiers (the same monsters scaled by a percent a tier), RynthAi
//  keeps a monster's HP, kills, casts to kill, seconds per kill and hit rate per
//  tier. The rows describe the tier RynthAi is showing;
//  RynthPluginGetAwakenedTierJson (the export's old name) says which and whether
//  to show tiers at all, and the panel's picker sets it
//  (RynthPluginSetDamageViewTier). An older RynthAi without the export shows
//  nothing of it.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using RynthCore.Engine.Compatibility;
using RynthCore.Engine.Plugins;
using RynthCore.Engine.UI.Panels;

namespace RynthCore.Engine.UI.Data;

/// <summary>One row of RynthPluginGetMonsterDamageJson (a monster, per weapon/element/tier key).</summary>
internal sealed class DamageRow
{
    public uint Wcid; public string Name = ""; public uint Wid; public string Weapon = "";
    public string Elem = ""; public int Tier; public int Hp; public bool HpManual;
    /// <summary>Hp is an estimate: tier 0's plus the tier's scaling (that tier not appraised yet).</summary>
    public bool HpEst;
    public double Crit; public int CritN; public double NonCrit; public int NonCritN;
    public double Casts; public int Kills; public string Key = "";
    /// <summary>Every wcid of a row of several kinds (monsters of one name, e.g. retail's five
    /// Rampagers), ascending; empty for one kind. Wcid is the one edits and details go to; the
    /// plugin applies an edit to all of them.</summary>
    public uint[] Wcids = Array.Empty<uint>();
    /// <summary>Per-monster (wcid) weapon state, the same on every row of a wcid.</summary>
    public uint AssignedWid; public string AssignedWeapon = "";
    public uint BestWid; public string BestWeapon = "";
    public uint AssignedOff; public string AssignedOffName = "";
    public string Pet = ""; public string PetLabel = "Auto";
    /// <summary>What the monster takes most damage from ("Bludgeon 1.0, Pierce 0.86") and where that came from.</summary>
    public string Weak = ""; public string WeakSrc = "";
    /// <summary>Seconds per kill and hit rate (0-1) over every weapon; -1 = not learned yet.</summary>
    public double SecKill = -1, HitRate = -1;
    /// <summary>The monster being fought (or selected) right now.</summary>
    public bool Targeted;
    /// <summary>One of these is on the player's landblock.</summary>
    public bool Nearby;
    public List<DamageTierStat> Tiers = new();
    /// <summary>The synthetic top "Default" line (wcid 0).</summary>
    public bool IsDefault;
    /// <summary>Evades, resists, attacks and spells both ways since login (the row's optional "mstats",
    /// DamageRowStats.cs); null when the plugin sends none.</summary>
    public DamageRowStats? Stats;
}

internal sealed class DamageTierStat
{
    public int Tier; public string Elem = ""; public string Weapon = "";
    public double Crit; public int CritN; public double NonCrit; public int NonCritN;
    public double Casts; public int Kills;
}

internal sealed class DamageView
{
    public List<DamageRow> Rows = new();
    /// <summary>Weapons with learned rows (the filter list), in first-seen order.</summary>
    public List<(uint Id, string Name)> LearnedWeapons = new();
    /// <summary>Configured weapons (ItemRules) plus learned ones, by name: the per-row pickers.</summary>
    public List<(uint Id, string Name)> WeaponChoices = new();
    /// <summary>("" = Auto, "E:&lt;element&gt;", "I:&lt;essence id&gt;", label).</summary>
    public List<(string Key, string Name)> PetChoices = new();
    public bool Bound;
    /// <summary>The difficulty tier the rows describe; Show false = say nothing of tiers.</summary>
    public DifficultyTierView Tier = DifficultyTierView.Hidden;
}

/// <summary>RynthPluginGetAwakenedTierJson: RynthAi shows tiers only when the server plugin reports them.</summary>
internal sealed class DifficultyTierView
{
    public static readonly DifficultyTierView Hidden = new();

    public bool Show;
    /// <summary>The tier where the player stands; the tier the rows describe; whether that follows Current.</summary>
    public int Current, View;
    public bool Following = true;
    public double Percent = 5;
    /// <summary>Tiers with something learned, plus 0 and the current one, ascending.</summary>
    public List<int> Tiers = new();

    public static DifficultyTierView Parse(string? json)
    {
        if (string.IsNullOrEmpty(json)) return Hidden;
        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement r = doc.RootElement;
            if (!r.TryGetProperty("show", out var sh) || sh.ValueKind != JsonValueKind.True) return Hidden;
            var t = new DifficultyTierView { Show = true };
            if (r.TryGetProperty("current", out var c)) t.Current = c.GetInt32();
            if (r.TryGetProperty("view", out var v)) t.View = v.GetInt32();
            if (r.TryGetProperty("following", out var f)) t.Following = f.ValueKind == JsonValueKind.True;
            if (r.TryGetProperty("percent", out var pc)) t.Percent = pc.GetDouble();
            if (r.TryGetProperty("tiers", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (JsonElement e in arr.EnumerateArray()) t.Tiers.Add(e.GetInt32());
            return t;
        }
        catch { return Hidden; }
    }
}

/// <summary>The monster rules (RynthPluginGetMonstersJson). Parsed is shared: copy before changing (ParseCopy).</summary>
internal sealed class MonsterRulesSnapshot
{
    public MonsterRulesSnapshot(string json, MonstersPanel.Payload parsed)
    {
        Json = json;
        Parsed = parsed;
        foreach (MonstersPanel.Rule r in parsed.Rules)
            if (!r.Name.Equals("Default", StringComparison.OrdinalIgnoreCase))
                _custom.Add(r.Name);
    }

    public string Json { get; }
    public MonstersPanel.Payload Parsed { get; }
    private readonly HashSet<string> _custom = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>An exact-name rule exists for <paramref name="name"/> (it doesn't inherit Default).</summary>
    public bool HasCustomRule(string name) => _custom.Contains(name);

    public MonstersPanel.Rule? FindRule(string name)
    {
        foreach (MonstersPanel.Rule r in Parsed.Rules)
            if (!r.Name.Equals("Default", StringComparison.OrdinalIgnoreCase) && r.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return r;
        return null;
    }

    public MonstersPanel.Rule DefaultRule()
    {
        foreach (MonstersPanel.Rule r in Parsed.Rules)
            if (r.Name.Equals("Default", StringComparison.OrdinalIgnoreCase))
                return r;
        return new MonstersPanel.Rule { Name = "Default", UseBolt = true };
    }

    /// <summary>A private, editable copy of the payload.</summary>
    public MonstersPanel.Payload ParseCopy() =>
        JsonSerializer.Deserialize(Json, MonstersPanelJsonContext.Default.Payload) ?? new MonstersPanel.Payload();
}

/// <summary>Damage rows + weapons (500 ms) and pet choices (2 s) while a Damage face is open.</summary>
internal sealed unsafe class DamageSource : UiSource<DamageView>
{
    private delegate* unmanaged[Cdecl]<IntPtr> _getRows, _getWeapons, _getPets, _getTier;
    private string? _rowsJson, _weaponsJson, _petsJson, _tierJson;
    private DifficultyTierView _tier = DifficultyTierView.Hidden;
    private List<DamageRow> _rows = new();
    private List<(uint Id, string Name)> _items = new();
    private List<(string Key, string Name)> _pets = new();
    private long _nextPetTicks;

    public DamageSource() : base("Damage", periodMs: 500) { }

    protected internal override void Poll()
    {
        if (_getRows == null)
        {
            _getRows = (delegate* unmanaged[Cdecl]<IntPtr>)PluginExportBinder.Resolve("RynthAi", "RynthPluginGetMonsterDamageJson");
            _getWeapons = (delegate* unmanaged[Cdecl]<IntPtr>)PluginExportBinder.Resolve("RynthAi", "RynthPluginGetCombatWeaponsJson");
            _getPets = (delegate* unmanaged[Cdecl]<IntPtr>)PluginExportBinder.Resolve("RynthAi", "RynthPluginGetPetChoicesJson");
            _getTier = (delegate* unmanaged[Cdecl]<IntPtr>)PluginExportBinder.Resolve("RynthAi", "RynthPluginGetAwakenedTierJson");
            if (_getRows == null)
            {
                if (Current == null) Publish(new DamageView { Bound = false });
                return;
            }
        }

        bool changed = Current == null || !Current.Value.Bound;
        string? json = Read(_getRows);
        if (json != null && json != _rowsJson) { _rowsJson = json; _rows = ParseRows(json); changed = true; }

        if (_getWeapons != null && (json = Read(_getWeapons)) != null && json != _weaponsJson)
        {
            _weaponsJson = json;
            _items = ParseItems(json);
            changed = true;
        }

        // The difficulty tier: RynthAi's {"show":false} where the server plugin reports none.
        string? tierJson = _getTier != null ? Read(_getTier) : null;
        if (tierJson != _tierJson)
        {
            _tierJson = tierJson;
            _tier = DifficultyTierView.Parse(tierJson);
            changed = true;
        }

        long now = Stopwatch.GetTimestamp();
        if (_getPets != null && now >= _nextPetTicks)
        {
            _nextPetTicks = now + Stopwatch.Frequency * 2;
            if ((json = Read(_getPets)) != null && json != _petsJson)
            {
                _petsJson = json;
                _pets = ParsePets(json);
                changed = true;
            }
        }

        if (changed) Publish(BuildView());
    }

    /// <summary>Re-read the pet choices on the next poll too (after a pet change).</summary>
    public void RefreshPetsSoon() => _nextPetTicks = 0;

    protected internal override void Reset()
    {
        _getRows = _getWeapons = _getPets = _getTier = null;
        _rowsJson = _weaponsJson = _petsJson = _tierJson = null;
        _tier = DifficultyTierView.Hidden;
        ClearSnapshot();
    }

    private static string? Read(delegate* unmanaged[Cdecl]<IntPtr> fn)
    {
        IntPtr p = fn();
        return p == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(p);
    }

    private DamageView BuildView()
    {
        var view = new DamageView { Rows = _rows, Bound = true, Tier = _tier };
        var seen = new HashSet<uint>();
        foreach (DamageRow r in _rows)
            if (r.Wid != 0 && seen.Add(r.Wid)) view.LearnedWeapons.Add((r.Wid, r.Weapon));

        var choiceSeen = new HashSet<uint>();
        foreach (var it in _items) if (it.Id != 0 && choiceSeen.Add(it.Id)) view.WeaponChoices.Add(it);
        foreach (var w in view.LearnedWeapons) if (choiceSeen.Add(w.Id)) view.WeaponChoices.Add(w);
        view.WeaponChoices.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        view.PetChoices = _pets.Count > 0 ? _pets : new List<(string, string)> { ("", "Auto (update RynthAi for pet choices)") };
        return view;
    }

    private static List<DamageRow> ParseRows(string json)
    {
        var list = new List<DamageRow>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (JsonElement e in doc.RootElement.EnumerateArray())
            {
                var row = new DamageRow
                {
                    Wcid = (uint)e.GetProperty("wcid").GetInt64(),
                    Name = e.GetProperty("name").GetString() ?? "",
                    Wid = (uint)e.GetProperty("wid").GetInt64(),
                    Weapon = e.GetProperty("weapon").GetString() ?? "",
                    Elem = e.GetProperty("elem").GetString() ?? "",
                    Tier = e.GetProperty("tier").GetInt32(),
                    Hp = e.GetProperty("hp").GetInt32(),
                    HpManual = e.GetProperty("hpManual").GetBoolean(),
                    Crit = e.GetProperty("crit").GetDouble(),
                    CritN = e.GetProperty("critN").GetInt32(),
                    NonCrit = e.GetProperty("noncrit").GetDouble(),
                    NonCritN = e.GetProperty("noncritN").GetInt32(),
                    Casts = e.GetProperty("casts").GetDouble(),
                    Kills = e.GetProperty("kills").GetInt32(),
                    Key = e.GetProperty("key").GetString() ?? "",
                };
                // Optional fields: an older plugin omits them.
                if (e.TryGetProperty("assignedWid", out var aw)) row.AssignedWid = (uint)aw.GetInt64();
                if (e.TryGetProperty("assignedWeapon", out var awn)) row.AssignedWeapon = awn.GetString() ?? "";
                if (e.TryGetProperty("bestWid", out var bw)) row.BestWid = (uint)bw.GetInt64();
                if (e.TryGetProperty("bestWeapon", out var bwn)) row.BestWeapon = bwn.GetString() ?? "";
                if (e.TryGetProperty("assignedOff", out var ao)) row.AssignedOff = (uint)ao.GetInt64();
                if (e.TryGetProperty("assignedOffName", out var aon)) row.AssignedOffName = aon.GetString() ?? "";
                if (e.TryGetProperty("isDefault", out var idf)) row.IsDefault = idf.GetBoolean();
                if (e.TryGetProperty("hpEst", out var he)) row.HpEst = he.ValueKind == JsonValueKind.True;
                if (e.TryGetProperty("wcids", out var wa) && wa.ValueKind == JsonValueKind.Array)
                {
                    var kinds = new List<uint>();
                    foreach (var w in wa.EnumerateArray()) if (w.TryGetInt64(out long wl)) kinds.Add((uint)wl);
                    if (kinds.Count > 1) row.Wcids = kinds.ToArray();
                }
                if (e.TryGetProperty("pet", out var pc)) row.Pet = pc.GetString() ?? "";
                if (e.TryGetProperty("petLabel", out var pl)) row.PetLabel = pl.GetString() ?? "Auto";
                if (e.TryGetProperty("weak", out var wk)) row.Weak = wk.GetString() ?? "";
                if (e.TryGetProperty("weakSrc", out var ws)) row.WeakSrc = ws.GetString() ?? "";
                if (e.TryGetProperty("secKill", out var sk)) row.SecKill = sk.GetDouble();
                if (e.TryGetProperty("hitRate", out var hr)) row.HitRate = hr.GetDouble();
                if (e.TryGetProperty("targeted", out var tg)) row.Targeted = tg.GetBoolean();
                if (e.TryGetProperty("nearby", out var nb)) row.Nearby = nb.GetBoolean();
                row.Stats = DamageRowStats.Parse(e);
                if (e.TryGetProperty("tiers", out var tarr) && tarr.ValueKind == JsonValueKind.Array)
                    foreach (JsonElement te in tarr.EnumerateArray())
                        row.Tiers.Add(new DamageTierStat
                        {
                            Tier = te.GetProperty("tier").GetInt32(),
                            Elem = te.GetProperty("elem").GetString() ?? "",
                            Weapon = te.GetProperty("weapon").GetString() ?? "",
                            Crit = te.GetProperty("crit").GetDouble(),
                            CritN = te.GetProperty("critN").GetInt32(),
                            NonCrit = te.GetProperty("noncrit").GetDouble(),
                            NonCritN = te.GetProperty("noncritN").GetInt32(),
                            Casts = te.GetProperty("casts").GetDouble(),
                            Kills = te.GetProperty("kills").GetInt32(),
                        });
                list.Add(row);
            }
        }
        catch { }
        return list;
    }

    private static List<(uint, string)> ParseItems(string json)
    {
        var list = new List<(uint, string)>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (JsonElement e in doc.RootElement.EnumerateArray())
                list.Add(((uint)e.GetProperty("id").GetInt64(), e.GetProperty("name").GetString() ?? ""));
        }
        catch { }
        return list;
    }

    private static List<(string, string)> ParsePets(string json)
    {
        var list = new List<(string, string)>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (JsonElement e in doc.RootElement.EnumerateArray())
                list.Add((e.GetProperty("key").GetString() ?? "", e.GetProperty("name").GetString() ?? ""));
        }
        catch { }
        return list;
    }

    /// <summary>Tier display: 0 = none ("—"); negative = ring spell ("R&lt;level&gt;"); positive = spell level.</summary>
    public static string FormatTier(int t) =>
        t == 0 ? "—" : t < 0 ? "R" + (-t).ToString(CultureInfo.InvariantCulture) : t.ToString(CultureInfo.InvariantCulture);
}

/// <summary>One monster's breakdown (RynthPluginGetMonsterDetailJson), for the Monster Detail panel.</summary>
internal sealed class MonsterDetail
{
    public uint Wcid; public string Name = ""; public int Hp; public string HpSrc = "";
    public double SecKill = -1, HitRate = -1;
    public string WeakSrc = "";
    public List<(string Element, double Mult)> Weak = new();   // Mult -1 = order only
    public List<DetailCast> Casts = new();
    public List<(string Weapon, int Hits, int Misses, double Sec, int SecN)> Weapons = new();
    public List<(string Element, int Kills, double Sec)> Summons = new();
    public List<(string Element, int Hits, double Avg, double Max)> Taken = new();
    public bool Bound;
}

internal sealed class DetailCast
{
    public string Weapon = "", Elem = ""; public int Tier, Hits, CritN, NonCritN, Kills;
    public double Avg, Crit, NonCrit, Casts;
}

/// <summary>The selected monster's detail (1 s) while the Monster Detail panel is open.</summary>
internal sealed unsafe class MonsterDetailSource : UiSource<MonsterDetail>
{
    private delegate* unmanaged[Cdecl]<uint, IntPtr> _get;
    private string? _lastJson;
    private uint _lastWcid;

    public MonsterDetailSource() : base("MonsterDetail", periodMs: 1000) { }

    /// <summary>The monster to show (any thread); the next poll reads it.</summary>
    public static volatile uint SelectedWcid;

    protected internal override void Poll()
    {
        uint wcid = SelectedWcid;
        if (_get == null)
        {
            _get = (delegate* unmanaged[Cdecl]<uint, IntPtr>)PluginExportBinder.Resolve("RynthAi", "RynthPluginGetMonsterDetailJson");
            if (_get == null)
            {
                if (Current == null) Publish(new MonsterDetail { Bound = false });
                return;
            }
        }
        if (wcid == 0) return;
        IntPtr p = _get(wcid);
        string? json = p == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(p);
        if (json == null || (json == _lastJson && wcid == _lastWcid)) return;
        _lastJson = json;
        _lastWcid = wcid;
        Publish(Parse(json, wcid));
    }

    protected internal override void Reset()
    {
        _get = null;
        _lastJson = null;
        ClearSnapshot();
    }

    private static MonsterDetail Parse(string json, uint wcid)
    {
        var d = new MonsterDetail { Wcid = wcid, Bound = true };
        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement r = doc.RootElement;
            if (!r.TryGetProperty("wcid", out _)) return d;
            d.Name = r.GetProperty("name").GetString() ?? "";
            d.Hp = r.GetProperty("hp").GetInt32();
            d.HpSrc = r.GetProperty("hpSrc").GetString() ?? "";
            d.SecKill = r.GetProperty("secKill").GetDouble();
            d.HitRate = r.GetProperty("hitRate").GetDouble();
            d.WeakSrc = r.GetProperty("weakSrc").GetString() ?? "";
            foreach (JsonElement e in r.GetProperty("weak").EnumerateArray())
                d.Weak.Add((e.GetProperty("e").GetString() ?? "", e.GetProperty("m").GetDouble()));
            foreach (JsonElement e in r.GetProperty("casts").EnumerateArray())
                d.Casts.Add(new DetailCast
                {
                    Weapon = e.GetProperty("weapon").GetString() ?? "", Elem = e.GetProperty("elem").GetString() ?? "",
                    Tier = e.GetProperty("tier").GetInt32(), Hits = e.GetProperty("hits").GetInt32(),
                    Avg = e.GetProperty("avg").GetDouble(), Crit = e.GetProperty("crit").GetDouble(),
                    CritN = e.GetProperty("critN").GetInt32(), NonCrit = e.GetProperty("noncrit").GetDouble(),
                    NonCritN = e.GetProperty("noncritN").GetInt32(), Casts = e.GetProperty("casts").GetDouble(),
                    Kills = e.GetProperty("kills").GetInt32(),
                });
            foreach (JsonElement e in r.GetProperty("weapons").EnumerateArray())
                d.Weapons.Add((e.GetProperty("weapon").GetString() ?? "", e.GetProperty("hits").GetInt32(),
                    e.GetProperty("misses").GetInt32(), e.GetProperty("sec").GetDouble(), e.GetProperty("secN").GetInt32()));
            foreach (JsonElement e in r.GetProperty("summons").EnumerateArray())
                d.Summons.Add((e.GetProperty("elem").GetString() ?? "", e.GetProperty("kills").GetInt32(), e.GetProperty("sec").GetDouble()));
            foreach (JsonElement e in r.GetProperty("taken").EnumerateArray())
                d.Taken.Add((e.GetProperty("elem").GetString() ?? "", e.GetProperty("hits").GetInt32(),
                    e.GetProperty("avg").GetDouble(), e.GetProperty("max").GetDouble()));
        }
        catch { }
        return d;
    }
}

/// <summary>RynthPluginGetMonstersJson while a Monsters or Damage face is open (1 s, and after each write).</summary>
internal sealed unsafe class MonsterRulesSource : UiSource<MonsterRulesSnapshot>
{
    private delegate* unmanaged[Cdecl]<IntPtr> _get;
    private string? _lastJson;

    public MonsterRulesSource() : base("MonsterRules", periodMs: 1000) { }

    protected internal override void Poll()
    {
        string? json = FetchJson();
        if (string.IsNullOrEmpty(json) || json == _lastJson) return;
        MonstersPanel.Payload? parsed;
        try { parsed = JsonSerializer.Deserialize(json, MonstersPanelJsonContext.Default.Payload); }
        catch { return; }
        if (parsed == null) return;
        _lastJson = json;
        Publish(new MonsterRulesSnapshot(json, parsed));
    }

    /// <summary>The plugin's current rules JSON. Pump thread only (hub commands use it to edit the latest rules).</summary>
    internal string? FetchJson()
    {
        if (_get == null)
            _get = (delegate* unmanaged[Cdecl]<IntPtr>)PluginExportBinder.Resolve("RynthAi", "RynthPluginGetMonstersJson");
        if (_get == null) return null;
        IntPtr p = _get();
        return p == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(p);
    }

    protected internal override void Reset()
    {
        _get = null;
        _lastJson = null;
        ClearSnapshot();
    }
}

/// <summary>The Damage and Monsters panels' mutating exports, on the pump thread. Any thread may call these.</summary>
internal static unsafe class DamageCommands
{
    private static delegate* unmanaged[Cdecl]<uint, int, void> _setHp;
    private static delegate* unmanaged[Cdecl]<IntPtr, int> _deleteRow;
    private static delegate* unmanaged[Cdecl]<uint, uint, void> _setWeapon, _setOffhand;
    private static delegate* unmanaged[Cdecl]<uint, void> _setDefaultWeapon;
    private static delegate* unmanaged[Cdecl]<IntPtr, void> _setMonsters;
    private static delegate* unmanaged[Cdecl]<void> _clearStats;
    private static delegate* unmanaged[Cdecl]<uint, IntPtr, void> _setPet;
    private static delegate* unmanaged[Cdecl]<int, void> _setViewTier;

    static DamageCommands()
    {
        PluginManager.PluginsUnloaded += () =>
        {
            _setHp = null; _deleteRow = null; _setWeapon = null; _setOffhand = null; _setDefaultWeapon = null;
            _setMonsters = null; _clearStats = null; _setPet = null; _setViewTier = null;
        };
    }

    private static IntPtr Export(string name) => PluginExportBinder.Resolve("RynthAi", name);

    private static void Run(string label, Action action, bool rules = false)
    {
        UiDataHub.Post("Damage " + label, () =>
        {
            action();
            UiSources.Damage.RequestRefreshAfterPluginTick();
            if (rules) UiSources.MonsterRules.RequestRefreshAfterPluginTick();
        });
    }

    /// <summary>Manual max HP; 0 returns the monster to auto.</summary>
    public static void SetHp(uint wcid, int hp) => Run("hp", () =>
    {
        if (_setHp == null) _setHp = (delegate* unmanaged[Cdecl]<uint, int, void>)Export("RynthPluginSetMonsterHp");
        if (_setHp != null) _setHp(wcid, hp);
    });

    public static void DeleteRow(string key) => Run("delete row", () =>
    {
        if (_deleteRow == null) _deleteRow = (delegate* unmanaged[Cdecl]<IntPtr, int>)Export("RynthPluginDeleteMonsterRow");
        if (_deleteRow == null || key.Length == 0) return;
        IntPtr k = Marshal.StringToHGlobalAnsi(key);
        try { _deleteRow(k); }
        finally { Marshal.FreeHGlobal(k); }
    });

    /// <summary>Per-monster weapon (0 = Auto, the learned best).</summary>
    public static void SetWeapon(uint wcid, uint weaponId) => Run("weapon", () =>
    {
        if (_setWeapon == null) _setWeapon = (delegate* unmanaged[Cdecl]<uint, uint, void>)Export("RynthPluginSetMonsterWeapon");
        if (_setWeapon != null) _setWeapon(wcid, weaponId);
    });

    /// <summary>Per-monster offhand item (0 = none: the monster's rule, else the DEFAULT line, decides).</summary>
    public static void SetOffhand(uint wcid, uint offhandId) => Run("offhand", () =>
    {
        if (_setOffhand == null) _setOffhand = (delegate* unmanaged[Cdecl]<uint, uint, void>)Export("RynthPluginSetMonsterOffhand");
        if (_setOffhand != null) _setOffhand(wcid, offhandId);
    });

    /// <summary>The Default line's weapon: every monster set to Default uses it (0 = learned best).</summary>
    public static void SetDefaultWeapon(uint weaponId) => Run("default weapon", () =>
    {
        if (_setDefaultWeapon == null) _setDefaultWeapon = (delegate* unmanaged[Cdecl]<uint, void>)Export("RynthPluginSetDefaultWeapon");
        if (_setDefaultWeapon != null) _setDefaultWeapon(weaponId);
    });

    /// <summary>Pet for this monster: "" = Auto, "E:&lt;element&gt;", "I:&lt;essence id&gt;".</summary>
    public static void SetPet(uint wcid, string choice) => Run("pet", () =>
    {
        if (_setPet == null) _setPet = (delegate* unmanaged[Cdecl]<uint, IntPtr, void>)Export("RynthPluginSetMonsterPet");
        if (_setPet == null) return;
        IntPtr a = Marshal.StringToHGlobalAnsi(choice);
        try { _setPet(wcid, a); }
        finally { Marshal.FreeHGlobal(a); }
        UiSources.Damage.RefreshPetsSoon();
    });

    /// <summary>The difficulty tier the panel shows: -1 follows the one where the player stands.</summary>
    public static void SetViewTier(int tier) => Run("view tier", () =>
    {
        if (_setViewTier == null) _setViewTier = (delegate* unmanaged[Cdecl]<int, void>)Export("RynthPluginSetDamageViewTier");
        if (_setViewTier != null) _setViewTier(tier);
        UiSources.MonsterDetail.RequestRefresh();
    });

    /// <summary>Removes one monster from the Damage tab: its learned data and its settings.</summary>
    public static void RemoveMonster(uint wcid) => DeleteRow(wcid.ToString(CultureInfo.InvariantCulture));

    /// <summary>Clears all learned damage statistics (names and manual settings are kept).</summary>
    public static void ClearStats() => Run("clear stats", () =>
    {
        if (_clearStats == null) _clearStats = (delegate* unmanaged[Cdecl]<void>)Export("RynthPluginClearMonsterStats");
        if (_clearStats != null) _clearStats();
    });

    /// <summary>Writes the whole rules array, already serialized (the Monsters panel's save).</summary>
    public static void SetRulesJson(string json) => Run("rules", () =>
    {
        if (_setMonsters == null) _setMonsters = (delegate* unmanaged[Cdecl]<IntPtr, void>)Export("RynthPluginSetMonstersJson");
        if (_setMonsters == null) return;
        IntPtr ansi = Marshal.StringToHGlobalAnsi(json);
        try { _setMonsters(ansi); }
        finally { Marshal.FreeHGlobal(ansi); }
    }, rules: true);

    /// <summary>
    /// Edits <paramref name="name"/>'s exact-name rule on the plugin's latest
    /// rules (read fresh here, so an edit can't clobber one made elsewhere).
    /// A monster that inherits Default gets a copy of Default first, inserted
    /// right after it so the specific rule wins. "Default" edits Default itself.
    /// <paramref name="copyFrom"/>: the rule to copy instead of Default when the monster has no
    /// rule of its own yet (the name rule that covers it, so an edit doesn't swap its spells).
    /// </summary>
    public static void EditRule(string name, Action<MonstersPanel.Rule> edit, string? copyFrom = null) => Run("edit rule", () =>
    {
        MonstersPanel.Payload? rules = FreshRules();
        if (rules == null) return;
        edit(EnsureRule(rules, name, copyFrom));
        PushRules(rules.Rules);
    }, rules: true);

    /// <summary>
    /// The first rule other than Default whose name <paramref name="monsterName"/> contains, in list
    /// order, the way RynthAi's targeting priority finds it (CombatManager.ScoreCandidate, which
    /// goes by name only: a match expression doesn't limit the priority bonus).
    /// </summary>
    public static MonstersPanel.Rule? CoveringRule(List<MonstersPanel.Rule> rules, string monsterName)
    {
        foreach (MonstersPanel.Rule r in rules)
        {
            if (IsDefault(r) || string.IsNullOrWhiteSpace(r.Name)) continue;
            if (monsterName.IndexOf(r.Name, StringComparison.OrdinalIgnoreCase) >= 0) return r;
        }
        return null;
    }

    /// <summary>Makes <paramref name="name"/> follow Default again (drops its exact-name rule).</summary>
    public static void ResetRule(string name) => Run("reset rule", () =>
    {
        MonstersPanel.Payload? rules = FreshRules();
        if (rules == null) return;
        int i = rules.Rules.FindIndex(r => !IsDefault(r) && r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (i < 0) return;
        rules.Rules.RemoveAt(i);
        PushRules(rules.Rules);
    }, rules: true);

    private static bool IsDefault(MonstersPanel.Rule r) => r.Name.Equals("Default", StringComparison.OrdinalIgnoreCase);

    private static MonstersPanel.Payload? FreshRules()
    {
        string? json = UiSources.MonsterRules.FetchJson();
        if (string.IsNullOrEmpty(json)) return null;
        MonstersPanel.Payload? p;
        try { p = JsonSerializer.Deserialize(json, MonstersPanelJsonContext.Default.Payload); }
        catch { return null; }
        // Never write back an empty or unloaded array: it would wipe the rules (Default included).
        return p?.Rules == null || p.Rules.Count == 0 ? null : p;
    }

    private static MonstersPanel.Rule EnsureRule(MonstersPanel.Payload rules, string name, string? copyFrom = null)
    {
        int defIdx = rules.Rules.FindIndex(IsDefault);
        if (name.Equals("Default", StringComparison.OrdinalIgnoreCase))
        {
            if (defIdx >= 0) return rules.Rules[defIdx];
            var created = new MonstersPanel.Rule { Name = "Default", UseBolt = true };
            rules.Rules.Add(created);
            return created;
        }
        MonstersPanel.Rule? existing = rules.Rules.Find(r => !IsDefault(r) && r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing != null) return existing;
        MonstersPanel.Rule s = defIdx >= 0 ? rules.Rules[defIdx] : new MonstersPanel.Rule { Name = "Default", UseBolt = true };
        MonstersPanel.Rule? from = string.IsNullOrEmpty(copyFrom) ? null
            : rules.Rules.Find(r => !IsDefault(r) && r.Name.Equals(copyFrom, StringComparison.OrdinalIgnoreCase));
        MonstersPanel.Rule clone = CopyOfDefault(from ?? s, name);
        if (from != null) { clone.OffhandId = from.OffhandId; clone.PetDamage = from.PetDamage; clone.AttackPower = from.AttackPower; clone.Kite = from.Kite; clone.PreferredDamage = from.PreferredDamage; }   // a name rule's own choices, kept
        rules.Rules.Insert(defIdx >= 0 ? defIdx + 1 : 0, clone);
        return clone;
    }

    /// <summary>
    /// A new rule named <paramref name="name"/> with Default's settings. Off hand, pet, attack
    /// power, kite and preferred damage are left on "follow Default" (0 / PAuto / Default /
    /// Default / Auto) rather than copied, so a later change on the DEFAULT line still reaches it.
    /// </summary>
    private static MonstersPanel.Rule CopyOfDefault(MonstersPanel.Rule s, string name) => new()
    {
        Name = name, Category = s.Category,
        MatchExpression = "",   // an exact-name rule matches by name, not by a (possibly false) expression
        Priority = s.Priority, DamageType = s.DamageType, WeaponId = s.WeaponId, OffhandId = 0,
        ExVuln = s.ExVuln, PetDamage = "PAuto", AttackPower = "Default", Kite = "Default", PreferredDamage = "Auto",
        Fester = s.Fester, Broadside = s.Broadside, GravityWell = s.GravityWell,
        Imperil = s.Imperil, Yield = s.Yield, Vuln = s.Vuln,
        Corrosion = s.Corrosion, Corruption = s.Corruption, DestructiveCurse = s.DestructiveCurse,
        WeakeningCurse = s.WeakeningCurse, FesteringCurse = s.FesteringCurse,
        UseArc = s.UseArc, UseBolt = s.UseBolt, UseRing = s.UseRing, UseStreak = s.UseStreak, UseBlast = s.UseBlast, UseWall = s.UseWall,
        CustomDebuffs = s.CustomDebuffs,
    };

    /// <summary>
    /// Adds a name rule (the Damage panel's Name rules): it covers every monster whose name
    /// contains <paramref name="name"/>, and the expression too when one is given. Starts as a
    /// copy of Default and goes at the end of the list, so a monster's own rule (inserted
    /// after Default) still wins over it. An existing rule of that name only takes the expression.
    /// </summary>
    public static void AddRule(string name, string expression) => Run("add rule", () =>
    {
        name = name.Trim();
        expression = expression.Trim();
        if (name.Length == 0 || name.Equals("Default", StringComparison.OrdinalIgnoreCase)) return;
        MonstersPanel.Payload? rules = FreshRules();
        if (rules == null) return;
        MonstersPanel.Rule? existing = rules.Rules.Find(r => !IsDefault(r) && r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            if (expression.Length == 0) return;
            existing.MatchExpression = expression;
        }
        else
        {
            MonstersPanel.Rule def = rules.Rules.Find(IsDefault) ?? new MonstersPanel.Rule { Name = "Default", UseBolt = true };
            MonstersPanel.Rule added = CopyOfDefault(def, name);
            added.MatchExpression = expression;
            rules.Rules.Add(added);
        }
        PushRules(rules.Rules);
    }, rules: true);

    private static void PushRules(List<MonstersPanel.Rule> rules)
    {
        if (rules.Count == 0) return;   // never wipe the plugin's rules
        if (_setMonsters == null) _setMonsters = (delegate* unmanaged[Cdecl]<IntPtr, void>)Export("RynthPluginSetMonstersJson");
        if (_setMonsters == null) return;
        string json = JsonSerializer.Serialize(new MonstersPanel.Payload { Rules = rules }, MonstersPanelJsonContext.Default.Payload);
        IntPtr ansi = Marshal.StringToHGlobalAnsi(json);
        try { _setMonsters(ansi); }
        finally { Marshal.FreeHGlobal(ansi); }
    }

    // ── Drawer vocabulary (shared by both faces) ─────────────────────────

    public static readonly (string Field, string Label, string Tip)[] DebuffDefs =
    {
        ("Imperil",     "Imperil",  "Imperil (Gossamer Flesh / Imperil Other)"),
        ("Vuln",        "Vuln",     "Vulnerability (element-matched)"),
        ("Fester",      "Fester",   "Fester (Decrepitude's Grasp / Fester Other)"),
        ("Yield",       "Yield",    "Yield (Magic Yield Other)"),
        ("Broadside",   "Broadside","Broadside (Missile Weapons Ineptitude)"),
        ("GravityWell", "Gravity",  "Gravity Well (Vulnerability Other)"),
    };

    /// <summary>The Void debuffs, cast with Void Magic (a character without it skips them). Drawn
    /// as their own group: the faces lay DebuffDefs out in one row.</summary>
    public static readonly (string Field, string Label, string Tip)[] VoidDebuffDefs =
    {
        ("Corrosion",        "Corrosion",   "Corrosion: Nether damage over about 15 seconds.\nA Void projectile; the damage starts when it hits."),
        ("Corruption",       "Corruption",  "Corruption: Nether damage over about 30 seconds.\nA Void blast (a spread of projectiles); the damage starts when it hits."),
        ("DestructiveCurse", "Destructive", "Destructive Curse: Nether damage over about 30 seconds."),
        ("WeakeningCurse",   "Weakening",   "Weakening Curse: the monster does less damage (15 seconds)."),
        ("FesteringCurse",   "Festering",   "Festering Curse: the monster is healed less (30 seconds)."),
    };

    /// <summary>The Damage panel's one-letter debuff marks, in DebuffDefs then VoidDebuffDefs order.</summary>
    public static readonly (string Field, char Letter)[] DebuffLetters =
    {
        ("Imperil", 'I'), ("Vuln", 'V'), ("Fester", 'F'), ("Yield", 'Y'), ("Broadside", 'B'), ("GravityWell", 'G'),
        // Void: C Corrosion, R coRRuption, D Destructive Curse, W Weakening Curse, H Festering Curse (less healing).
        ("Corrosion", 'C'), ("Corruption", 'R'), ("DestructiveCurse", 'D'), ("WeakeningCurse", 'W'), ("FesteringCurse", 'H'),
    };

    /// <summary>"IVF" for the debuffs a rule has on; X = an extra vuln, + = typed-in ones.</summary>
    public static string DebuffMarks(MonstersPanel.Rule r)
    {
        var sb = new System.Text.StringBuilder(8);
        foreach (var (field, letter) in DebuffLetters) if (r.GetToggle(field)) sb.Append(letter);
        if (!string.IsNullOrEmpty(r.ExVuln) && !r.ExVuln.Equals("None", StringComparison.OrdinalIgnoreCase)) sb.Append('X');
        if (!string.IsNullOrWhiteSpace(r.CustomDebuffs)) sb.Append('+');
        return sb.ToString();
    }

    public static readonly (string Field, string Label, string Tip)[] ShapeDefs =
    {
        ("UseArc",    "Arc",    "Arc spells"),
        ("UseBolt",   "Bolt",   "Bolt spells (default)"),
        ("UseRing",   "Ring",   "Ring spells"),
        ("UseStreak", "Streak", "Streak spells"),
        ("UseBlast",  "Blast",  "Blast spells (Flame Blast, Frost Blast...): a spread of projectiles, from level III.\n" +
                                "Used instead of Bolt when both are on (Arc and Streak come first). A character who knows\n" +
                                "no blast of the element casts its Bolt."),
        ("UseWall",   "Wall",   "Wall spells (Slithering Flames, Os' Wall...): a line of projectiles straight ahead.\n" +
                                "Cast only when Wall Range is on and Min Wall Targets monsters stand in the wall's path to\n" +
                                "the target; RynthAi turns to face the target first. Otherwise the monster's other shape, or a bolt."),
    };

    public static readonly string[] ExVulnTypes =
        { "None", "Slash", "Pierce", "Bludgeon", "Fire", "Cold", "Lightning", "Acid", "Nether" };

    /// <summary>Rule.DamageType choices (Auto = from the monster's weaknesses).</summary>
    public static readonly string[] DamageTypes =
        { "Auto", "Slash", "Pierce", "Bludgeon", "Fire", "Cold", "Lightning", "Acid", "Nether" };

    /// <summary>Rule.PreferredDamage choices: the kind of damage to fight with when the character uses more than one.</summary>
    public static readonly string[] PreferredDamages = { "Auto", "Melee", "Missile", "War Magic", "Void Magic" };

    /// <summary>Rule.OffhandId as RynthAi reads it: 0 = follow the DEFAULT line, 1-4 a mode, anything else an item id.</summary>
    public static readonly string[] OffhandModeNames = { "Default", "Auto", "Shield", "Dual wield", "None" };

    /// <summary>Rule.AttackPower choices, spelled as RynthAi reads them (AttackPowerModes).</summary>
    public static readonly string[] AttackPowerModes =
        { "Default", "High", "Reckless", "Variable", "Reckless + Variable" };

    /// <summary>Rule.Kite choices, spelled as RynthAi reads them (KiteModes).</summary>
    public static readonly string[] KiteModes =
        { "Default", "Off", "PullAway" };

    /// <summary>Rule.PetDamage choices; "PAuto" is shown as Auto.</summary>
    public static readonly string[] PetDamageTypes =
        { "PAuto", "Slash", "Pierce", "Bludgeon", "Fire", "Cold", "Lightning", "Acid", "Nether" };

    /// <summary>Flips a debuff/shape toggle; a monster is never left with no spell shape (combat would refuse to cast).</summary>
    public static void FlipToggle(MonstersPanel.Rule rule, string field)
    {
        bool on = !rule.GetToggle(field);
        rule.SetToggle(field, on);
        bool shape = field is "UseArc" or "UseBolt" or "UseRing" or "UseStreak" or "UseBlast" or "UseWall";
        if (shape && !on && !HasAnyShape(rule))
            rule.UseBolt = true;
    }

    /// <summary>At least one spell shape is on (RynthAi refuses to attack with magic otherwise).</summary>
    public static bool HasAnyShape(MonstersPanel.Rule r) => r.UseArc || r.UseBolt || r.UseRing || r.UseStreak || r.UseBlast || r.UseWall;

    /// <summary>The shape RynthAi casts when no ring is due (RynthAi CombatManager.PickBaseShape): Arc, Streak, Blast, then Bolt.</summary>
    public static string BaseShapeName(MonstersPanel.Rule r)
    {
        if (r.UseArc) return "Arc";
        if (r.UseStreak) return "Streak";
        if (r.UseBlast) return "Blast";
        if (r.UseBolt || r.UseRing || r.UseWall) return "Bolt";
        return "";
    }
}
