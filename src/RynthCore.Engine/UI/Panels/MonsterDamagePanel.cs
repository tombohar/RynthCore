// ============================================================================
//  RynthCore.Engine — UI/Panels/MonsterDamagePanel.cs
//  Interactive table of the RynthAi plugin's learned per-monster combat data.
//  Bridge exports (polled every 500ms):
//    RynthPluginGetMonsterDamageJson()       → JSON rows (STABLE order)
//    RynthPluginSetMonsterHp(wcid, hp)       → manual HP override
//    RynthPluginDeleteMonsterRow(key)        → delete one learned row
//    RynthPluginGetCombatWeaponsJson()       → selectable weapons [{id,name}]
//    RynthPluginSetMonsterWeapon(wcid, wid)  → per-monster weapon override (0 = Auto/best)
//    RynthPluginSetMonsterOffhand(wcid, oid) → per-monster offhand override (0 = none; stored only)
//  Columns: WCID | Monster | Elem | Tier | HP(edit) | Crit | NonCrit | Casts/Kill | Kills | Weapon | Offhand | ✕
//  Weapon is per-MONSTER (wcid): it defaults to the learned-best ("Auto") and the
//  same value shows on every row of that monster; picking pins an override (gold).
//  Rows update IN PLACE (cells only) — the row Grids + HP TextBoxes are never
//  destroyed on a data tick, so the HP box stays focusable/typable while the
//  numbers keep ticking. A structural rebuild happens only when the set of rows
//  changes (new monster), and is deferred while an HP box is focused.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using RynthCore.Engine.ImGuiBackend;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.UI.Panels;

internal static class MonsterDamagePanel
{
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr GetJsonFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void   SetHpFn(uint wcid, int hp);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int    DelRowFn(IntPtr keyAnsi);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void   SetU2Fn(uint wcid, uint id); // weapon/offhand override
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void   SetU1Fn(uint id);            // default weapon (one arg)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void   SetJsonFn(IntPtr ansiJson);  // monsters rules write-back
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void   VoidFn();                    // parameterless action (clear stats)

    private static GetJsonFn? _getJson;
    private static SetHpFn?   _setHp;
    private static DelRowFn?  _delRow;
    private static GetJsonFn? _getWeapons;   // RynthPluginGetCombatWeaponsJson
    private static SetU2Fn?   _setWeapon;     // RynthPluginSetMonsterWeapon
    private static SetU2Fn?   _setOffhand;    // RynthPluginSetMonsterOffhand
    private static SetU1Fn?   _setDefaultWeapon; // RynthPluginSetDefaultWeapon (sweeping default)
    private static GetJsonFn? _getMonsters;   // RynthPluginGetMonstersJson  (per-monster debuff/shape rules)
    private static SetJsonFn? _setMonsters;   // RynthPluginSetMonstersJson  (write-back, whole rules array)
    private static VoidFn?    _clearStats;    // RynthPluginClearMonsterStats (master reset)

    private static void TryBind()
    {
        if (_getJson != null) return;
        LoadedPlugin? plugin = PluginManager.Plugins.FirstOrDefault(
            p => p.DisplayName.Contains("RynthAi", StringComparison.OrdinalIgnoreCase));
        if (plugin == null || plugin.ModuleHandle == IntPtr.Zero) return;

        IntPtr a = GetProcAddress(plugin.ModuleHandle, "RynthPluginGetMonsterDamageJson");
        IntPtr b = GetProcAddress(plugin.ModuleHandle, "RynthPluginSetMonsterHp");
        IntPtr c = GetProcAddress(plugin.ModuleHandle, "RynthPluginDeleteMonsterRow");
        IntPtr d = GetProcAddress(plugin.ModuleHandle, "RynthPluginGetCombatWeaponsJson");
        IntPtr e = GetProcAddress(plugin.ModuleHandle, "RynthPluginSetMonsterWeapon");
        IntPtr f = GetProcAddress(plugin.ModuleHandle, "RynthPluginSetMonsterOffhand");
        IntPtr g = GetProcAddress(plugin.ModuleHandle, "RynthPluginGetMonstersJson");
        IntPtr h = GetProcAddress(plugin.ModuleHandle, "RynthPluginSetMonstersJson");
        IntPtr i = GetProcAddress(plugin.ModuleHandle, "RynthPluginClearMonsterStats");
        IntPtr j = GetProcAddress(plugin.ModuleHandle, "RynthPluginSetDefaultWeapon");
        if (a != IntPtr.Zero) _getJson     = Marshal.GetDelegateForFunctionPointer<GetJsonFn>(a);
        if (b != IntPtr.Zero) _setHp       = Marshal.GetDelegateForFunctionPointer<SetHpFn>(b);
        if (c != IntPtr.Zero) _delRow      = Marshal.GetDelegateForFunctionPointer<DelRowFn>(c);
        if (d != IntPtr.Zero) _getWeapons  = Marshal.GetDelegateForFunctionPointer<GetJsonFn>(d);
        if (e != IntPtr.Zero) _setWeapon   = Marshal.GetDelegateForFunctionPointer<SetU2Fn>(e);
        if (f != IntPtr.Zero) _setOffhand  = Marshal.GetDelegateForFunctionPointer<SetU2Fn>(f);
        if (g != IntPtr.Zero) _getMonsters = Marshal.GetDelegateForFunctionPointer<GetJsonFn>(g);
        if (h != IntPtr.Zero) _setMonsters = Marshal.GetDelegateForFunctionPointer<SetJsonFn>(h);
        if (i != IntPtr.Zero) _clearStats  = Marshal.GetDelegateForFunctionPointer<VoidFn>(i);
        if (j != IntPtr.Zero) _setDefaultWeapon = Marshal.GetDelegateForFunctionPointer<SetU1Fn>(j);
    }

    internal static Control Create() => new View().Root;

    private const string Cols = "60,156,54,36,80,56,60,68,48,120,110,26";

    private static readonly IBrush HeaderBg = new SolidColorBrush(Color.FromArgb(0xFF, 0x12, 0x1C, 0x26));
    private static readonly IBrush PanelBg  = new SolidColorBrush(Color.FromArgb(0xF0, 0x08, 0x10, 0x18));
    private static readonly IBrush RowBg    = new SolidColorBrush(Color.FromArgb(0x40, 0x20, 0x30, 0x40));
    private static readonly IBrush ManualHp = new SolidColorBrush(Color.FromRgb(0xFF, 0xD1, 0x6A)); // gold = user-set
    private static readonly IBrush Dim      = new SolidColorBrush(Color.FromRgb(0x8A, 0x9A, 0xA8));

    // Weapon-filter dropdown (a Canvas-layered picker — a native ComboBox popup won't
    // composite into the overlay window; same idiom MonstersPanel uses).
    private static readonly IBrush PickerBg     = new SolidColorBrush(Color.FromRgb(0x0A, 0x14, 0x1E));
    private static readonly IBrush PickerBorder = new SolidColorBrush(Color.FromRgb(0x26, 0xD9, 0xE6));
    private static readonly IBrush PickerSelBg  = new SolidColorBrush(Color.FromRgb(0x1A, 0x2E, 0x42));
    private static readonly IBrush EntryBg      = new SolidColorBrush(Color.FromRgb(0x0F, 0x1F, 0x2E));
    private static readonly IBrush EntryBorder  = new SolidColorBrush(Color.FromRgb(0x26, 0x40, 0x59));
    private static readonly IBrush SearchBg     = new SolidColorBrush(Color.FromArgb(0xFF, 0x16, 0x22, 0x2E));
    private static readonly IBrush ToggleOn     = new SolidColorBrush(Color.FromRgb(0x33, 0xCC, 0x66)); // debuff/shape enabled
    private static readonly IBrush ToggleOff    = new SolidColorBrush(Color.FromRgb(0x4D, 0x4D, 0x4D)); // disabled

