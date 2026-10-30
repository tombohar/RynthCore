// ============================================================================
//  RynthCore.Engine - ImGui/Panels/Classic/ClassicBuffsTab.cs
//  The Classic look's Buffs tab: the buff profile (the dashboard's Buffs
//  picker and RynthAi's Spells window), buffing on/off, Rebuff all / Cancel,
//  and every spell on you now with its icon and time left, soonest first,
//  harmful ones on top in red. The list is the engine's player enchantment
//  snapshot (EnchantmentHooks.CopyPlayerStatMods: refreshed by the main thread
//  every 250 ms, read here four times a second); names and icons come from the
//  client's spell table (PortalSpellTable), the icons drawn by RetailSprites.
//  What a buff profile holds is RynthAi's (its Spells window), not shown here.
//  AC's render thread only.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using RynthCore.Engine.Compatibility;
using RynthCore.Engine.ImGuiBackend.Hud;
using RynthCore.Engine.UI;
using RynthCore.Engine.UI.Data;
using static RynthCore.Engine.ImGuiBackend.Panels.Classic.ClassicKit;

namespace RynthCore.Engine.ImGuiBackend.Panels.Classic;

internal sealed class ClassicBuffsTab : IClassicTab
{
    public string Name => "Buffs";

    private const int MaxEnch = 256, VisibleRows = 16;
    private const double NoEnd = 1e12;

    private sealed class Line
    {
        public uint SpellId;
        public string Name = "";
        public double Expiry;
        public bool Harmful;
        public string Left = "";
        public uint LeftColor;
    }

    private readonly uint[] _ids = new uint[MaxEnch], _types = new uint[MaxEnch], _keys = new uint[MaxEnch], _cats = new uint[MaxEnch];
    private readonly double[] _exp = new double[MaxEnch];
    private readonly float[] _vals = new float[MaxEnch];
    private readonly List<Line> _lines = new();
    private readonly Dictionary<uint, Line> _byId = new();
    private long _nextRead, _nextText;
    private bool _valid;
    private int _first, _soon;
    private float _grab;
    private string _soonest = "";

    public void Shown()
    {
        PortalSpellTable.EnsureLoadQueued();
        _nextRead = _nextText = 0;
    }

    public void Hidden() { }

    public float Draw(ClassicContext ctx, Vector2 o, float w)
    {
        Refresh();
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        RynthAiSnapshot raw = ctx.Raw;
        float y = o.Y;

        // ── Profile ──
        Header(dl, new Vector2(o.X, y), w, "Buff profile", _soonest.Length > 0 ? "first to end: " + _soonest : null);
        y += Px(23);
        {
            float rowH = Px(22), labelW = Px(52);
            TextMid(dl, o.X + Px(3), y, rowH, "Profile", Text);
            var ddPos = new Vector2(o.X + labelW + Px(6), y + (rowH - Px(19)) * 0.5f);
            float ddW = w - labelW - Px(6) - Px(52);
            string[] profiles = raw.BuffProfiles;
            if (Dropdown("##cb_profile", ctx.View?.BuffText ?? "Built-in", ddPos, ddW, ctx.Popup.IsOpen) && profiles.Length > 0)
                ctx.Popup.Open(ddPos + new Vector2(0, Px(19)), ddW, profiles, raw.SelectedBuffIdx, i => RynthAiCommands.SelectProfile(4, i));
            SafeImGui.SetItemTooltip(profiles.Length > 0 ? "The buff profile buffing uses" : "Buff profiles need RynthAi 0.5.31 or newer");
            if (Plate("##cb_spells", "Spells", new Vector2(o.X + w - Px(46), y + (rowH - Px(17)) * 0.5f)))
                RynthAiCommands.ApplyRemoteCommand("spells", "toggle");
            SafeImGui.SetItemTooltip("RynthAi Spells: browse the spell list and build buff profiles.");
            y += rowH;
        }
        {
            bool on = raw.BuffingEnabled;
            if (Gem("##cb_on", "Buffing", new Vector2(o.X, y), w / 2, on))
                RynthAiCommands.SetSubsystemEnabled(1, !on);
            SafeImGui.SetItemTooltip(on ? "Buffing on. Click to turn it off." : "Buffing off. Click to turn it on.");
            y += Px(20);
        }

        // ── Active ──
        y += Px(6);
        int harmful = 0;
        foreach (Line l in _lines) if (l.Harmful) harmful++;
        string right = !_valid ? "not in game" : _lines.Count == 0 ? "none"
            : $"{_lines.Count - harmful} buffs" + (harmful > 0 ? $" · {harmful} harmful" : "") + (_soon > 0 ? $" · {_soon} ending soon" : "");
        Header(dl, new Vector2(o.X, y), w, "On you now", right);
        y += Px(23);
        float rowH2 = Px(18), listW = w - Px(16), listH = rowH2 * VisibleRows;
        var listMin = new Vector2(o.X, y);
        dl.AddRectFilled(listMin, listMin + new Vector2(listW, listH), RynthTheme.Argb(0x8C000000));
        _first = Wheel(listMin, listMin + new Vector2(w, listH), _first, _lines.Count, VisibleRows);
        for (int r = 0; r < VisibleRows && _first + r < _lines.Count; r++)
        {
            Line l = _lines[_first + r];
            var p = new Vector2(o.X, y + r * rowH2);
            if (((_first + r) & 1) == 1) dl.AddRectFilled(p, p + new Vector2(listW, rowH2), AltRow);
            float icon = Px(16);
            var ip = new Vector2(p.X + Px(3), p.Y + (rowH2 - icon) * 0.5f);
            if (PortalSpellTable.TryGetIcon(l.SpellId, out uint iconId) && RetailSprites.TryGet(iconId, out IntPtr tex))
                dl.AddImage(tex, ip, ip + new Vector2(icon, icon));
            else
                dl.AddRect(ip, ip + new Vector2(icon, icon), l.Harmful ? Down : GoldDim);
            float tw = Px(60);
            TextClip(dl, p.X + Px(24), p.Y, rowH2, p.X + listW - tw, l.Name, l.Harmful ? Down : White, 10);
            TextRight(dl, p.X + listW - Px(4), p.Y, rowH2, l.Left, l.LeftColor, 10);
        }
        if (_lines.Count == 0)
            TextMid(dl, o.X + Px(6), y, rowH2, _valid ? "No spells on you." : "Log in to see your buffs.", Dim, 10);
        _first = Scrollbar("##cb_sb", new Vector2(o.X + listW, y), listH, _lines.Count, VisibleRows, _first, ref _grab);
        y += listH + Px(6);

        // ── Rebuff ──
        {
            float bw = Px(118);
            if (Button("##cb_rebuff", "Rebuff all", new Vector2(o.X, y), bw, ButtonColor.Blue))
                RynthAiCommands.ForceRebuff();
            SafeImGui.SetItemTooltip("Force rebuff: recast every buff in the profile now.");
            if (Button("##cb_cancel", "Cancel", new Vector2(o.X + bw + Px(6), y), Px(96), ButtonColor.Red))
                RynthAiCommands.CancelForceRebuff();
            SafeImGui.SetItemTooltip("Cancel a force rebuff");
            y += Px(32);
        }
        return y - o.Y;
    }

