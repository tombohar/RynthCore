// ============================================================================
//  RynthCore.Engine - ImGui/MonsterHud/DebuffIconRow.cs
//  The debuff icon row, shared by the monster plates (your debuffs on each
//  monster, under its health bar) and your own plate (harmful spells on you,
//  under your mana bar): one icon per kind (a Phosphor glyph in the kind's
//  tint on a dark square), a stack digit when a kind merges several spell
//  categories, a countdown line under each icon that runs out with the spell,
//  a blink in the last BlinkSec seconds, "+N" when more kinds than fit, and
//  (monster plates only) the "debuffed by others" marker.
//
//  Moved out of MonsterHud.PaintIcons unchanged (2026-10-27) so the player
//  plate can draw the same row; the monster plates look exactly as before.
//  Sizes (2026-10-28): every length comes from DebuffRowMetrics (PlateMetrics.cs),
//  the plate's scale x its own "Debuff icon size" setting (monster plates and
//  your plate apart, neither tied to a text size); the stack digit and "+N"
//  scale the plate's small text by it. At 100 % the row is exactly as before.
//  Hover tooltips (2026-11-07): a row painted with tips on notes the icon
//  under the cursor (the last row painted wins: it is on top); after every
//  plate is painted, MonsterHud.Draw calls ShowHoverTip, which names the
//  kind, what it does, its spells and when the first ends (DebuffTip), the
//  "+N" count, or what the "debuffed by others" marker means. No tip while an
//  ImGui panel has the mouse, the Insert UI toggle is off, the plate is
//  fading, or (rows drawn under AC's windows) one of AC's windows covers it.
//  Render thread only. Engine-owned statics.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using ImGuiNET;
using RynthCore.Engine.Compatibility;

namespace RynthCore.Engine.ImGuiBackend.Hud;

/// <summary>Whose row it is, for the hover tooltip (Off: no tooltip).</summary>
internal enum DebuffRowTips : byte { Off, Monster, Self }

internal static class DebuffIconRow
{
    private enum Hit : byte { None, Icon, More, Others }

    private readonly record struct TipKey(Hit Hit, DebuffKind Kind, uint S0, uint S1, uint S2, uint S3, int Stack, int Secs, DebuffRowTips Row, int More);

    // The icon under the cursor this frame (BeginHover clears it; the topmost row painted wins).
    private static Hit _hit;
    private static DebuffIcon _hitIcon;
    private static int _hitMore;
    private static DebuffRowTips _hitRow;
    private static bool _hitUnderUi;
    // The last tooltip built, kept while the cursor stays on the same icon and second.
    private static TipKey _tipKey;
    private static string _tipText = "";
    private static readonly List<string> TipNames = new(DebuffIcon.MaxSpells);

    /// <summary>Start of a HUD frame: nothing hovered yet.</summary>
    internal static void BeginHover() => _hit = Hit.None;

    /// <summary>
    /// End of a HUD frame (inside the ImGui frame): the tooltip of the icon the cursor is on, if any.
    /// A row drawn under AC's windows gets none where one of AC's windows covers the cursor.
    /// </summary>
    internal static void ShowHoverTip(long now)
    {
        if (_hit == Hit.None) return;
        if (_hitUnderUi && MonsterHud.AcWindowCoversCursor()) return;
        int secs = _hit == Hit.Icon ? (int)Math.Ceiling((_hitIcon.Expiry - now) / (double)Stopwatch.Frequency) : 0;
        var key = new TipKey(_hit, _hitIcon.Kind, _hitIcon.Spell0, _hitIcon.Spell1, _hitIcon.Spell2, _hitIcon.Spell3,
            _hitIcon.Stack, secs, _hitRow, _hitMore);
        if (key != _tipKey || _tipText.Length == 0)
        {
            _tipKey = key;
            _tipText = _hit switch
            {
                Hit.Others => DebuffTip.Others,
                Hit.More => DebuffTip.More(_hitMore),
                _ => IconTip(in _hitIcon, secs, _hitRow),
            };
        }
        SafeImGui.SetTooltip(_tipText);
    }

    private static string IconTip(in DebuffIcon ic, int secs, DebuffRowTips row)
    {
        TipNames.Clear();
        for (int i = 0; i < ic.SpellCount; i++)
            if (PortalSpellTable.TryGetById(ic.SpellAt(i), out DebuffSpell s) && s.Name.Length > 0)
                TipNames.Add(s.Name);
        return DebuffTip.Text(ic.Kind, TipNames, ic.Stack, secs, estimated: row == DebuffRowTips.Monster, self: row == DebuffRowTips.Self);
    }

    private static bool Inside(Vector2 m, Vector2 p0, Vector2 p1) => m.X >= p0.X && m.X <= p1.X && m.Y >= p0.Y && m.Y <= p1.Y;

    /// <summary>Icons per row; more shows "+N" (the last slot makes room for it).</summary>
    internal const int MaxIcons = 5;
    /// <summary>Icons blink in their last seconds.</summary>
    internal const float BlinkSec = 5f;

