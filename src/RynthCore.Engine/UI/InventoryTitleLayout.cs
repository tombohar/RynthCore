// ============================================================================
//  RynthCore.Engine - UI/InventoryTitleLayout.cs
//  The Classic Inventory title bar's left half: where the title, the Vault button
//  and the related-panel buttons (Mini Remote, RynthSort, Item Count, Global
//  Inventory) go, so nothing lands off the panel when the bar is narrow.
//
//  The bar is 300 retail units wide (x scale), so at the retail size the room left of the
//  four fixed toggles (- + tools Modern) is about 186 units. Vault is always placed (it is
//  the only one with no other way in); the four related buttons collapse by priority:
//    Text   four text buttons (Remote Sort Count GInv), when the title keeps its minimum
//    Icons  four icon squares
//    Menu   one "more" square that opens a menu of the four
//    None   nothing fits (a bar far narrower than any real panel); the Character menu has them all
//  The title gets what is left (TitleLeft..TitleRight) and is elided with an ellipsis.
//
//  Pure (no ImGui, no files, no engine state). All X values are relative to the bar's
//  left edge, in pixels; callers add the bar origin. Tests: tools/BarDeclutterTests.
// ============================================================================

using System;

namespace RynthCore.Engine.UI;

internal enum TitleExtrasMode { Text, Icons, Menu, None }

/// <summary>A button's span on the bar: left edge and width (width 0 = not drawn).</summary>
internal readonly record struct TitleSlot(float X, float W)
{
    public float Right => X + W;
    public bool Present => W > 0f;
}