    // ── Data ────────────────────────────────────────────────────────────

    private void Refresh()
    {
        long now = Environment.TickCount64;
        if (now >= _nextRead)
        {
            _nextRead = now + 250;
            int n = EnchantmentHooks.CopyPlayerStatMods(_ids, _exp, _types, _keys, _vals, _cats);
            _valid = n >= 0;
            Rebuild(Math.Max(0, n));
            _nextText = 0;
        }
        if (now >= _nextText)
        {
            _nextText = now + 1000;
            Texts();
        }
    }

    /// <summary>The lines for this read: one per spell (the strongest copy's expiry), cooldowns and unnamed left out.</summary>
    private void Rebuild(int n)
    {
        _byId.Clear();
        _lines.Clear();
        for (int i = 0; i < n; i++)
        {
            uint id = _ids[i];
            if (id == 0 || id >= 0x8000) continue;   // 0x8000+: cooldowns, not spells
            if (!PortalSpellTable.TryGetName(id, out string name) || name.Length == 0) continue;
            if (_byId.TryGetValue(id, out Line? have))
            {
                if (_exp[i] > have.Expiry) have.Expiry = _exp[i];
                continue;
            }
            var line = new Line { SpellId = id, Name = name, Expiry = _exp[i], Harmful = PortalSpellTable.TryGetById(id, out _) };
            _byId[id] = line;
            _lines.Add(line);
        }
        // Harmful first, then the soonest to end; spells with no end last.
        _lines.Sort((a, b) =>
        {
            if (a.Harmful != b.Harmful) return a.Harmful ? -1 : 1;
            int c = a.Expiry.CompareTo(b.Expiry);
            return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name);
        });
    }

    private void Texts()
    {
        double serverNow = TimeSyncHooks.GetCurrentServerTime();
        _soon = 0;
        _soonest = "";
        foreach (Line l in _lines)
        {
            if (l.Expiry >= NoEnd) { l.Left = "no end"; l.LeftColor = Faint; continue; }
            if (serverNow <= 0) { l.Left = ""; l.LeftColor = Faint; continue; }
            double left = l.Expiry - serverNow;
            long s = (long)Math.Max(0, left);
            l.Left = left <= 0 ? "ending" : s >= 3600 ? $"{s / 3600}h {s % 3600 / 60:00}m" : s >= 60 ? $"{s / 60}m {s % 60:00}s" : $"{s}s";
            l.LeftColor = l.Harmful ? Down : left < 60 ? Down : left < 600 ? Amber : Green;
            if (!l.Harmful && left < 600) _soon++;
            if (!l.Harmful && _soonest.Length == 0) _soonest = l.Name + " in " + l.Left;
        }
    }
}