    // Per-monster debuff + spell-shape config (the expandable drawer). Fields map to
    // MonstersPanel.Rule.GetToggle/SetToggle; combat consumes them via BuildDebuffList /
    // the UseArc/Bolt/Ring/Streak shape gate — so this drawer is just an editor of the
    // existing name-keyed rule (no combat/storage changes).
    private static readonly (string Field, string Label, string Tip)[] DebuffDefs =
    {
        ("Imperil",     "Imperil",  "Imperil (Gossamer Flesh / Imperil Other)"),
        ("Vuln",        "Vuln",     "Vulnerability (element-matched)"),
        ("Fester",      "Fester",   "Fester (Decrepitude's Grasp / Fester Other)"),
        ("Yield",       "Yield",    "Yield (Magic Yield Other)"),
        ("Broadside",   "Broadside","Broadside (Missile Weapons Ineptitude)"),
        ("GravityWell", "Gravity",  "Gravity Well (Vulnerability Other)"),
    };
    private static readonly (string Field, string Label, string Tip)[] ShapeDefs =
    {
        ("UseArc",    "Arc",    "Arc spells"),
        ("UseBolt",   "Bolt",   "Bolt spells (default)"),
        ("UseRing",   "Ring",   "Ring spells"),
        ("UseStreak", "Streak", "Streak spells"),
    };
    private static readonly string[] ExVulnTypes =
        { "None", "Slash", "Pierce", "Bludgeon", "Fire", "Cold", "Lightning", "Acid", "Nether" };
    private static bool IsShape(string f) => f is "UseArc" or "UseBolt" or "UseRing" or "UseStreak";
    private static bool NoShapeOn(MonstersPanel.Rule r) => !r.UseArc && !r.UseBolt && !r.UseRing && !r.UseStreak;

    private sealed class Row
    {
        public uint Wcid; public string Name = ""; public uint Wid; public string Weapon = "";
        public string Elem = ""; public int Tier; public int Hp; public bool HpManual;
        public double Crit; public int CritN; public double NonCrit; public int NonCritN;
        public double Casts; public int Kills; public string Key = "";
        // Per-monster (wcid) weapon picker state (same on every row of a wcid).
        public uint AssignedWid; public string AssignedWeapon = "";   // user override (0 = none)
        public uint BestWid;     public string BestWeapon = "";       // learned recommendation (0 = none yet)
        public uint AssignedOff; public string AssignedOffName = "";  // offhand override (stored only)
        public List<TierStat> Tiers = new();   // per-tier breakdown for the expand drawer
        public bool IsDefault;                 // the synthetic top "Default" line (wcid 0)
    }

    private sealed class TierStat
    {
        public int Tier; public string Elem = ""; public string Weapon = "";
        public double Crit; public int CritN; public double NonCrit; public int NonCritN;
        public double Casts; public int Kills;
    }

    private sealed class RowWidgets
    {
        public Grid Root = null!;
        public TextBlock Wcid = null!, Name = null!, Elem = null!, Tier = null!;
        public TextBlock Crit = null!, NonCrit = null!, Casts = null!, Kills = null!;
        public TextBox Hp = null!;
        public Button HpManualToggle = null!;  // "M": on = manual HP entry, off = auto
        public Button DefaultToggle = null!;   // "D": on = this monster follows Default (debuffs/shapes/weapon)
        public Button Weapon = null!, Offhand = null!;
        public Button Chevron = null!;  // toggles the per-monster debuff drawer
        public Row Data = null!;  // latest row data (so picker click handlers read current best/override)
    }

    private sealed class View
    {
        public readonly Control Root;
        private readonly StackPanel _rows;
        private readonly Control _header;
        private readonly Button _filterBtn;
        private readonly TextBox _searchBox;
        private readonly Button _resetBtn;
        private readonly TextBlock _status;
        private readonly Canvas _pickerCanvas;     // overlay layer for the weapon dropdown
        private Border? _activePicker;
        private Button? _activeAnchor;             // which button owns the open picker (toggle/anchor tracking)

        private readonly Dictionary<string, RowWidgets> _cache = new();
        private List<string> _displayedKeys = new();

        private uint   _filterWid;                 // 0 = all weapons
        private string _filterName = "All weapons";
        private string _filterText = "";           // monster name / wcid search (case-insensitive)
        private string _lastJson = "";
        private bool   _editing;                   // an HP box is focused → defer structural rebuilds
        private List<Row> _data = new();
        private readonly List<(uint Wid, string Name)> _weapons = new();
        private readonly List<(uint Id, string Name)> _items = new(); // selectable weapons (from plugin ItemRules)
        private string _lastWeaponsJson = "";
        private MonstersPanel.Payload _monsters = new();  // name-keyed debuff/shape rules (for the drawer)
        private string _lastMonstersJson = "";
        private uint _expandedWcid;                       // monster (wcid) whose drawer is open (0 = none)
        private bool _defaultExpanded;                    // the top Default line's drawer is open

        public View()
        {
            _filterBtn = new Button
            {
                Content = "Weapon: All ▾",
                FontSize = 11,
                Padding = new Thickness(8, 2),
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x20, 0x3A, 0x52)),
            };
            _filterBtn.Click += (_, _) => ShowWeaponPicker();

