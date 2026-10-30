// ============================================================================
//  RynthCore.Engine - ImGui/MonsterHud/PlateMetrics.cs
//  The plates' lengths that need no font, so the offline tests can check them:
//    DebuffRowMetrics - the debuff icon row (DebuffIconRow) at a plate's scale
//      and its "Debuff icon size" setting (2026-10-28: a player asked for
//      bigger icons, apart from the text size). The monster plates and your
//      own plate each have their own setting.
//    SelfPlateMetrics - your plate's bars, the name's room above them and the
//      debuff row under them (PlayerPlate.Measure), and the clamp that keeps
//      all of it on screen.
//  At 100 % every length is the expression the plates used before the setting
//  existed (k x 1 is k exactly), so nothing moves by a pixel until it is set.
//  Plain math, no engine dependencies (the offline tests compile it in).
// ============================================================================

using System;

namespace RynthCore.Engine.ImGuiBackend.Hud;

/// <summary>
/// The debuff icon row's lengths at a plate's scale <c>k</c> and an icon size setting (a percentage,
/// 100 = the size before the setting). Everything the row draws or reserves follows k x the setting:
/// the icon square, its glyph, the countdown line, the spacing; the stack digit and "+N" scale their
/// text size by <see cref="Scale"/> (<see cref="TextSize"/>). Text size settings never change it.
/// </summary>
internal readonly struct DebuffRowMetrics
{
    /// <summary>The setting as a factor (1 = 100 %).</summary>
    public readonly float Scale;
    /// <summary>The plate's k times <see cref="Scale"/>: what the row's own lengths follow.</summary>
    public readonly float K;
    /// <summary>The icon square (13 px at k 1, 100 %).</summary>
    public readonly float Icon;
    /// <summary>The glyph inside the square (px).</summary>
    public readonly float Glyph;
    /// <summary>Between the bar(s) above and the icons.</summary>
    public readonly float Above;
    /// <summary>Under each icon: room for the countdown line.</summary>
    public readonly float Below;
    /// <summary>Between two icons (and before "+N" and the "debuffed by others" marker).</summary>
    public readonly float Gap;
    /// <summary>The countdown line: its distance under the icon and its thickness.</summary>
    public readonly float LineGap, LineH;
    /// <summary>The icon square's corner radius.</summary>
    public readonly float Round;
    /// <summary>The stack digit's offset from the icon's top-right corner (right, up).</summary>
    public readonly float DigitDx, DigitDy;

    /// <param name="k">The plate's scale (Look > Scale x UI scale, x the monster plate's distance shrink).</param>
    /// <param name="sizePercent">The icon size setting (MonsterHudSettings: 50-200; 0 or less = 100).</param>
    public DebuffRowMetrics(float k, int sizePercent)
    {
        float s = Scale = sizePercent > 0 ? sizePercent / 100f : 1f;
        float ik = K = k * s;
        Icon = MathF.Round(13f * ik);
        Glyph = MathF.Round(Icon * 0.8f);
        Above = MathF.Round(3f * ik);
        Below = MathF.Round(3f * s);
        Gap = MathF.Max(2f, MathF.Round(2f * ik));
        LineGap = MathF.Max(1f, MathF.Round(s));
        LineH = MathF.Max(1f, MathF.Round(1.5f * ik));
        Round = 2f * ik;
        DigitDx = MathF.Round(s);
        DigitDy = MathF.Round(2f * s);
    }

    /// <summary>The room the row takes under the bar(s): the gap above, the icons, the countdown line.</summary>
    public float Height => Above + Icon + Below;

    /// <summary>The row's text size (stack digit, "+N") from the plate's small text size (whole px).</summary>
    public float TextSize(float smallSize) => MathF.Round(smallSize * Scale);

    /// <summary>
    /// The row's width: <paramref name="slots"/> squares (icons and the "debuffed by others" marker) and,
    /// when <paramref name="more"/>, the "+N" text <paramref name="moreTextW"/> px wide after them.
    /// </summary>
    public float Width(int slots, bool more, float moreTextW)
    {
        float moreW = more ? MoreWidth(moreTextW) : 0f;
        return slots * Icon + Math.Max(0, slots - 1) * Gap + (more ? Gap + moreW : 0f);
    }

    /// <summary>The "+N" text's slot: its width plus a little room.</summary>
    public float MoreWidth(float moreTextW) => moreTextW + 4f * K;

    /// <summary>
    /// A plate box <paramref name="width"/> wide, grown to hold an enlarged row (<paramref name="rowWidth"/>
    /// plus <paramref name="pad"/>). At 100 % or smaller the box is left as it was before the setting.
    /// </summary>
    public float CoverWidth(float width, float rowWidth, float pad) =>
        Scale > 1f ? MathF.Max(width, rowWidth + pad) : width;

    /// <summary>
    /// How far an enlarged row centred under bars <paramref name="barW"/> wide reaches past each of their
    /// ends (whole px). 0 at 100 % or smaller, as before the setting.
    /// </summary>
    public float Overhang(float rowWidth, float barW) =>
        Scale > 1f ? MathF.Max(0f, MathF.Ceiling((rowWidth - barW) * 0.5f)) : 0f;
}

