// ============================================================================
//  RynthCore.Engine — UI/Panels/MonsterDamagePanel.cs
//  Interactive table of the RynthAi plugin's learned per-monster combat data.
//  Bridge exports (polled every 500ms):
//    RynthPluginGetMonsterDamageJson() → JSON rows (STABLE order)
//    RynthPluginSetMonsterHp(wcid, hp) → manual HP override
//    RynthPluginDeleteMonsterRow(key)  → delete one learned row
//  Columns: WCID | Monster | Elem | Tier | HP(edit) | Crit | NonCrit | Casts/Kill | Kills | ✕
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

    private static GetJsonFn? _getJson;
    private static SetHpFn?   _setHp;
    private static DelRowFn?  _delRow;

    private static void TryBind()
    {
        if (_getJson != null) return;
        LoadedPlugin? plugin = PluginManager.Plugins.FirstOrDefault(
            p => p.DisplayName.Contains("RynthAi", StringComparison.OrdinalIgnoreCase));
        if (plugin == null || plugin.ModuleHandle == IntPtr.Zero) return;

        IntPtr a = GetProcAddress(plugin.ModuleHandle, "RynthPluginGetMonsterDamageJson");
        IntPtr b = GetProcAddress(plugin.ModuleHandle, "RynthPluginSetMonsterHp");
        IntPtr c = GetProcAddress(plugin.ModuleHandle, "RynthPluginDeleteMonsterRow");
        if (a != IntPtr.Zero) _getJson = Marshal.GetDelegateForFunctionPointer<GetJsonFn>(a);
        if (b != IntPtr.Zero) _setHp   = Marshal.GetDelegateForFunctionPointer<SetHpFn>(b);
        if (c != IntPtr.Zero) _delRow  = Marshal.GetDelegateForFunctionPointer<DelRowFn>(c);
    }

    internal static Control Create() => new View().Root;

    private const string Cols = "60,156,54,36,58,56,60,68,48,26";

    private static readonly IBrush HeaderBg = new SolidColorBrush(Color.FromArgb(0xFF, 0x12, 0x1C, 0x26));
    private static readonly IBrush PanelBg  = new SolidColorBrush(Color.FromArgb(0xF0, 0x08, 0x10, 0x18));
    private static readonly IBrush RowBg    = new SolidColorBrush(Color.FromArgb(0x40, 0x20, 0x30, 0x40));
    private static readonly IBrush ManualHp = new SolidColorBrush(Color.FromRgb(0xFF, 0xD1, 0x6A)); // gold = user-set
    private static readonly IBrush Dim      = new SolidColorBrush(Color.FromRgb(0x8A, 0x9A, 0xA8));

    private sealed class Row
    {
        public uint Wcid; public string Name = ""; public uint Wid; public string Weapon = "";
        public string Elem = ""; public int Tier; public int Hp; public bool HpManual;
        public double Crit; public int CritN; public double NonCrit; public int NonCritN;
        public double Casts; public int Kills; public string Key = "";
    }

    private sealed class RowWidgets
    {
        public Grid Root = null!;
        public TextBlock Wcid = null!, Name = null!, Elem = null!, Tier = null!;
        public TextBlock Crit = null!, NonCrit = null!, Casts = null!, Kills = null!;
        public TextBox Hp = null!;
    }

    private sealed class View
    {
        public readonly Control Root;
        private readonly StackPanel _rows;
        private readonly Control _header;
        private readonly Button _filterBtn;
        private readonly TextBlock _status;

        private readonly Dictionary<string, RowWidgets> _cache = new();
        private List<string> _displayedKeys = new();

        private uint   _filterWid;                 // 0 = all weapons
        private string _filterName = "All weapons";
        private string _lastJson = "";
        private bool   _editing;                   // an HP box is focused → defer structural rebuilds
        private List<Row> _data = new();
        private readonly List<(uint Wid, string Name)> _weapons = new();

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
            _filterBtn.Click += (_, _) => CycleFilter();

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
                Children = { _filterBtn, _status },
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

            var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Background = PanelBg };
            grid.Children.Add(top);    Grid.SetRow(top, 0);
            grid.Children.Add(scroll); Grid.SetRow(scroll, 1);
            Root = grid;

            TryBind();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            timer.Tick += (_, _) => Poll();
            timer.Start();
            // Stop with the visual tree (RadarPanel idiom): a running
            // DispatcherTimer roots the closed panel view forever — every
            // open/close otherwise adds another immortal poller hitting the
            // plugin C exports against a detached tree.
            grid.AttachedToVisualTree   += (_, _) => { if (!timer.IsEnabled) timer.Start(); };
            grid.DetachedFromVisualTree += (_, _) => timer.Stop();
            Poll();
        }

        private void Poll()
        {
            if (_getJson == null) { TryBind(); if (_getJson == null) { _status.Text = "Waiting for RynthAi plugin…"; return; } }

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

        private void Reconcile()
        {
            List<Row> filtered = _filterWid == 0 ? _data : _data.Where(r => r.Wid == _filterWid).ToList();
            var newKeys = filtered.Select(r => r.Key).ToList();
            bool structural = !newKeys.SequenceEqual(_displayedKeys);

            if (structural && !_editing)
            {
                _rows.Children.Clear();
                _rows.Children.Add(_header);
                foreach (var r in filtered)
                {
                    if (!_cache.TryGetValue(r.Key, out var rw)) { rw = CreateRow(r); _cache[r.Key] = rw; }
                    _rows.Children.Add(rw.Root);
                    UpdateRow(rw, r);
                }
                // prune cache entries no longer shown
                var keep = new HashSet<string>(newKeys);
                foreach (var k in _cache.Keys.Where(k => !keep.Contains(k)).ToList()) _cache.Remove(k);
                _displayedKeys = newKeys;

                if (filtered.Count == 0)
                    _rows.Children.Add(new TextBlock { Text = "No rows yet — fight monsters with magic.", FontSize = 11, Foreground = Dim, Margin = new Thickness(4, 8) });
            }
            else
            {
                // Same row set (or a structural change deferred while editing): just
                // refresh the cells of the rows we already show — controls untouched.
                foreach (var r in filtered)
                    if (_cache.TryGetValue(r.Key, out var rw)) UpdateRow(rw, r);
            }
        }

        private static List<Row> Parse(string json)
        {
            var list = new List<Row>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                foreach (var e in doc.RootElement.EnumerateArray())
                {
                    list.Add(new Row
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
                    });
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

        private void CycleFilter()
        {
            if (_weapons.Count == 0) { _filterWid = 0; _filterName = "All weapons"; }
            else
            {
                int idx = _filterWid == 0 ? -1 : _weapons.FindIndex(w => w.Wid == _filterWid);
                idx++;
                if (idx >= _weapons.Count) { _filterWid = 0; _filterName = "All weapons"; }
                else { _filterWid = _weapons[idx].Wid; _filterName = _weapons[idx].Name; }
            }
            _filterBtn.Content = "Weapon: " + Trunc(_filterName, 22) + " ▾";
            // Force a structural rebuild for the new filter.
            _displayedKeys = new List<string> { "\0force" };
            Reconcile();
        }

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
            AddCell(g, 9, "",           bold: true);
            return g;
        }

        private RowWidgets CreateRow(Row r)
        {
            var g = NewRowGrid(22);
            g.Background = RowBg;
            var rw = new RowWidgets { Root = g };

            rw.Wcid    = AddCell(g, 0, "", brush: Dim);
            rw.Name    = AddCell(g, 1, "", trim: true);
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
            g.Children.Add(hp); Grid.SetColumn(hp, 4);

            rw.Crit    = AddCell(g, 5, "");
            rw.NonCrit = AddCell(g, 6, "");
            rw.Casts   = AddCell(g, 7, "");
            rw.Kills   = AddCell(g, 8, "");

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
            g.Children.Add(del); Grid.SetColumn(del, 9);

            return rw;
        }

        private void UpdateRow(RowWidgets rw, Row r)
        {
            rw.Wcid.Text    = r.Wcid.ToString(CultureInfo.InvariantCulture);
            rw.Name.Text    = r.Name;
            rw.Elem.Text    = r.Elem;
            rw.Tier.Text    = r.Tier > 0 ? r.Tier.ToString() : "—";
            rw.Crit.Text    = r.CritN    > 0 ? r.Crit.ToString("0")    : "—";
            rw.Crit.Foreground    = r.CritN    > 0 ? Brushes.White : Dim;
            rw.NonCrit.Text = r.NonCritN > 0 ? r.NonCrit.ToString("0") : "—";
            rw.NonCrit.Foreground = r.NonCritN > 0 ? Brushes.White : Dim;
            rw.Casts.Text   = r.Kills > 0 ? r.Casts.ToString("0.00") : "—";
            rw.Kills.Text   = r.Kills.ToString();

            // Never overwrite the HP box while the user is editing it.
            if (!rw.Hp.IsFocused)
            {
                string hpText = r.Hp > 0 ? r.Hp.ToString(CultureInfo.InvariantCulture) : "";
                if ((rw.Hp.Text ?? "") != hpText) rw.Hp.Text = hpText;
                rw.Hp.Foreground = r.HpManual ? ManualHp : Brushes.White;
            }
        }

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