    private static readonly uint IconBack = MonsterHud.C(0xD80B1219), OthersTint = MonsterHud.C(0xFFFF8A7A);

    // One glyph and tint per DebuffKind (index = kind).
    private static readonly string[] KindGlyph =
    {
        PhosphorIcons.Fire, PhosphorIcons.Snowflake, PhosphorIcons.Lightning, PhosphorIcons.Flask,
        PhosphorIcons.Sword, PhosphorIcons.Knife, PhosphorIcons.Hammer,
        PhosphorIcons.ShieldSlash, PhosphorIcons.MagicWand, PhosphorIcons.ShieldWarning,
        PhosphorIcons.ArrowFatLinesDown, PhosphorIcons.HeartBreak, PhosphorIcons.Footprints,
        PhosphorIcons.TrendDown, PhosphorIcons.Skull, PhosphorIcons.ArrowFatDown,
    };
    private static readonly uint[] KindTint =
    {
        MonsterHud.ElemFire, MonsterHud.ElemCold, MonsterHud.ElemLightning, MonsterHud.ElemAcid,
        MonsterHud.ElemSlash, MonsterHud.ElemPierce, MonsterHud.ElemBludgeon,
        MonsterHud.C(0xFF9FB4C8),   // Imperil: steel
        MonsterHud.C(0xFFB98CFF),   // Magic Yield: violet
        MonsterHud.C(0xFFFFB347),   // melee / missile defense: orange
        MonsterHud.C(0xFFF2D24B),   // attributes: yellow
        MonsterHud.C(0xFFFF6B8A),   // vitals / regen: pink-red
        MonsterHud.C(0xFFA8C66C),   // movement: moss
        MonsterHud.C(0xFF8FB8DE),   // skills: blue-grey
        MonsterHud.C(0xFF7CE08A),   // damage over time: green
        MonsterHud.C(0xFFC8C8C8),   // other
    };
    private static readonly string[] StackDigits = { "", "", "2", "3", "4", "5", "6", "7", "8", "9" };

    /// <summary>
    /// The row centred on <paramref name="cx"/> with its top at <paramref name="top"/>:
    /// <paramref name="count"/> kinds (may exceed <paramref name="icons"/>' length: the rest
    /// show as "+N"), then the "debuffed by others" marker when <paramref name="others"/>.
    /// <paramref name="row"/> holds every length (the plate's scale x its icon size setting);
    /// <paramref name="smallFont"/>/<paramref name="smallSize"/> are the plate's small text, which the
    /// stack digit and "+N" scale by the icon size; <paramref name="overflowKey"/>/<paramref name="overflowText"/>
    /// cache the "+N" string per row. <paramref name="now"/> is Stopwatch ticks. <paramref name="tips"/>:
    /// whose row, for the hover tooltip (Off: none); <paramref name="underUi"/>: drawn under AC's windows.
    /// </summary>
    internal static void Paint(ImDrawListPtr dl, DebuffIcon[] icons, int count, bool others, in DebuffRowMetrics row,
        ImFontPtr smallFont, float smallSize, ref int overflowKey, ref string overflowText,
        float cx, float top, float a, long now, DebuffRowTips tips = DebuffRowTips.Off, bool underUi = false)
    {
        // Hover: only a settled plate, with the UI toggle on and no ImGui panel holding the mouse.
        Vector2 mouse = default;
        bool hover = false;
        if (tips != DebuffRowTips.Off && a >= 0.25f && Win32Backend.IsUiCaptureEnabled())
        {
            ImGuiIOPtr io = ImGuiNET.ImGui.GetIO();
            hover = !io.WantCaptureMouse;
            mouse = io.MousePos;
        }

        float sz = row.Icon, gap = row.Gap;
        int shown = Prepare(icons.Length, count, in row, ref smallFont, ref smallSize, ref overflowKey, ref overflowText,
            out int overflow, out string more, out float moreTextW);
        float moreW = overflow > 0 ? row.MoreWidth(moreTextW) : 0f;
        float rowW = row.Width(shown + (others ? 1 : 0), overflow > 0, moreTextW);
        float x = MathF.Round(cx - rowW * 0.5f);
        float glyphPx = row.Glyph;
        ImFontPtr iconFont = ImGuiFonts.Sharp(glyphPx, bold: false);   // the icons are merged into every bake
        float lineH = row.LineH;

        for (int i = 0; i < shown; i++)
        {
            DebuffIcon ic = icons[i];
            int kind = (int)ic.Kind;
            uint tint = kind < KindTint.Length ? KindTint[kind] : KindTint[^1];
            float remaining = (ic.Expiry - now) / (float)Stopwatch.Frequency;
            float frac = ic.Duration > 0 ? Math.Clamp(remaining / ic.Duration, 0f, 1f) : 0f;
            float blink = remaining < BlinkSec ? 0.45f + 0.55f * MathF.Abs(MathF.Cos(remaining * MathF.PI * 2f)) : 1f;

            var i0 = new Vector2(x, top);
            var i1 = i0 + new Vector2(sz, sz);
            dl.AddRectFilled(i0, i1, MonsterHud.Mul(IconBack, a), row.Round);
            dl.AddRect(i0, i1, MonsterHud.Mul(tint, a * 0.55f * blink), row.Round, ImDrawFlags.None, 1f);
            PhosphorIcons.DrawCentered(dl, iconFont, glyphPx, KindGlyph[kind < KindGlyph.Length ? kind : KindGlyph.Length - 1],
                i0, new Vector2(sz, sz), MonsterHud.Mul(tint, a * blink));
            if (ic.Stack > 1 && ic.Stack < StackDigits.Length)
            {
                string digit = StackDigits[ic.Stack];
                float ds = MathF.Round(smallSize * 0.85f);
                var dp = new Vector2(i1.X - MonsterHud.TextWidth(smallFont, ds, digit) + row.DigitDx, i0.Y - row.DigitDy);
                MonsterHud.OutlinedText(dl, smallFont, ds, dp, a, digit);
                dl.AddText(smallFont, ds, dp, MonsterHud.Mul(MonsterHud.HpText, a), digit);
            }
            // Countdown: a line under the icon that shrinks to nothing at expiry.
            float ly = i1.Y + row.LineGap;
            dl.AddRectFilled(new Vector2(i0.X, ly), new Vector2(i1.X, ly + lineH), MonsterHud.Mul(MonsterHud.Outline, a * 0.8f));
            if (frac > 0f)
                dl.AddRectFilled(new Vector2(i0.X, ly), new Vector2(i0.X + MathF.Max(1f, sz * frac), ly + lineH), MonsterHud.Mul(tint, a * blink));
            // The icon and its countdown line, with the gap to the next icon split between them (no dead spots).
            if (hover && Inside(mouse, i0 - new Vector2(gap * 0.5f, 0f), new Vector2(i1.X + gap * 0.5f, ly + lineH)))
                Note(Hit.Icon, in ic, 0, tips, underUi);
            x += sz + gap;
        }

        if (overflow > 0)
        {
            var mp = new Vector2(x, MathF.Round(top + (sz - smallSize) * 0.5f));
            MonsterHud.OutlinedText(dl, smallFont, smallSize, mp, a, more);
            dl.AddText(smallFont, smallSize, mp, MonsterHud.Mul(MonsterHud.DimText, a), more);
            if (hover && Inside(mouse, new Vector2(x, top), new Vector2(x + moreW, top + sz)))
                Note(Hit.More, default, overflow, tips, underUi);
            x += moreW + gap;
        }

        if (others)
        {
            var i0 = new Vector2(x, top);
            dl.AddRectFilled(i0, i0 + new Vector2(sz, sz), MonsterHud.Mul(IconBack, a * 0.8f), row.Round);
            PhosphorIcons.DrawCentered(dl, iconFont, glyphPx, PhosphorIcons.UsersThree, i0, new Vector2(sz, sz), MonsterHud.Mul(OthersTint, a * 0.8f));
            if (hover && Inside(mouse, i0, i0 + new Vector2(sz, sz)))
                Note(Hit.Others, default, 0, tips, underUi);
        }
    }