/// <summary>The positions InventoryFace's Classic title bar draws, in bar-relative pixels.</summary>
internal readonly record struct InventoryTitleLayout(
    TitleExtrasMode Mode,
    float Btn,
    TitleSlot Smaller, TitleSlot Bigger, TitleSlot Tools, TitleSlot Modern,
    TitleSlot Vault,
    TitleSlot Remote, TitleSlot Sort, TitleSlot Count, TitleSlot GInv,
    TitleSlot More,
    float TitleLeft, float TitleRight)
{
    // Units at scale 1 (the retail art's), multiplied by the bar's scale.
    public const float BtnUnits = 18, EdgeUnits = 8, PadUnits = 6, GapSmall = 2, GapGroup = 6, VaultGap = 8,
        VaultUnits = 38, RemoteUnits = 48, SortUnits = 36, CountUnits = 40, GInvUnits = 42,
        TextGap = 4, IconGap = 2, LeadGap = 4, TitleMinUnits = 56, TitleGap = 4;

    /// <summary>The title's room (TitleRight - TitleLeft), never negative.</summary>
    public float TitleWidth => Math.Max(0f, TitleRight - TitleLeft);

    /// <summary>The left edge of everything drawn left of the title (Vault and the related buttons): the drag surface ends here.</summary>
    public float LeftmostX
    {
        get
        {
            float x = Math.Min(Modern.X, Smaller.X);
            if (Vault.Present) x = Math.Min(x, Vault.X);
            if (Mode is TitleExtrasMode.Text or TitleExtrasMode.Icons) x = Math.Min(x, Remote.X);
            if (Mode == TitleExtrasMode.Menu) x = Math.Min(x, More.X);
            return x;
        }
    }

    /// <summary>
    /// Lays the bar out. <paramref name="barW"/> is the bar's width in pixels (the panel width less
    /// the close button), <paramref name="s"/> its scale, <paramref name="vault"/> whether the Vault
    /// button exists (VaultFeed.Available), <paramref name="titleWantW"/> the title text's full
    /// width in pixels (it only decides how much room the title is owed: at most 56 units).
    /// </summary>
    public static InventoryTitleLayout Compute(float barW, float s, bool vault, float titleWantW)
    {
        if (!(s > 0f) || !float.IsFinite(s)) s = 1f;
        if (!float.IsFinite(barW)) barW = 0f;
        if (!float.IsFinite(titleWantW) || titleWantW < 0f) titleWantW = 0f;

        float btn = MathF.Round(BtnUnits * s);
        float pad = PadUnits * s;

        // The four fixed toggles at the bar's right end (unchanged from the original Classic layout).
        float modernX = barW - EdgeUnits * s - btn;
        float toolsX = modernX - btn - GapSmall * s;
        float biggerX = toolsX - btn - GapGroup * s;
        float smallerX = biggerX - btn - GapSmall * s;
        var modern = new TitleSlot(modernX, btn);
        var tools = new TitleSlot(toolsX, btn);
        var bigger = new TitleSlot(biggerX, btn);
        var smaller = new TitleSlot(smallerX, btn);

        // Vault sits next to the toggles, and is never dropped. On a bar too narrow to hold it
        // without crossing the title area it is pushed to the left edge, never past it.
        float edge = smallerX;
        TitleSlot vaultSlot = default;
        if (vault)
        {
            float vw = MathF.Round(VaultUnits * s);
            float vx = Math.Max(0f, edge - vw - VaultGap * s);
            vaultSlot = new TitleSlot(vx, vw);
            edge = vx;
        }

        // The room left of `edge` is shared by the title and the related buttons. The title is owed
        // TitleMinUnits (less if its whole text is shorter); take the richest button style that leaves it.
        float titleMin = Math.Min(titleWantW, TitleMinUnits * s);
        float lead = LeadGap * s;

        float rw = MathF.Round(RemoteUnits * s), sw = MathF.Round(SortUnits * s),
              cw = MathF.Round(CountUnits * s), gw = MathF.Round(GInvUnits * s);
        float tg = TextGap * s, ig = IconGap * s;

        // Right to left: GInv, Count, Sort, Remote.
        float tGinvX = edge - lead - gw;
        float tCountX = tGinvX - tg - cw;
        float tSortX = tCountX - tg - sw;
        float tRemoteX = tSortX - tg - rw;

        float iGinvX = edge - lead - btn;
        float iCountX = iGinvX - ig - btn;
        float iSortX = iCountX - ig - btn;
        float iRemoteX = iSortX - ig - btn;

        float moreX = edge - lead - btn;

        TitleExtrasMode mode;
        float groupLeft;
        if (tRemoteX - pad >= titleMin) { mode = TitleExtrasMode.Text; groupLeft = tRemoteX; }
        else if (iRemoteX - pad >= titleMin) { mode = TitleExtrasMode.Icons; groupLeft = iRemoteX; }
        else if (moreX - pad >= 0f) { mode = TitleExtrasMode.Menu; groupLeft = moreX; }
        else { mode = TitleExtrasMode.None; groupLeft = edge; }

        TitleSlot remote = default, sort = default, count = default, ginv = default, more = default;
        switch (mode)
        {
            case TitleExtrasMode.Text:
                remote = new TitleSlot(tRemoteX, rw);
                sort = new TitleSlot(tSortX, sw);
                count = new TitleSlot(tCountX, cw);
                ginv = new TitleSlot(tGinvX, gw);
                break;
            case TitleExtrasMode.Icons:
                remote = new TitleSlot(iRemoteX, btn);
                sort = new TitleSlot(iSortX, btn);
                count = new TitleSlot(iCountX, btn);
                ginv = new TitleSlot(iGinvX, btn);
                break;
            case TitleExtrasMode.Menu:
                more = new TitleSlot(moreX, btn);
                break;
        }

        float titleRight = Math.Max(pad, groupLeft - TitleGap * s);
        return new InventoryTitleLayout(mode, btn, smaller, bigger, tools, modern, vaultSlot,
            remote, sort, count, ginv, more, pad, titleRight);
    }

    /// <summary>Header (Modern look) buttons: four 22 px icons at a 24 px pitch, or one "more" button.</summary>
    public const float HeaderIconsW = 96, HeaderMenuW = 24, HeaderCountGap = 10;

    /// <summary>
    /// The Modern header's related-panel buttons for <paramref name="freeW"/> pixels between the pyreals
    /// and the Classic / Close buttons, shared with the item count text (<paramref name="countW"/>).
    /// Icons when all four and the count fit, else the one menu button, with the count text dropped last.
    /// Text mode never applies here (Icons, Menu or None).
    /// </summary>
    public static TitleExtrasMode ChooseHeaderExtras(float freeW, float countW, out bool showCount)
    {
        if (!float.IsFinite(freeW)) freeW = 0f;
        if (!float.IsFinite(countW) || countW < 0f) countW = 0f;
        float count = countW + HeaderCountGap;
        if (freeW >= HeaderIconsW + count) { showCount = true; return TitleExtrasMode.Icons; }
        if (freeW >= HeaderMenuW + count) { showCount = true; return TitleExtrasMode.Menu; }
        if (freeW >= HeaderMenuW) { showCount = false; return TitleExtrasMode.Menu; }
        showCount = freeW >= count;
        return TitleExtrasMode.None;
    }

    /// <summary>The Modern header's Vault button (Aelrynth): one 22 px icon at a 24 px pitch, left of the Classic button.</summary>
    public const float HeaderVaultW = 24;

    /// <summary>
    /// <see cref="ChooseHeaderExtras"/> with the Vault button in the picture: when <paramref name="vault"/>
    /// the Vault takes its 24 px first and is never dropped (it is the only way into the vault in the Modern
    /// look); the four related buttons, the "more" menu and the count text share what is left, as before.
    /// Without a vault the result is exactly ChooseHeaderExtras'.
    /// </summary>
    internal static HeaderPlan ChooseHeader(float freeW, float countW, bool vault)
    {
        if (!float.IsFinite(freeW)) freeW = 0f;
        TitleExtrasMode extras = ChooseHeaderExtras(vault ? freeW - HeaderVaultW : freeW, countW, out bool showCount);
        return new HeaderPlan(vault, extras, showCount);
    }

    /// <summary>
    /// <paramref name="text"/> cut to fit <paramref name="maxW"/> pixels, ending in an ellipsis when
    /// cut ("" when not even the ellipsis fits). <paramref name="measure"/> gives a string's width.
    /// </summary>
    public static string Elide(string text, float maxW, Func<string, float> measure)
    {
        if (text.Length == 0 || !(maxW > 0f)) return "";
        if (measure(text) <= maxW) return text;
        const string Ellipsis = "...";
        if (measure(Ellipsis) > maxW) return "";
        int lo = 0, hi = text.Length - 1;   // longest prefix whose prefix + ellipsis still fits
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (measure(text.Substring(0, mid).TrimEnd() + Ellipsis) <= maxW) lo = mid; else hi = mid - 1;
        }
        return text.Substring(0, lo).TrimEnd() + Ellipsis;
    }
}