/// <summary>
/// Your own plate's lengths (PlayerPlate.Measure): three bars, the name's room above them and the debuff
/// row under them. The plate is placed by the bars' top-left; it reaches <see cref="NameH"/> above that,
/// <see cref="BlockH"/> + <see cref="IconsH"/> below it and <see cref="IconsOver"/> past either end, and
/// <see cref="ClampLeft"/> / <see cref="ClampTop"/> keep all of it on screen (the fixed plate, and a
/// following one with Keep on screen); the drag area covers the same rect.
/// </summary>
internal struct SelfPlateMetrics
{
    public float K, BarW, BarH, Gap, Round, BlockH, NameH;
    /// <summary>The debuff row's room under the bars (0 without an icon) and its reach past the bars' ends.</summary>
    public float IconsH, IconsOver;
    public DebuffRowMetrics Row;

    /// <summary>Your plate's measured height: the name, the bars and the debuff row.</summary>
    public readonly float Height => NameH + BlockH + IconsH;

    /// <summary>The debuff row's top, down from the bars' top.</summary>
    public readonly float RowTop => BlockH + Row.Above;

    /// <param name="k">Look > Scale x UI scale.</param>
    /// <param name="numbers">"Numbers on your bars" (taller, wider bars).</param>
    /// <param name="barWidth">"Your bar width" (1 = default).</param>
    /// <param name="barHeight">"Your bar height" (1 = default).</param>
    /// <param name="nameSize">The name's text size, 0 when the name is off.</param>
    /// <param name="icons">There is a debuff icon to show.</param>
    /// <param name="iconPercent">"Your debuff icon size" (100 = default).</param>
    public static SelfPlateMetrics Measure(float k, bool numbers, float barWidth, float barHeight, float nameSize, bool icons, int iconPercent)
    {
        SelfPlateMetrics m = default;
        m.K = k;
        m.BarW = MathF.Round((numbers ? 124f : 96f) * k * barWidth);
        m.BarH = MathF.Round((numbers ? 15f : 5f) * k * barHeight);
        m.Gap = MathF.Max(2f, MathF.Round(2f * k));
        m.Round = MathF.Max(1f, 1.5f * k);
        m.BlockH = 3 * m.BarH + 2 * m.Gap;
        m.NameH = nameSize > 0f ? nameSize + 3f * k : 0f;
        m.Row = new DebuffRowMetrics(k, iconPercent);
        m.IconsH = icons ? m.Row.Height : 0f;
        m.IconsOver = 0f;   // PlayerPlate sets it from the row's width (needs the "+N" text): Row.Overhang
        return m;
    }

    /// <summary>The bars' left edge kept 4 px inside the screen, an enlarged debuff row's ends included.</summary>
    public readonly float ClampLeft(float left, float displayW) =>
        Math.Clamp(left, 4f + IconsOver, MathF.Max(4f + IconsOver, displayW - BarW - 4f - IconsOver));

    /// <summary>The bars' top kept 4 px inside the screen, with the name above and the debuff row under.</summary>
    public readonly float ClampTop(float y, float displayH) =>
        Math.Clamp(y, 4f + NameH, MathF.Max(4f + NameH, displayH - BlockH - IconsH - 4f));
}