            _searchBox = new TextBox
            {
                Watermark = "Search monster / wcid…",
                FontSize = 11,
                Width = 160, MinHeight = 22, Height = 22,
                Padding = new Thickness(4, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                Foreground = Brushes.White,
                Background = SearchBg,
                BorderBrush = EntryBorder,
            };
            // Overlay text input needs the Win32 backend told a box is active or
            // keystrokes never reach Avalonia (same wiring the HP box uses).
            _searchBox.GotFocus  += (_, _) => Win32Backend.AvaloniaTextInputActive = true;
            _searchBox.LostFocus += (_, _) => Win32Backend.AvaloniaTextInputActive = false;
            _searchBox.TextChanged += (_, _) =>
            {
                string t = (_searchBox.Text ?? "").Trim();
                if (t == _filterText) return;
                _filterText = t;
                _displayedKeys = new List<string> { "\0force" }; // force a structural rebuild for the new filter
                Reconcile();
            };

            _resetBtn = new Button
            {
                Content = "Reset stats",
                FontSize = 11,
                Padding = new Thickness(8, 2),
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x5A, 0x20, 0x20)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x8A)),
                BorderThickness = new Thickness(1),
            };
            ToolTip.SetTip(_resetBtn, "Clear all learned damage statistics (keeps monster names + your manual settings).");
            _resetBtn.Click += (_, _) => ShowResetConfirm();

            _status = new TextBlock
            {
                Text = "Binding to RynthAi…",
                FontSize = 11,
                Foreground = Dim,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
            };

            var top = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Margin = new Thickness(6, 5),
                Children = { _filterBtn, _searchBox, _resetBtn, _status },
            };

            _header = HeaderRow();
            _rows = new StackPanel { Spacing = 1, Margin = new Thickness(4, 0, 4, 4) };
            _rows.Children.Add(_header);

            var scroll = new ScrollViewer
            {
                Content = _rows,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility   = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            };

            var contentGrid = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Background = PanelBg };
            contentGrid.Children.Add(top);    Grid.SetRow(top, 0);
            contentGrid.Children.Add(scroll); Grid.SetRow(scroll, 1);

            // Picker overlay (sibling, on top). Only hit-tests while a picker is open so
            // it doesn't swallow clicks meant for the rows beneath it. Clicking the empty
            // canvas area (outside the picker border) dismisses.
            _pickerCanvas = new Canvas { IsHitTestVisible = false, Background = Brushes.Transparent };
            _pickerCanvas.PointerPressed += (_, e) =>
            {
                if (_activePicker == null || !ReferenceEquals(e.Source, _pickerCanvas)) return;
                e.Handled = true;
                ClosePicker();
            };

            var rootGrid = new Grid();
            rootGrid.Children.Add(contentGrid);
            rootGrid.Children.Add(_pickerCanvas);
            Root = rootGrid;

            TryBind();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            timer.Tick += (_, _) => Poll();
            timer.Start();
            // Stop with the visual tree (RadarPanel idiom): a running
            // DispatcherTimer roots the closed panel view forever — every
            // open/close otherwise adds another immortal poller hitting the
            // plugin C exports against a detached tree.
            rootGrid.AttachedToVisualTree   += (_, _) => { if (!timer.IsEnabled) timer.Start(); };
            rootGrid.DetachedFromVisualTree += (_, _) => timer.Stop();
            Poll();
        }

        private void Poll()
        {
            if (_getJson == null) { TryBind(); if (_getJson == null) { _status.Text = "Waiting for RynthAi plugin…"; return; } }

            RefreshItems();
            RefreshMonsters();

            IntPtr ptr;
            try { ptr = _getJson(); } catch { return; }
            if (ptr == IntPtr.Zero) return;
            string json = Marshal.PtrToStringAnsi(ptr) ?? "[]";
            if (json == _lastJson) return;
            _lastJson = json;

            _data = Parse(json);
            UpdateWeapons();
            Reconcile();
        }

        // Refresh the selectable-weapons list (configured ItemRules) shown in the row pickers.
        private void RefreshItems()
        {
            if (_getWeapons == null) return;
            IntPtr p;
            try { p = _getWeapons(); } catch { return; }
            if (p == IntPtr.Zero) return;
            string json = Marshal.PtrToStringAnsi(p) ?? "[]";
            if (json == _lastWeaponsJson) return;
            _lastWeaponsJson = json;
            _items.Clear();
            try
            {
                using var doc = JsonDocument.Parse(json);
                foreach (var e in doc.RootElement.EnumerateArray())
                    _items.Add(((uint)e.GetProperty("id").GetInt64(), e.GetProperty("name").GetString() ?? ""));
            }
            catch { }
        }

        // Refresh the name-keyed debuff/shape rules (shared with the Monsters tab) used by
        // the per-monster drawer. Reuses MonstersPanel's payload type + JSON context.
        private void RefreshMonsters()
        {
            if (_getMonsters == null) return;
            IntPtr p;
            try { p = _getMonsters(); } catch { return; }
            if (p == IntPtr.Zero) return;
            string json = Marshal.PtrToStringAnsi(p) ?? "";
            if (json.Length == 0 || json == _lastMonstersJson) return;
            _lastMonstersJson = json;
            try
            {
                var parsed = JsonSerializer.Deserialize(json, MonstersPanelJsonContext.Default.Payload);
                if (parsed != null) _monsters = parsed;
            }
            catch { }
        }

        private void Reconcile()
        {
            List<Row> filtered = ApplyFilters();
            var newKeys = filtered.Select(r => r.Key).ToList();
            bool structural = !newKeys.SequenceEqual(_displayedKeys);

            if (structural && !_editing)
            {
                ClosePicker(); // a row's dropdown anchor may be about to be rebuilt
                _rows.Children.Clear();
                _rows.Children.Add(_header);
                bool drawerDone = false;
                foreach (var r in filtered)
                {
                    if (!_cache.TryGetValue(r.Key, out var rw)) { rw = CreateRow(r); _cache[r.Key] = rw; }
                    _rows.Children.Add(rw.Root);
                    UpdateRow(rw, r);
                    // Insert the drawer right after the expanded row. Default line → edits the
                    // Default debuffs/shapes rule; a monster → its debuffs + per-tier breakdown.
                    if (r.IsDefault)
                    {
                        if (_defaultExpanded) _rows.Children.Add(BuildDebuffDrawer(r));
                    }
                    else if (!drawerDone && _expandedWcid != 0 && r.Wcid == _expandedWcid)
                    {
                        _rows.Children.Add(BuildDebuffDrawer(r));
                        drawerDone = true;
                    }
                }
                // prune cache entries no longer shown
                var keep = new HashSet<string>(newKeys);
                foreach (var k in _cache.Keys.Where(k => !keep.Contains(k)).ToList()) _cache.Remove(k);
                _displayedKeys = newKeys;

                if (filtered.Count == 0)
                {
                    bool filtering = _filterWid != 0 || _filterText.Length > 0;
                    string msg = _data.Count > 0 && filtering
                        ? "No monsters match the current filter."
                        : "No rows yet — fight monsters with magic.";
                    _rows.Children.Add(new TextBlock { Text = msg, FontSize = 11, Foreground = Dim, Margin = new Thickness(4, 8) });
                }
            }
            else
            {
                // Same row set (or a structural change deferred while editing): just
                // refresh the cells of the rows we already show — controls untouched.
                foreach (var r in filtered)
                    if (_cache.TryGetValue(r.Key, out var rw)) UpdateRow(rw, r);
            }
        }

        // Weapon filter (dropdown) AND monster search (name substring or wcid) combined.
        private List<Row> ApplyFilters()
        {
            IEnumerable<Row> q = _data;
            // The Default line is always kept (pinned at top), exempt from filters.
            if (_filterWid != 0)
                q = q.Where(r => r.IsDefault || r.Wid == _filterWid);
            if (_filterText.Length > 0)
                q = q.Where(r => r.IsDefault
                    || r.Name.IndexOf(_filterText, StringComparison.OrdinalIgnoreCase) >= 0
                    || r.Wcid.ToString(CultureInfo.InvariantCulture).Contains(_filterText));
            return q.ToList();
        }

        private static List<Row> Parse(string json)
        {
            var list = new List<Row>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                foreach (var e in doc.RootElement.EnumerateArray())
                {
                    var row = new Row
                    {
                        Wcid     = (uint)e.GetProperty("wcid").GetInt64(),
                        Name     = e.GetProperty("name").GetString() ?? "",
                        Wid      = (uint)e.GetProperty("wid").GetInt64(),
                        Weapon   = e.GetProperty("weapon").GetString() ?? "",
                        Elem     = e.GetProperty("elem").GetString() ?? "",
                        Tier     = e.GetProperty("tier").GetInt32(),
                        Hp       = e.GetProperty("hp").GetInt32(),
                        HpManual = e.GetProperty("hpManual").GetBoolean(),
                        Crit     = e.GetProperty("crit").GetDouble(),
                        CritN    = e.GetProperty("critN").GetInt32(),
                        NonCrit  = e.GetProperty("noncrit").GetDouble(),
                        NonCritN = e.GetProperty("noncritN").GetInt32(),
                        Casts    = e.GetProperty("casts").GetDouble(),
                        Kills    = e.GetProperty("kills").GetInt32(),
                        Key      = e.GetProperty("key").GetString() ?? "",
                    };
                    // New per-monster weapon fields (TryGetProperty so an older plugin
                    // build that omits them doesn't blank the whole table).
                    if (e.TryGetProperty("assignedWid", out var aw))     row.AssignedWid     = (uint)aw.GetInt64();
                    if (e.TryGetProperty("assignedWeapon", out var awn)) row.AssignedWeapon  = awn.GetString() ?? "";
                    if (e.TryGetProperty("bestWid", out var bw))         row.BestWid         = (uint)bw.GetInt64();
                    if (e.TryGetProperty("bestWeapon", out var bwn))     row.BestWeapon      = bwn.GetString() ?? "";
                    if (e.TryGetProperty("assignedOff", out var ao))     row.AssignedOff     = (uint)ao.GetInt64();
                    if (e.TryGetProperty("assignedOffName", out var aon))row.AssignedOffName = aon.GetString() ?? "";
                    if (e.TryGetProperty("isDefault", out var idf))      row.IsDefault       = idf.GetBoolean();
                    // Per-tier breakdown (for the expand drawer). Optional so an older plugin doesn't break the table.
                    if (e.TryGetProperty("tiers", out var tarr) && tarr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var te in tarr.EnumerateArray())
                        {
                            row.Tiers.Add(new TierStat
                            {
                                Tier     = te.GetProperty("tier").GetInt32(),
                                Elem     = te.GetProperty("elem").GetString() ?? "",
                                Weapon   = te.GetProperty("weapon").GetString() ?? "",
                                Crit     = te.GetProperty("crit").GetDouble(),
                                CritN    = te.GetProperty("critN").GetInt32(),
                                NonCrit  = te.GetProperty("noncrit").GetDouble(),
                                NonCritN = te.GetProperty("noncritN").GetInt32(),
                                Casts    = te.GetProperty("casts").GetDouble(),
                                Kills    = te.GetProperty("kills").GetInt32(),
                            });
                        }
                    }
                    list.Add(row);
                }
            }
            catch { }
            return list;
        }

        private void UpdateWeapons()
        {
            _weapons.Clear();
            var seen = new HashSet<uint>();
            foreach (var r in _data)
                if (r.Wid != 0 && seen.Add(r.Wid)) _weapons.Add((r.Wid, r.Weapon));

            if (_filterWid != 0 && !seen.Contains(_filterWid)) { _filterWid = 0; _filterName = "All weapons"; }
            _filterBtn.Content = "Weapon: " + Trunc(_filterName, 22) + " ▾";
            _status.Text = _data.Count == 0 ? "No kills recorded yet." : $"{_data.Count} rows · {_weapons.Count} weapon(s)";
        }

        // General Canvas-layered dropdown, shared by the top weapon filter and the
        // per-row weapon/offhand pickers. A native ComboBox popup won't composite into
        // this overlay window. Tracks the owning anchor so multiple picker buttons toggle
        // correctly (same idiom as MonstersPanel.ShowPicker).
        private void ShowPicker(Button anchor, List<(uint Id, string Name)> items, uint selectedId, Action<uint> onPick)
        {
            // Re-clicking the button that owns the open picker closes it.
            if (ReferenceEquals(anchor, _activeAnchor)) { ClosePicker(); return; }
            ClosePicker();
            _activeAnchor = anchor;

            var stack = new StackPanel { Spacing = 1 };
            foreach (var it in items)
            {
                bool sel = it.Id == selectedId;
                var entry = new Button
                {
                    Content = it.Name,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Background = sel ? PickerSelBg : EntryBg,
                    Foreground = sel ? PickerBorder : Brushes.White,
                    BorderBrush = EntryBorder,
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(6, 2),
                    FontSize = 11,
                    Height = 22,
                };
                uint pickedId = it.Id;
                entry.Click += (_, _) => { ClosePicker(); onPick(pickedId); };
                stack.Children.Add(entry);
            }

            const double width = 240;
            Point anchorPt = anchor.TranslatePoint(new Point(0, anchor.Bounds.Height), _pickerCanvas)
                             ?? new Point(8, 8);
            double left = Math.Clamp(anchorPt.X, 4, Math.Max(4, Root.Bounds.Width - width - 4));
            double top  = anchorPt.Y;
            double maxH = Math.Min(items.Count * 23 + 8, Math.Max(80, Root.Bounds.Height - top - 4));

            var picker = new Border
            {
                Width = width,
                MaxHeight = maxH,
                Background = PickerBg,
                BorderBrush = PickerBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(2),
                Child = new ScrollViewer
                {
                    VerticalScrollBarVisibility   = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                    Content = stack,
                },
            };
            _pickerCanvas.Children.Add(picker);
            Canvas.SetLeft(picker, left);
            Canvas.SetTop(picker, top);
            _activePicker = picker;
            _pickerCanvas.IsHitTestVisible = true;
        }

        private void ShowWeaponPicker()
        {
            var items = new List<(uint Id, string Name)>(_weapons.Count + 1) { (0u, "All weapons") };
            foreach (var w in _weapons) items.Add((w.Wid, w.Name));
            ShowPicker(_filterBtn, items, _filterWid, id =>
            {
                _filterWid  = id;
                var match = _weapons.FirstOrDefault(w => w.Wid == id);
                _filterName = id == 0 ? "All weapons" : (match.Name ?? ("Weapon " + id));
                _filterBtn.Content = "Weapon: " + Trunc(_filterName, 22) + " ▾";
                _displayedKeys = new List<string> { "\0force" }; // force a structural rebuild for the new filter
                Reconcile();
            });
        }

        // Distinct selectable weapons for the per-row pickers: configured weapons
        // (ItemRules, from the plugin) merged with weapons we have learned data for,
        // sorted by name.
        private List<(uint Id, string Name)> WeaponChoices()
        {
            var seen = new HashSet<uint>();
            var list = new List<(uint Id, string Name)>();
            foreach (var it in _items)
                if (it.Id != 0 && seen.Add(it.Id)) list.Add(it);
            foreach (var w in _weapons)
                if (w.Wid != 0 && seen.Add(w.Wid)) list.Add((w.Wid, w.Name));
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        private void ClosePicker()
        {
            if (_activePicker != null)
            {
                _pickerCanvas.Children.Remove(_activePicker);
                _activePicker = null;
            }
            _activeAnchor = null;
            _pickerCanvas.IsHitTestVisible = false;
        }

        // Master-reset confirmation, drawn on the Canvas overlay (a native dialog won't
        // composite here). Clicking outside the box (the canvas) cancels.
        private void ShowResetConfirm()
        {
            ClosePicker();
            _activeAnchor = _resetBtn;

            var msg = new TextBlock
            {
                Text = "Reset ALL learned damage statistics?\n\n" +
                       "Monster names and your settings (manual HP, weapon/offhand picks, " +
                       "debuffs) are KEPT — only the learned damage, crit, casts-to-kill, " +
                       "kills and HP are cleared. This cannot be undone.",
                FontSize = 11, Foreground = Brushes.White,
                TextWrapping = TextWrapping.Wrap, MaxWidth = 332,
            };
            var cancel = new Button
            {
                Content = "Cancel", FontSize = 11, Height = 24, Padding = new Thickness(12, 2),
                Background = EntryBg, Foreground = Brushes.White,
                BorderBrush = EntryBorder, BorderThickness = new Thickness(1),
            };
            var confirm = new Button
            {
                Content = "Clear stats", FontSize = 11, Height = 24, Padding = new Thickness(12, 2),
                Background = new SolidColorBrush(Color.FromRgb(0x6E, 0x1E, 0x1E)), Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x8A)), BorderThickness = new Thickness(1),
            };
            cancel.Click  += (_, _) => ClosePicker();
            confirm.Click += (_, _) =>
            {
                if (_clearStats != null) { try { _clearStats(); } catch { } }
                ClosePicker();
                _lastJson = "";                       // force the next poll to re-read the zeroed stats
                Dispatcher.UIThread.Post(Poll);
            };
            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Right,
                Children = { cancel, confirm },
            };
            var dialog = new Border
            {
                Width = 360,
                Background = PickerBg, BorderBrush = PickerBorder, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6), Padding = new Thickness(14, 12),
                Child = new StackPanel { Orientation = Orientation.Vertical, Spacing = 10, Children = { msg, btnRow } },
            };
            double left = Math.Max(4, (Root.Bounds.Width - 360) / 2);
            double top  = Math.Max(8, (Root.Bounds.Height - 150) / 3);
            _pickerCanvas.Children.Add(dialog);
            Canvas.SetLeft(dialog, left);
            Canvas.SetTop(dialog, top);
            _activePicker = dialog;
            _pickerCanvas.IsHitTestVisible = true;
        }

        // ── Per-monster debuff drawer ────────────────────────────────────────────
        // Debuffs/shapes are name-keyed on the shared MonsterRule (combat reads it via
        // BuildDebuffList / the shape gate), with a "Default" rule every monster inherits.
        // The drawer edits an EXACT-name rule for the monster: editing while inheriting
        // clones Default into one (so other config carries over); Reset deletes it.

        private MonstersPanel.Rule? FindRule(string name) =>
            _monsters.Rules?.FirstOrDefault(r =>
                !r.Name.Equals("Default", StringComparison.OrdinalIgnoreCase)
                && r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        private bool HasCustomRule(string name) => FindRule(name) != null;

        private MonstersPanel.Rule DefaultRule() =>
            _monsters.Rules?.FirstOrDefault(r => r.Name.Equals("Default", StringComparison.OrdinalIgnoreCase))
            ?? new MonstersPanel.Rule { Name = "Default", UseBolt = true };

        private static MonstersPanel.Rule CloneRule(MonstersPanel.Rule s, string name) => new MonstersPanel.Rule
        {
            Name = name,
            Category = s.Category,
            MatchExpression = "",   // exact-name rule matches by name, not a (possibly false) expression
            Priority = s.Priority,
            DamageType = s.DamageType,
            WeaponId = s.WeaponId,
            OffhandId = s.OffhandId,
            ExVuln = s.ExVuln,
            PetDamage = s.PetDamage,
            Fester = s.Fester, Broadside = s.Broadside, GravityWell = s.GravityWell,
            Imperil = s.Imperil, Yield = s.Yield, Vuln = s.Vuln,
            UseArc = s.UseArc, UseBolt = s.UseBolt, UseRing = s.UseRing, UseStreak = s.UseStreak,
        };

        // Force a fresh re-read of the rules right before a write. Both panels run on the
        // single UI thread, so re-reading here means the drawer builds its edit on the very
        // latest plugin state and can't clobber a rule just added on the Monsters tab.
        private void RefreshMonstersNow() { _lastMonstersJson = ""; RefreshMonsters(); }

        // Return the editable exact-name rule, creating it (cloned from Default) if the
        // monster is currently inheriting. Inserted right after Default so the more-specific
        // rule wins GetRuleForTarget's first-substring-match resolution.
        private MonstersPanel.Rule EnsureCustomRule(string name)
        {
            RefreshMonstersNow();
            // The Default line edits the real Default rule in place (not a clone).
            if (name.Equals("Default", StringComparison.OrdinalIgnoreCase))
            {
                var def = DefaultRule();
                if (_monsters.Rules != null && !_monsters.Rules.Contains(def)) _monsters.Rules.Add(def);
                return def;
            }
            var existing = FindRule(name);
            if (existing != null) return existing;
            var clone = CloneRule(DefaultRule(), name);
            int defIdx = _monsters.Rules.FindIndex(r => r.Name.Equals("Default", StringComparison.OrdinalIgnoreCase));
            _monsters.Rules.Insert(defIdx >= 0 ? defIdx + 1 : 0, clone);
            return clone;
        }

        private void ResetRule(string name)
        {
            RefreshMonstersNow();
            int i = _monsters.Rules.FindIndex(r =>
                !r.Name.Equals("Default", StringComparison.OrdinalIgnoreCase)
                && r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (i < 0) return;
            _monsters.Rules.RemoveAt(i);
            PushMonsters();
            RebuildDrawer();
        }

        // Whole-rules-array write-back over the existing Monsters bridge (same shape the
        // Monsters tab pushes). The plugin's ApplyMonstersJson preserves the Default rule.
        private void PushMonsters()
        {
            if (_setMonsters == null) return;
            // Never write back an empty/un-loaded array — that would wipe the user's rules
            // (incl. Default) on the plugin side.
            if (_monsters?.Rules == null || _monsters.Rules.Count == 0) return;
            IntPtr ansi = IntPtr.Zero;
            try
            {
                string json = JsonSerializer.Serialize(
                    new MonstersPanel.Payload { Rules = _monsters.Rules },
                    MonstersPanelJsonContext.Default.Payload);
                ansi = Marshal.StringToHGlobalAnsi(json);
                _setMonsters(ansi);
            }
            catch { }
            finally { if (ansi != IntPtr.Zero) Marshal.FreeHGlobal(ansi); }
            _lastMonstersJson = ""; // re-read the plugin's applied copy on the next poll
        }

        private void RebuildDrawer()
        {
            _displayedKeys = new List<string> { "\0force" }; // force a structural rebuild → drawer re-renders
            Reconcile();
        }

        private Control BuildDebuffDrawer(Row row)
        {
            string monsterName = row.Name;
            var border = new Border
            {
                Background = PickerBg,
                BorderBrush = PickerBorder,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(4, 0, 4, 2),
                Padding = new Thickness(8, 6),
            };
            var stack = new StackPanel { Orientation = Orientation.Vertical, Spacing = 5 };
            border.Child = stack;

            // Guard: never write back an empty/un-loaded rules array (would clobber Default).
            if (_setMonsters == null || _monsters.Rules == null || _monsters.Rules.Count == 0)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = "Monster rules not loaded yet — open once the plugin is in-world.",
                    FontSize = 11, Foreground = Dim,
                });
                return border;
            }

            var exact = FindRule(monsterName);
            bool custom = exact != null;
            var view = exact ?? DefaultRule();

            // Header: title + source + reset.
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            header.Children.Add(new TextBlock
            {
                Text = "Debuffs · " + monsterName, FontSize = 11, FontWeight = FontWeight.Bold,
                Foreground = PickerBorder, VerticalAlignment = VerticalAlignment.Center,
            });
            bool isDefaultRule = monsterName.Equals("Default", StringComparison.OrdinalIgnoreCase);
            header.Children.Add(new TextBlock
            {
                Text = isDefaultRule ? "(applies to every monster set to Default)"
                                     : (custom ? "(custom)" : "(inheriting Default)"),
                FontSize = 10,
                Foreground = custom || isDefaultRule ? ManualHp : Dim, VerticalAlignment = VerticalAlignment.Center,
            });
            var reset = new Button
            {
                Content = "Reset to Default", FontSize = 10, Height = 20, Padding = new Thickness(6, 1),
                Background = EntryBg, Foreground = custom ? Brushes.White : Dim,
                BorderBrush = EntryBorder, BorderThickness = new Thickness(1), IsEnabled = custom,
            };
            reset.Click += (_, _) => ResetRule(monsterName);
            header.Children.Add(reset);
            stack.Children.Add(header);

            // Debuffs.
            var debuffRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            debuffRow.Children.Add(LabelTag("Debuffs:"));
            foreach (var (field, label, tip) in DebuffDefs)
                debuffRow.Children.Add(DrawerToggle(monsterName, field, label, tip, view.GetToggle(field)));
            stack.Children.Add(debuffRow);

            // Spell shapes.
            var shapeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            shapeRow.Children.Add(LabelTag("Shapes:"));
            foreach (var (field, label, tip) in ShapeDefs)
                shapeRow.Children.Add(DrawerToggle(monsterName, field, label, tip, view.GetToggle(field)));
            stack.Children.Add(shapeRow);

            // Extra Vuln element (a small dropdown via the existing picker).
            var exRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            exRow.Children.Add(LabelTag("Extra Vuln:"));
            string curEx = string.IsNullOrEmpty(view.ExVuln) ? "None" : view.ExVuln;
            var exBtn = MakePickerButton();
            exBtn.Width = 120; exBtn.HorizontalAlignment = HorizontalAlignment.Left; exBtn.Content = curEx;
            exBtn.Click += (_, _) =>
            {
                var items = new List<(uint Id, string Name)>();
                for (uint i = 0; i < ExVulnTypes.Length; i++) items.Add((i, ExVulnTypes[i]));
                // Read the current value at click time (not the build-time closure) so an
                // external edit doesn't make the picker preselect the wrong entry.
                string nowEx = (FindRule(monsterName) ?? DefaultRule()).ExVuln;
                if (string.IsNullOrEmpty(nowEx)) nowEx = "None";
                uint sel = (uint)Math.Max(0, Array.IndexOf(ExVulnTypes, nowEx));
                ShowPicker(exBtn, items, sel, id =>
                {
                    EnsureCustomRule(monsterName).ExVuln = ExVulnTypes[id];
                    PushMonsters();
                    RebuildDrawer();
                });
            };
            exRow.Children.Add(exBtn);
            stack.Children.Add(exRow);

            // Per-tier breakdown — the "all tiers" view. One line per tier the monster was killed
            // with (most-killed first); ring tiers render as R#. The collapsed row above shows only
            // the latest tier, so this is where the full history lives.
            if (row.Tiers != null && row.Tiers.Count > 0)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = "Tiers used", FontSize = 11, FontWeight = FontWeight.Bold,
                    Foreground = PickerBorder, Margin = new Thickness(0, 4, 0, 0),
                });
                foreach (var t in row.Tiers)
                {
                    string crit = t.CritN > 0 ? t.Crit.ToString("0", CultureInfo.InvariantCulture) : "—";
                    string nc   = t.NonCritN > 0 ? t.NonCrit.ToString("0", CultureInfo.InvariantCulture) : "—";
                    string ck   = t.Kills > 0 ? t.Casts.ToString("0.00", CultureInfo.InvariantCulture) : "—";
                    string line = $"{FormatTier(t.Tier),-4} {(string.IsNullOrEmpty(t.Elem) ? "" : t.Elem),-8} kills {t.Kills,-4} crit {crit,-5} non-crit {nc,-5} casts/kill {ck}";
                    stack.Children.Add(new TextBlock
                    {
                        Text = line, FontSize = 11, Foreground = Brushes.White,
                        FontFamily = new FontFamily("Consolas"),
                    });
                }
            }

            return border;
        }

        private Control DrawerToggle(string monsterName, string field, string label, string tip, bool on)
        {
            var rect = new Border
            {
                Width = 11, Height = 11, CornerRadius = new CornerRadius(2),
                Background = on ? ToggleOn : ToggleOff, VerticalAlignment = VerticalAlignment.Center,
            };
            var btn = new Button
            {
                Height = 18, Padding = new Thickness(3, 0),
                Background = EntryBg, BorderBrush = EntryBorder, BorderThickness = new Thickness(1),
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 3,
                    Children =
                    {
                        rect,
                        new TextBlock { Text = label, FontSize = 10, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center },
                    },
                },
            };
            ToolTip.SetTip(btn, tip);
            btn.Click += (_, _) =>
            {
                var rule = EnsureCustomRule(monsterName);
                bool nv = !rule.GetToggle(field);
                rule.SetToggle(field, nv);
                // Never leave a monster with no offensive shape (combat would refuse to cast).
                if (IsShape(field) && !nv && NoShapeOn(rule)) rule.UseBolt = true;
                PushMonsters();
                RebuildDrawer();
            };
            return btn;
        }

        private static TextBlock LabelTag(string t) => new TextBlock
        {
            Text = t, FontSize = 10, Foreground = Dim, Width = 64,
            VerticalAlignment = VerticalAlignment.Center,
        };

        private static Grid NewRowGrid(double height) => new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(Cols),
            Height = height,
        };

        private Control HeaderRow()
        {
            var g = NewRowGrid(22);
            g.Background = HeaderBg;
            AddCell(g, 0, "WCID",       bold: true);
            AddCell(g, 1, "Monster",    bold: true);
            AddCell(g, 2, "Elem",       bold: true);
            AddCell(g, 3, "Tier",       bold: true);
            AddCell(g, 4, "HP",         bold: true);
            AddCell(g, 5, "Crit",       bold: true);
            AddCell(g, 6, "NonCrit",    bold: true);
            AddCell(g, 7, "Casts/Kill", bold: true);
            AddCell(g, 8, "Kills",      bold: true);
            AddCell(g, 9, "Weapon",     bold: true);
            AddCell(g, 10, "Offhand",   bold: true);
            AddCell(g, 11, "",          bold: true);
            return g;
        }

        // Compact dropdown-anchor button used in the Weapon/Offhand cells.
        private static Button MakePickerButton() => new Button
        {
            Content = "Auto",
            FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(4, 0),
            Margin = new Thickness(1, 1),
            Height = 18,
            Background = EntryBg,
            Foreground = Brushes.White,
            BorderBrush = EntryBorder,
            BorderThickness = new Thickness(1),
        };

        private RowWidgets CreateRow(Row r)
        {
            if (r.IsDefault) return CreateDefaultRow();
            var g = NewRowGrid(22);
            g.Background = RowBg;
            var rw = new RowWidgets { Root = g };

            rw.Wcid    = AddCell(g, 0, "", brush: Dim);

            // Col 1: a ▸/▾ chevron (toggles this monster's debuff drawer) + the name.
            var chevron = new Button
            {
                Content = "▸", FontSize = 10, Width = 16, Height = 18,
                Padding = new Thickness(0), Margin = new Thickness(0, 0, 2, 0),
                Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Foreground = Dim,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(chevron, "Per-monster debuffs, spell shapes & tier breakdown");
            chevron.Click += (_, _) =>
            {
                uint w = rw.Data.Wcid;
                _expandedWcid = _expandedWcid == w ? 0u : w;
                _displayedKeys = new List<string> { "\0force" }; // force a rebuild to insert/remove the drawer
                Reconcile();
            };
            // "D" Default checkbox: on = this monster follows the Default config (debuffs/shapes +
            // weapon); off = its own custom rule. Checked when it has no custom rule AND no weapon override.
            var dToggle = new Button
            {
                Content = "D", FontSize = 9, Width = 16, Height = 18,
                Padding = new Thickness(0), Margin = new Thickness(0, 0, 3, 0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = EntryBg, Foreground = Dim,
                BorderBrush = EntryBorder, BorderThickness = new Thickness(1),
            };
            ToolTip.SetTip(dToggle, "Follow the Default config (debuffs/shapes/weapon). Uncheck to customize this monster.");
            dToggle.Click += (_, _) =>
            {
                var cur = rw.Data;
                bool onDefault = !HasCustomRule(cur.Name) && cur.AssignedWid == 0;
                if (onDefault)
                {
                    EnsureCustomRule(cur.Name);   // turn OFF default → give it an editable custom rule
                    PushMonsters();
                }
                else
                {
                    ResetRule(cur.Name);          // turn ON default → drop custom rule (PushMonsters inside)
                    if (_setWeapon != null && cur.Wcid != 0) { try { _setWeapon(cur.Wcid, 0); } catch { } } // weapon → Default
                }
                _lastJson = "";
                Dispatcher.UIThread.Post(Poll);
            };
            rw.DefaultToggle = dToggle;

            var nameText = new TextBlock
            {
                FontSize = 11, Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var nameCell = new DockPanel { Margin = new Thickness(2, 0, 2, 0) };
            DockPanel.SetDock(chevron, Dock.Left);
            DockPanel.SetDock(dToggle, Dock.Left);
            nameCell.Children.Add(chevron);
            nameCell.Children.Add(dToggle);
            nameCell.Children.Add(nameText);
            rw.Chevron = chevron;
            rw.Name = nameText;
            g.Children.Add(nameCell); Grid.SetColumn(nameCell, 1);

            rw.Elem    = AddCell(g, 2, "");
            rw.Tier    = AddCell(g, 3, "");

            var hp = new TextBox
            {
                FontSize = 11, MinHeight = 18, Height = 18,
                Padding = new Thickness(2, 0), Margin = new Thickness(1, 1),
                VerticalContentAlignment = VerticalAlignment.Center,
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x16, 0x22, 0x2E)),
                Watermark = "?",
                IsReadOnly = !r.HpManual,   // auto = read-only display; manual = editable
            };
            uint wcid = r.Wcid;
            void CommitHp()
            {
                if (_setHp == null) return;
                string t = (hp.Text ?? "").Trim();
                if (t.Length == 0) { try { _setHp(wcid, 0); } catch { } _lastJson = ""; return; }
                if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && v > 0)
                {
                    try { _setHp(wcid, v); } catch { }
                    _lastJson = ""; // force a refresh so the gold "manual" flag shows
                }
            }
            hp.GotFocus  += (_, _) => { _editing = true;  Win32Backend.AvaloniaTextInputActive = true; };
            hp.LostFocus += (_, _) => { Win32Backend.AvaloniaTextInputActive = false; _editing = false; CommitHp(); };
            hp.KeyDown   += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    CommitHp();
                    _editing = false;
                    Win32Backend.AvaloniaTextInputActive = false;
                    e.Handled = true;
                }
            };
            rw.Hp = hp;

            // "M" per-row manual-HP toggle. On = manual entry (box editable, gold);
            // off = auto only (box read-only, shows the learned/appraised HP). Reuses the
            // existing RynthPluginSetMonsterHp plumbing: hp>0 sets manual, 0 clears -> auto.
            var mToggle = new Button
            {
                Content = "M", FontSize = 9, Width = 16, Height = 18,
                Padding = new Thickness(0), Margin = new Thickness(0, 1, 2, 1),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = EntryBg, Foreground = Dim,
                BorderBrush = EntryBorder, BorderThickness = new Thickness(1),
            };
            ToolTip.SetTip(mToggle, "Manual max-HP for this monster (off = auto)");
            mToggle.Click += (_, _) =>
            {
                if (_setHp == null) return;
                var cur = rw.Data;
                if (cur.HpManual)
                {
                    try { _setHp(cur.Wcid, 0); } catch { }   // manual -> auto
                    _lastJson = "";
                }
                else
                {
                    // auto -> manual: seed with the known auto HP (if any), then open the box.
                    if (cur.Hp > 0) { try { _setHp(cur.Wcid, cur.Hp); } catch { } _lastJson = ""; }
                    rw.Hp.IsReadOnly = false;
                    rw.Hp.Focus();
                }
            };
            rw.HpManualToggle = mToggle;

            var hpCell = new DockPanel();
            DockPanel.SetDock(mToggle, Dock.Left);
            hpCell.Children.Add(mToggle);
            hpCell.Children.Add(hp);
            g.Children.Add(hpCell); Grid.SetColumn(hpCell, 4);

            rw.Crit    = AddCell(g, 5, "");
            rw.NonCrit = AddCell(g, 6, "");
            rw.Casts   = AddCell(g, 7, "");
            rw.Kills   = AddCell(g, 8, "");

            // Weapon (col 9): per-monster pick. "Auto (best: …)" (id 0) clears the override.
            var weaponBtn = MakePickerButton();
            ToolTip.SetTip(weaponBtn, "Weapon to use on this monster. Auto = the learned best (fewest casts to kill).");
            weaponBtn.Click += (_, _) =>
            {
                var cur = rw.Data;
                string bestLabel = cur.BestWid != 0
                    ? "Auto (best: " + Trunc(cur.BestWeapon, 16) + ")"
                    : "Auto (best: —)";
                var choices = new List<(uint Id, string Name)> { (0u, bestLabel) };
                choices.AddRange(WeaponChoices());
                ShowPicker(weaponBtn, choices, cur.AssignedWid, id =>
                {
                    if (_setWeapon != null) { try { _setWeapon(cur.Wcid, id); } catch { } }
                    _lastJson = "";
                    Dispatcher.UIThread.Post(Poll);
                });
            };
            rw.Weapon = weaponBtn;
            g.Children.Add(weaponBtn); Grid.SetColumn(weaponBtn, 9);

            // Offhand (col 10): per-monster pick (stored only — combat does not equip it).
            var offhandBtn = MakePickerButton();
            ToolTip.SetTip(offhandBtn, "Offhand for this monster (saved per-monster; combat does not auto-equip it yet).");
            offhandBtn.Click += (_, _) =>
            {
                var cur = rw.Data;
                var choices = new List<(uint Id, string Name)> { (0u, "(none)") };
                choices.AddRange(WeaponChoices());
                ShowPicker(offhandBtn, choices, cur.AssignedOff, id =>
                {
                    if (_setOffhand != null) { try { _setOffhand(cur.Wcid, id); } catch { } }
                    _lastJson = "";
                    Dispatcher.UIThread.Post(Poll);
                });
            };
            rw.Offhand = offhandBtn;
            g.Children.Add(offhandBtn); Grid.SetColumn(offhandBtn, 10);

            var del = new Button
            {
                Content = "✕", FontSize = 11, Padding = new Thickness(0),
                Width = 22, Height = 18, Margin = new Thickness(1),
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x8A)),
                Background = Brushes.Transparent,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(del, $"Delete learned history for this row (wcid {r.Wcid}).");
            string key = r.Key;
            del.Click += (_, _) =>
            {
                if (_delRow != null && key.Length > 0)
                {
                    IntPtr k = Marshal.StringToHGlobalAnsi(key);
                    try { _delRow(k); } catch { } finally { Marshal.FreeHGlobal(k); }
                }
                _lastJson = "";
                Dispatcher.UIThread.Post(Poll);
            };
            g.Children.Add(del); Grid.SetColumn(del, 11);

            rw.Data = r;
            return rw;
        }

        // The synthetic "Default" line at the top: a chevron to edit the Default debuffs/shapes
        // rule, and a weapon picker that sets the per-character default weapon (sweeps all Default
        // monsters). Other columns are N/A.
        private RowWidgets CreateDefaultRow()
        {
            var g = NewRowGrid(22);
            g.Background = HeaderBg;
            var rw = new RowWidgets { Root = g };

            rw.Wcid = AddCell(g, 0, "", brush: Dim);

            var chevron = new Button
            {
                Content = "▸", FontSize = 10, Width = 16, Height = 18,
                Padding = new Thickness(0), Margin = new Thickness(0, 0, 2, 0),
                Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Foreground = Dim,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(chevron, "Edit the Default debuffs & spell shapes — applies to every monster set to Default");
            chevron.Click += (_, _) =>
            {
                _defaultExpanded = !_defaultExpanded;
                _displayedKeys = new List<string> { "\0force" };
                Reconcile();
            };
            var nameText = new TextBlock
            {
                Text = "DEFAULT", FontSize = 11, FontWeight = FontWeight.Bold,
                Foreground = PickerBorder, VerticalAlignment = VerticalAlignment.Center,
            };
            var nameCell = new DockPanel { Margin = new Thickness(2, 0, 2, 0) };
            DockPanel.SetDock(chevron, Dock.Left);
            nameCell.Children.Add(chevron);
            nameCell.Children.Add(nameText);
            rw.Chevron = chevron;
            rw.Name = nameText;
            g.Children.Add(nameCell); Grid.SetColumn(nameCell, 1);

            AddCell(g, 8, "weapon →", brush: Dim);

            var weaponBtn = MakePickerButton();
            ToolTip.SetTip(weaponBtn, "Default weapon — used by every monster set to Default (no own override).");
            weaponBtn.Click += (_, _) =>
            {
                var cur = rw.Data;
                var choices = new List<(uint Id, string Name)> { (0u, "Auto (learned best)") };
                choices.AddRange(WeaponChoices());
                ShowPicker(weaponBtn, choices, cur.AssignedWid, id =>
                {
                    if (_setDefaultWeapon != null) { try { _setDefaultWeapon(id); } catch { } }
                    _lastJson = "";
                    Dispatcher.UIThread.Post(Poll);
                });
            };
            rw.Weapon = weaponBtn;
            g.Children.Add(weaponBtn); Grid.SetColumn(weaponBtn, 9);

            return rw;
        }

        private void UpdateRow(RowWidgets rw, Row r)
        {
            rw.Data = r; // picker click handlers read the latest best/override from here
            if (r.IsDefault)
            {
                rw.Chevron.Content = _defaultExpanded ? "▾" : "▸";
                rw.Weapon.Content = r.AssignedWid != 0
                    ? Trunc(r.AssignedWeapon.Length > 0 ? r.AssignedWeapon : "Weapon " + r.AssignedWid, 16)
                    : "Auto (learned best)";
                rw.Weapon.Foreground = r.AssignedWid != 0 ? ManualHp : Dim;
                return;
            }
            rw.Wcid.Text    = r.Wcid.ToString(CultureInfo.InvariantCulture);
            rw.Name.Text    = r.Name;
            // Chevron: ▾ when this monster's drawer is open; gold when it has its own
            // debuff rule (vs inheriting Default) — glanceable while scanning the list.
            bool expanded = _expandedWcid == r.Wcid;
            rw.Chevron.Content = expanded ? "▾" : "▸";
            rw.Chevron.Foreground = HasCustomRule(r.Name) ? ManualHp : Dim;
            rw.Elem.Text    = r.Elem;
            rw.Tier.Text    = FormatTier(r.Tier);
            rw.Crit.Text    = r.CritN    > 0 ? r.Crit.ToString("0")    : "—";
            rw.Crit.Foreground    = r.CritN    > 0 ? Brushes.White : Dim;
            rw.NonCrit.Text = r.NonCritN > 0 ? r.NonCrit.ToString("0") : "—";
            rw.NonCrit.Foreground = r.NonCritN > 0 ? Brushes.White : Dim;
            rw.Casts.Text   = r.Kills > 0 ? r.Casts.ToString("0.00") : "—";
            rw.Kills.Text   = r.Kills.ToString();

            // Weapon: gold = pinned override, dim "Auto: <best>" = following the recommendation.
            if (r.AssignedWid != 0)
            {
                rw.Weapon.Content = Trunc(r.AssignedWeapon.Length > 0 ? r.AssignedWeapon : "Weapon " + r.AssignedWid, 16);
                rw.Weapon.Foreground = ManualHp;
            }
            else
            {
                rw.Weapon.Content = r.BestWid != 0 ? "Auto: " + Trunc(r.BestWeapon, 12) : "Auto";
                rw.Weapon.Foreground = Dim;
            }
            // Offhand: gold = set, dim "(none)" otherwise.
            if (r.AssignedOff != 0)
            {
                rw.Offhand.Content = Trunc(r.AssignedOffName.Length > 0 ? r.AssignedOffName : "Offhand " + r.AssignedOff, 16);
                rw.Offhand.Foreground = ManualHp;
            }
            else
            {
                rw.Offhand.Content = "(none)";
                rw.Offhand.Foreground = Dim;
            }

            // Never overwrite the HP box while the user is editing it.
            if (!rw.Hp.IsFocused)
            {
                string hpText = r.Hp > 0 ? r.Hp.ToString(CultureInfo.InvariantCulture) : "";
                if ((rw.Hp.Text ?? "") != hpText) rw.Hp.Text = hpText;
                rw.Hp.Foreground = r.HpManual ? ManualHp : Brushes.White;
                rw.Hp.IsReadOnly = !r.HpManual;   // auto = read-only (display only); manual = editable
            }
            // "M" toggle visual: gold = manual override active, dim = auto.
            rw.HpManualToggle.Foreground  = r.HpManual ? ManualHp : Dim;
            rw.HpManualToggle.BorderBrush = r.HpManual ? ManualHp : EntryBorder;

            // "D" Default toggle visual: green = following Default (no custom rule, no weapon override).
            bool onDefault = !HasCustomRule(r.Name) && r.AssignedWid == 0;
            rw.DefaultToggle.Foreground  = onDefault ? ToggleOn : Dim;
            rw.DefaultToggle.BorderBrush = onDefault ? ToggleOn : EntryBorder;
        }

        // Tier display: 0 = none ("—"); negative = ring spell ("R<level>"); positive = spell level.
        private static string FormatTier(int t) =>
            t == 0 ? "—" : (t < 0 ? "R" + (-t).ToString(CultureInfo.InvariantCulture)
                                  : t.ToString(CultureInfo.InvariantCulture));

        private static TextBlock AddCell(Grid g, int col, string text, bool bold = false, bool trim = false, IBrush? brush = null)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontSize = 11,
                FontWeight = bold ? FontWeight.Bold : FontWeight.Normal,
                Foreground = brush ?? Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 2, 0),
                TextTrimming = trim ? TextTrimming.CharacterEllipsis : TextTrimming.None,
            };
            g.Children.Add(tb);
            Grid.SetColumn(tb, col);
            return tb;
        }

        private static string Trunc(string s, int max) =>
            string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max - 1) + "…");
    }
}