    private static void Note(Hit hit, in DebuffIcon icon, int more, DebuffRowTips row, bool underUi)
    {
        _hit = hit;
        _hitIcon = icon;
        _hitMore = more;
        _hitRow = row;
        _hitUnderUi = underUi;
    }

    /// <summary>
    /// The row's width as <see cref="Paint"/> lays it out (same arguments): the plates' layout uses it so
    /// their box (crowding, clicks, the screen clamp) holds an enlarged row.
    /// </summary>
    internal static float Width(int capacity, int count, bool others, in DebuffRowMetrics row,
        ImFontPtr smallFont, float smallSize, ref int overflowKey, ref string overflowText)
    {
        int shown = Prepare(capacity, count, in row, ref smallFont, ref smallSize, ref overflowKey, ref overflowText,
            out int overflow, out _, out float moreTextW);
        return row.Width(shown + (others ? 1 : 0), overflow > 0, moreTextW);
    }

    /// <summary>
    /// How many icons show (the last slot makes room for "+N" when more kinds than fit), the "+N" string and
    /// its width, and the row's text (stack digit, "+N"): the plate's small text scaled by the icon size, from
    /// the smallest bake at least that big (at 100 % the plate's own font and size, untouched).
    /// </summary>
    private static int Prepare(int capacity, int count, in DebuffRowMetrics row, ref ImFontPtr font, ref float size,
        ref int overflowKey, ref string overflowText, out int overflow, out string more, out float moreTextW)
    {
        if (row.Scale != 1f)
        {
            size = row.TextSize(size);
            font = ImGuiFonts.Sharp(size, bold: false);
        }
        int shown = count > capacity ? capacity - 1 : count;
        overflow = count - shown;
        more = "";
        moreTextW = 0f;
        if (overflow > 0)
        {
            if (overflow != overflowKey)
            {
                overflowKey = overflow;
                overflowText = "+" + overflow.ToString(CultureInfo.InvariantCulture);
            }
            more = overflowText;
            moreTextW = MonsterHud.TextWidth(font, size, more);
        }
        return shown;
    }
}