/// <summary>What the Modern header draws between the pyreals and the Classic / Close buttons.</summary>
internal readonly record struct HeaderPlan(bool Vault, TitleExtrasMode Extras, bool ShowCount);

/// <summary>The Modern header's button positions, header-relative pixels (add the header's left edge).</summary>
internal readonly record struct InventoryHeaderLayout(
    HeaderPlan Plan,
    TitleSlot Close, TitleSlot Classic, TitleSlot Vault,
    TitleSlot Remote, TitleSlot Sort, TitleSlot Count, TitleSlot GInv, TitleSlot More,
    float RightEdge, float CountX)
{
    public const float Btn = 22, Pitch = 24;

    /// <summary>
    /// Right to left: Close, Classic, the Vault (when there is one), then four related icons or the
    /// "more" button, then the count text. <paramref name="w"/> is the header's width, <paramref name="leftEnd"/>
    /// where the pyreals text ends (header-relative), <paramref name="countW"/> the count text's width.
    /// </summary>
    public static InventoryHeaderLayout Compute(float w, float leftEnd, float countW, bool vault)
    {
        if (!float.IsFinite(w)) w = 0f;
        if (!float.IsFinite(leftEnd)) leftEnd = 0f;
        if (!float.IsFinite(countW) || countW < 0f) countW = 0f;

        var close = new TitleSlot(w - 22, Btn);
        float classicX = w - 46;
        var classic = new TitleSlot(classicX, Btn);
        HeaderPlan plan = InventoryTitleLayout.ChooseHeader(classicX - 4 - leftEnd, countW, vault);

        float anchor = classicX;   // the next button goes at anchor - 24
        TitleSlot vaultSlot = default;
        if (plan.Vault)
        {
            vaultSlot = new TitleSlot(anchor - Pitch, Btn);
            anchor = vaultSlot.X;
        }

        TitleSlot remote = default, sort = default, count = default, ginv = default, more = default;
        float rightEdge = anchor - 2;
        if (plan.Extras == TitleExtrasMode.Icons)
        {
            ginv = new TitleSlot(anchor - Pitch, Btn);
            count = new TitleSlot(ginv.X - Pitch, Btn);
            sort = new TitleSlot(count.X - Pitch, Btn);
            remote = new TitleSlot(sort.X - Pitch, Btn);
            rightEdge = remote.X - 2;
        }
        else if (plan.Extras == TitleExtrasMode.Menu)
        {
            more = new TitleSlot(anchor - Pitch, Btn);
            rightEdge = more.X - 2;
        }
        return new InventoryHeaderLayout(plan, close, classic, vaultSlot, remote, sort, count, ginv, more,
            rightEdge, rightEdge - countW - 4);
    }
}
