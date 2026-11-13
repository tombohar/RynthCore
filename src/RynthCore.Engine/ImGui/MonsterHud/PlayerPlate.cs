// ============================================================================
//  RynthCore.Engine - ImGui/MonsterHud/PlayerPlate.cs
//  The player's own plate: three thin stacked bars over your head (health red,
//  stamina amber, mana blue) in the monster plates' style (edge, shine, the
//  chip that drains after a drop); optional "cur/max" in each bar and the
//  name above. No name and no numbers by default, to stay compact.
//
//  Data: PlayerVitalsHooks.TryGetSnapshot, the buffed values AC's own vital
//  bars show, kept on AC's main thread (a lock and a struct copy per frame).
//
//  Two placements (MonsterHudSettings.SelfPlacement):
//    Fixed on screen (default since 2026-10-05: tied to the head the plate
//      jittered while the character moved). The bars sit at a saved screen
//      spot (fractions of the screen, so a resize keeps it in place, then
//      clamped inside the screen). Drawn by DrawScreen into ImGui's
//      background list, i.e. with the normal UI (over AC's windows, under
//      every ImGui panel), not through the under-UI 3D layer: a screen HUD
//      element doesn't belong to the world. "Lock position" (on by default)
//      makes it untouchable; unlocked, a frame shows and a left-drag anywhere
//      on the plate moves it (WantsMouse keeps that press from AC), saved on
//      release. Reset puts it back at the default spot.
//    Follow character: the player's live pose, projected like the monster
//      plates (MonsterHud.Project handles the landblock frame), above the
//      head, under the feet or beside the character; drawn by Draw into the
//      monster plates' list (UnderUiLayer).
//
//  First person (follow mode): when the camera is inside or right at the
//  head, the plate would sit in the middle of the screen, so it hides
//  (setting; on by default). The head point is published either way, for the
//  gain text.
//
//  Look (2026-10-27, each its own setting, all on by default = the old look):
//  the dark track + black edge behind each bar, the damage chip, the text
//  outline and the dark band behind the numbers. Your plate only.
//
//  "Debuffs on you" (2026-10-27, setting, on by default): the monster plates'
//  icon row (DebuffIconRow) under your mana bar, for harmful spells on you,
//  only while there is one. Data: the engine's player enchantment snapshot
//  (EnchantmentHooks.CopyPlayerStatMods: the main thread refreshes it every
//  250 ms; here a lock and an array copy, four times a second, as the Buffs
//  tab reads it), picked by SelfDebuffs.Select with the spell table
//  (PortalSpellTable, queued by MonsterHud.Update while the row is on). The
//  row hangs under the bars: placement keeps the bars where they are and
//  only the clamp and the drag area grow to cover it.
//  "Your debuff icon size" (2026-10-28, 50-200 %, 100 = as before): the row's
//  own scale, apart from your text size. The lengths that need no font are in
//  SelfPlateMetrics (PlateMetrics.cs, offline-tested): the clamp and the drag
//  area cover the row at any size, and an enlarged row wider than the bars
//  (IconsOver) too.
//
//  "Grey out what debuffs take" (2026-11-02, setting, on by default): while a
//  harmful spell lowers a maximum, the bar spans the maximum without it
//  (VitalLoss.FullMax, worked back from the shown one with the same snapshot's
//  StatMods) and the part it takes is grey at the bar's end, instead of the bar
//  filling up to the lowered maximum. The numbers stay AC's.
//
//  Render thread only. Engine-owned statics.
// ============================================================================

using System;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using ImGuiNET;
using RynthCore.Engine.Compatibility;
using RynthCore.Engine.D3D9;

namespace RynthCore.Engine.ImGuiBackend.Hud;

internal static class PlayerPlate
{
    private const float HeadHeight = 2.05f;          // metres above the feet (human-sized characters)
    private const float FirstPersonDistance = 1.3f;  // camera this close to the head = first person

    private static readonly uint HealthCol = MonsterHud.C(0xFFE04A4A), ManaCol = MonsterHud.C(0xFF3F8FE8),
        NameCol = MonsterHud.C(0xFFF2E8DC), NumCol = MonsterHud.C(0xFFFFFFFF);
    private static uint StaminaCol => RynthTheme.Highlight;

    private struct BarState
    {
        public float Shown, Trail;
        // Where the debuffed maximum ends on a bar that spans the full one (1 = nothing lost); eased.
        public float Cap;
        public long Hold;
        public bool Init;
        public uint Cur, Max;
        public string? Text;
        public long TextKey;
    }

    private static BarState _hp, _st, _mn;
    private static float _alpha;

    // "Debuffs on you": the snapshot copy (preallocated, read 4x a second) and this read's icons.
    private const int MaxEnch = SelfDebuffs.MaxEntries;
    private const int DebuffReadMs = 250;
    private static readonly uint[] _eIds = new uint[MaxEnch], _eTypes = new uint[MaxEnch], _eKeys = new uint[MaxEnch], _eCats = new uint[MaxEnch];
    private static readonly double[] _eExp = new double[MaxEnch];
    private static readonly float[] _eVals = new float[MaxEnch];
    private static readonly DebuffIcon[] _icons = new DebuffIcon[DebuffIconRow.MaxIcons];
    private static int _iconCount;
    private static long _nextDebuffRead;          // Environment.TickCount64
    private static int _overflowKey = -1;
    private static string _overflowText = "";
    private static readonly HarmfulSpellLookup Harmful = LookupHarmful;
    // "Grey out what debuffs take": the buff/debuff totals per vital (health, stamina, mana) from the same read.
    private static readonly VitalMods[] _vitalMods = { VitalMods.None, VitalMods.None, VitalMods.None };

    // Fixed mode: dragging (unlocked only).
    private static bool _hover, _dragging, _wantsMouse;
    private static Vector2 _grab;        // cursor minus the bars' centre when the drag began

    /// <summary>
    /// Fixed mode, unlocked: the cursor is on the plate (or a drag is held), so the click is the plate's.
    /// MonsterHud.WantsMouse includes it. Render thread.
    /// </summary>
    public static bool WantsMouse => _wantsMouse;

    /// <summary>Where the head is on screen this frame (valid when <see cref="HeadOnScreen"/>).</summary>
    public static Vector2 HeadScreen { get; private set; }
    public static bool HeadOnScreen { get; private set; }
    /// <summary>Top of the drawn plate (or the head point when it isn't drawn): where the gain text starts.</summary>
    public static float PlateTop { get; private set; }
    /// <summary>The camera is at the head (first person) this frame.</summary>
    public static bool FirstPerson { get; private set; }

    public static void Reset()
    {
        _hp = default; _st = default; _mn = default;
        _alpha = 0f;
        _iconCount = 0;
        _nextDebuffRead = 0;
        for (int i = 0; i < _vitalMods.Length; i++) _vitalMods[i] = VitalMods.None;
        HeadOnScreen = false;
        FirstPerson = false;
        DropDrag();
    }

    /// <summary>No fixed plate this frame: no hover, no drag, no capture.</summary>
    public static void DropDrag()
    {
        _hover = _dragging = _wantsMouse = false;
    }

    /// <summary>Clears last frame's capture before a frame (Update); DrawScreen sets it again.</summary>
    public static void ClearMouse() => _wantsMouse = false;

    public static void Draw(ref MonsterHud.Frame f)
    {
        // Head point (AC landblock-local), used by the gain text even when the plate is off.
        float hz = f.Pz + HeadHeight + MonsterHudSettings.HeightLift;
        FirstPerson = IsFirstPerson(ref f);
        HeadOnScreen = MonsterHud.Project(ref f, f.PlayerCell, f.Px, f.Py, hz, out Vector2 head, keepOnScreen: true) &&
                       head.X > -40 && head.X < f.Display.X + 40 && head.Y > -40 && head.Y < f.Display.Y + 40;
        HeadScreen = head;
        PlateTop = head.Y;
        // Fixed on screen: DrawScreen paints it with the normal UI (gain text stays over the head).
        if (MonsterHudSettings.SelfPlacement == SelfPlacement.Fixed) return;

        bool show = MonsterHudSettings.SelfPlate && HeadOnScreen && !(FirstPerson && MonsterHudSettings.SelfHideFirstPerson) &&
                    PlayerVitalsHooks.TryGetSnapshot(out _);
        if (!Advance(ref f, show)) return;
        Layout l = Measure(f.UiScale);
        float k = l.M.K, barW = l.M.BarW, blockH = l.M.BlockH, nameH = l.M.NameH, iconsH = l.M.IconsH;

        // Where the block goes: above the head (default), under the feet, or beside the character.
        float left, y;
        SelfPlatePosition where = MonsterHudSettings.SelfPosition;
        if (where == SelfPlatePosition.Above)
        {
            // The debuff row (when there is one) sits between the bars and the head.
            left = head.X - barW * 0.5f;
            y = head.Y - 4f * k - blockH - iconsH;
        }
        else
        {
            bool feetOk = MonsterHud.Project(ref f, f.PlayerCell, f.Px, f.Py, f.Pz, out Vector2 feet, keepOnScreen: true);
            if (!feetOk) feet = new Vector2(head.X, head.Y + 90f * k);
            float charH = MathF.Max(20f * k, feet.Y - head.Y);
            float midY = head.Y + charH * 0.5f;
            float side = MathF.Max(22f * k, charH * 0.28f);
            switch (where)
            {
                case SelfPlatePosition.Below:
                    left = feet.X - barW * 0.5f;
                    y = feet.Y + 6f * k + nameH;
                    break;
                case SelfPlatePosition.Left:
                    left = head.X - side - barW;
                    y = midY - blockH * 0.5f;
                    break;
                default: // Right
                    left = head.X + side;
                    y = midY - blockH * 0.5f;
                    break;
            }
        }
        if (MonsterHudSettings.KeepOnScreen)
        {
            left = l.M.ClampLeft(left, f.Display.X);
            y = l.M.ClampTop(y, f.Display.Y);
        }
        float top = Paint(f.Dl, ref l, MathF.Round(left), MathF.Round(y), f.Now, f.UnderUi);
        // Gain text floats above the plate when it's over the head, else above the head itself.
        PlateTop = where == SelfPlatePosition.Above ? top : head.Y;
    }

    /// <summary>
    /// Fixed on screen: the plate at its saved spot, into ImGui's background list (the normal UI pass:
    /// over AC's windows, under every panel), and the drag while unlocked. After <see cref="Draw"/>,
    /// inside the ImGui frame. Render thread.
    /// </summary>
    public static void DrawScreen(ref MonsterHud.Frame f)
    {
        if (MonsterHudSettings.SelfPlacement != SelfPlacement.Fixed) { DropDrag(); return; }
        bool show = MonsterHudSettings.SelfPlate && PlayerVitalsHooks.TryGetSnapshot(out _);
        if (!Advance(ref f, show)) { DropDrag(); return; }
        Layout l = Measure(f.UiScale);

        // The bars' centre from the saved fractions, then the block kept inside the screen.
        Vector2 display = f.Display;
        float cx = MonsterHudSettings.SelfFixedX * display.X, cy = MonsterHudSettings.SelfFixedY * display.Y;
        ref SelfPlateMetrics m = ref l.M;
        ImGuiIOPtr io = ImGuiNET.ImGui.GetIO();
        bool unlocked = !MonsterHudSettings.SelfLocked;
        if (_dragging)
        {
            if (unlocked && ImGuiNET.ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                cx = io.MousePos.X - _grab.X;
                cy = io.MousePos.Y - _grab.Y;
            }
            else
            {
                // Let go (or locked mid-drag): keep where it is, saved.
                _dragging = false;
                MonsterHudSettings.Save();
            }
        }
        // The saved spot is the bars' centre (a debuff row coming and going never moves them); the
        // clamp keeps the row under them (and past their ends, when it is wider) on screen too.
        float left = m.ClampLeft(cx - m.BarW * 0.5f, display.X);
        float y = m.ClampTop(cy - m.BlockH * 0.5f, display.Y);
        left = MathF.Round(left);
        y = MathF.Round(y);
        if (_dragging && display.X > 1 && display.Y > 1)
        {
            // The spot as fractions (clamped, so letting go off screen keeps it reachable).
            MonsterHudSettings.SelfFixedX = Math.Clamp((left + m.BarW * 0.5f) / display.X, 0f, 1f);
            MonsterHudSettings.SelfFixedY = Math.Clamp((y + m.BlockH * 0.5f) / display.Y, 0f, 1f);
        }

        // The plate's rect (name and debuff row included), a little padded: the drag surface.
        float pad = MathF.Round(4f * m.K);
        var r0 = new Vector2(left - pad - m.IconsOver, y - m.NameH - pad);
        var r1 = new Vector2(left + m.BarW + pad + m.IconsOver, y + m.BlockH + m.IconsH + pad);
        Drag(unlocked, r0, r1, new Vector2(left + m.BarW * 0.5f, y + m.BlockH * 0.5f));

        ImDrawListPtr dl = ImGuiNET.ImGui.GetBackgroundDrawList();
        if (unlocked)
        {
            // Unlocked: a frame says it can be moved (brighter while hovered or dragged).
            float a = Math.Clamp(_alpha, 0.35f, 1f);
            bool hot = _hover || _dragging;
            dl.AddRectFilled(r0, r1, MonsterHud.Mul(hot ? 0x40000000u : 0x28000000u, a), 3f);
            dl.AddRect(r0, r1, MonsterHud.Mul(hot ? FrameHot : FrameCol, a), 3f, ImDrawFlags.None, hot ? 1.5f : 1f);
        }
        Paint(dl, ref l, left, y, f.Now, underUi: false);
    }

    private static uint FrameCol => RynthTheme.WithAlpha(RynthTheme.Accent, 0xB0);
    private static uint FrameHot => RynthTheme.Accent;

    /// <summary>Hover and a left-drag on the unlocked plate (the MonsterHud plate-click pattern).</summary>
    private static void Drag(bool unlocked, Vector2 r0, Vector2 r1, Vector2 centre)
    {
        bool prevHover = _hover;
        _hover = false;
        ImGuiIOPtr io = ImGuiNET.ImGui.GetIO();
        bool usable = unlocked && Win32Backend.IsUiCaptureEnabled() && (!io.WantCaptureMouse || _dragging);
        if (usable)
        {
            Vector2 m = io.MousePos;
            _hover = m.X >= r0.X && m.X <= r1.X && m.Y >= r0.Y && m.Y <= r1.Y;
        }
        if (!_dragging && usable && (_hover || prevHover) && Win32Backend.MouseCaptured &&
            ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Left) &&
            !ImGuiNET.ImGui.IsMouseDown(ImGuiMouseButton.Right) && !ImGuiNET.ImGui.IsMouseDown(ImGuiMouseButton.Middle))
        {
            // The press went where last frame's hover put the capture: the drag starts.
            _dragging = true;
            _grab = io.MousePos - centre;
        }
        if (_hover || _dragging)
        {
            ImGuiNET.ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
            if (_hover && !_dragging)
                SafeImGui.SetTooltip(DragTip);
        }
        _wantsMouse = _hover || _dragging;
    }

    private const string DragTip = "Drag to move your plate.\nLock it again in Settings > Interface > Your Vitals or Vision > Nameplates > Your plate (or /rv plates self lock).";

    /// <summary>Fades the plate in / out and steps the bars; false when nothing should be painted.</summary>
    private static bool Advance(ref MonsterHud.Frame f, bool show)
    {
        float kAlpha = 1f - MathF.Exp(-f.Dt * 12f);
        _alpha += ((show ? MonsterHudSettings.Opacity : 0f) - _alpha) * kAlpha;
        if (!show) { if (_alpha < 0.02f) _alpha = 0f; return false; }
        if (_alpha < 0.01f) return false;

        RefreshDebuffs(f.Now);
        PlayerVitalsHooks.TryGetSnapshot(out PlayerVitalsSnapshot v);
        float kBar = 1f - MathF.Exp(-f.Dt * 14f);
        bool loss = MonsterHudSettings.SelfDebuffLoss;
        // The full maximums each frame from the latest totals, so a debuff's lowered maximum (which AC
        // sends at once) and its totals (read 4x a second) only disagree until the next read.
        Step(ref _hp, v.Health, v.MaxHealth, loss ? VitalLoss.FullMax(v.MaxHealth, in _vitalMods[VitalLoss.Health]) : v.MaxHealth, f.Now, kBar, f.Dt);
        Step(ref _st, v.Stamina, v.MaxStamina, loss ? VitalLoss.FullMax(v.MaxStamina, in _vitalMods[VitalLoss.Stamina]) : v.MaxStamina, f.Now, kBar, f.Dt);
        Step(ref _mn, v.Mana, v.MaxMana, loss ? VitalLoss.FullMax(v.MaxMana, in _vitalMods[VitalLoss.Mana]) : v.MaxMana, f.Now, kBar, f.Dt);
        return true;
    }

    /// <summary>
    /// "Debuffs on you" and "Grey out what debuffs take": the snapshot read, the icons picked and the
    /// vitals' totals summed, four times a second. Render thread.
    /// </summary>
    private static void RefreshDebuffs(long nowTicks)
    {
        bool icons = MonsterHudSettings.SelfDebuffs, loss = MonsterHudSettings.SelfDebuffLoss;
        if (!icons) _iconCount = 0;
        if (!loss) for (int i = 0; i < _vitalMods.Length; i++) _vitalMods[i] = VitalMods.None;
        if (!icons && !loss) return;
        long ms = Environment.TickCount64;
        if (ms < _nextDebuffRead) return;
        _nextDebuffRead = ms + DebuffReadMs;
        int n = EnchantmentHooks.CopyPlayerStatMods(_eIds, _eExp, _eTypes, _eKeys, _eVals, _eCats);
        if (loss) VitalLoss.Collect(_eIds, _eTypes, _eKeys, _eVals, _eCats, n, _vitalMods);
        if (!icons) return;
        if (!PortalSpellTable.Ready) { _iconCount = 0; return; }   // MonsterHud.Update queued the load
        _iconCount = n > 0
            ? SelfDebuffs.Select(_eIds, _eExp, _eCats, n, TimeSyncHooks.GetCurrentServerTime(), nowTicks, Stopwatch.Frequency, Harmful, _icons)
            : 0;
    }

    /// <summary>The spell table only holds non-beneficial enchantments: a hit is harmful. Its kind and base length.</summary>
    private static bool LookupHarmful(uint spellId, out DebuffKind kind, out float duration)
    {
        if (PortalSpellTable.TryGetById(spellId, out DebuffSpell s))
        {
            kind = PlateDebuffs.KindOf(s);
            duration = (float)s.Duration;
            return true;
        }
        kind = DebuffKind.Other;
        duration = 0f;
        return false;
    }

    /// <summary>The plate's sizes, fonts and look this frame.</summary>
    private struct Layout
    {
        // The lengths that need no font: bars, the name's room, the debuff row (SelfPlateMetrics, offline-tested).
        public SelfPlateMetrics M;
        public float SmallSize, NameSize;
        public bool Numbers;
        public ImFontPtr Small, NameFont;
        // Look options (MonsterHudSettings.SelfBarTrack ...), read once per frame.
        public bool Track, Chip, Outline, Band;
        // The debuff row's text (stack digit, "+N") before the row's own size scales it.
        public float IconTextSize;
        public ImFontPtr IconText;
    }

    private static Layout Measure(float uiScale)
    {
        Layout l = default;
        float k = MonsterHudSettings.Scale * uiScale;
        bool numbers = l.Numbers = MonsterHudSettings.SelfNumbers;
        // Sized from the 11 px bold bake, drawn from the smallest bake at least that big (no blur when scaled up;
        // nothing is re-baked when the text size changes).
        float text = MonsterHudSettings.Scale * MonsterHudSettings.SelfTextSize;
        l.SmallSize = MathF.Round(ImGuiFonts.Get(UiFont.UiBold11).FontSize * text);
        l.Small = ImGuiFonts.Sharp(l.SmallSize, bold: true);
        l.NameSize = MathF.Round(ImGuiFonts.Get(UiFont.UiBold11).FontSize * text);
        l.NameFont = ImGuiFonts.Sharp(l.NameSize, bold: true);
        // With numbers on, the bars grow to fit bold, outlined text that reads over any fill. Width, height
        // and text each have their own multiplier on top (1 = the default look). The debuff row takes room
        // only while there is an icon, at "Your debuff icon size" (not the text size).
        l.M = SelfPlateMetrics.Measure(k, numbers, MonsterHudSettings.SelfBarWidth, MonsterHudSettings.SelfBarHeight,
            MonsterHudSettings.SelfName ? l.NameSize : 0f, _iconCount > 0, MonsterHudSettings.SelfDebuffIconSize);
        l.Track = MonsterHudSettings.SelfBarTrack;
        l.Chip = MonsterHudSettings.SelfBarChip;
        l.Outline = MonsterHudSettings.SelfTextOutline;
        l.Band = MonsterHudSettings.SelfNumberBand;
        // The debuff row's small text: the monster plates' 9 px bake at your text size (the row scales it).
        l.IconTextSize = MathF.Round(ImGuiFonts.Get(UiFont.Ui9).FontSize * text);
        l.IconText = ImGuiFonts.Sharp(l.IconTextSize, bold: false);
        // Icons made bigger than the default can be wider than the bars: the clamp and the drag area cover them.
        if (_iconCount > 0 && l.M.Row.Scale > 1f)
            l.M.IconsOver = l.M.Row.Overhang(DebuffIconRow.Width(_icons.Length, _iconCount, others: false, in l.M.Row,
                l.IconText, l.IconTextSize, ref _overflowKey, ref _overflowText), l.M.BarW);
        return l;
    }

    /// <summary>
    /// Paints the three bars (and the name over them, the debuff row under them) with the bars' top-left
    /// at (left, y); returns the plate's top. <paramref name="now"/> is Stopwatch ticks; <paramref name="underUi"/>:
    /// <paramref name="dl"/> is the layer under AC's windows (the debuff tooltips then give way to them).
    /// </summary>
    private static float Paint(ImDrawListPtr dl, ref Layout l, float left, float y, long now, bool underUi)
    {
        float a = Math.Clamp(_alpha, 0f, 1f);
        ref SelfPlateMetrics m = ref l.M;
        PaintOne(dl, ref _hp, ref l, left, y, a, HealthCol);
        PaintOne(dl, ref _st, ref l, left, y + m.BarH + m.Gap, a, StaminaCol);
        PaintOne(dl, ref _mn, ref l, left, y + 2 * (m.BarH + m.Gap), a, ManaCol);
        // No tooltip mid-drag: the drag owns the cursor.
        if (_iconCount > 0)
            DebuffIconRow.Paint(dl, _icons, _iconCount, others: false, in m.Row, l.IconText, l.IconTextSize,
                ref _overflowKey, ref _overflowText, left + m.BarW * 0.5f, y + m.RowTop, a, now,
                _dragging ? DebuffRowTips.Off : DebuffRowTips.Self, underUi);
        float top = y - 1f;
        if (MonsterHudSettings.SelfName)
        {
            string name = PlayerName();
            if (name.Length > 0)
            {
                float w = MonsterHud.TextWidth(l.NameFont, l.NameSize, name);
                var pos = new Vector2(MathF.Round(left + (m.BarW - w) * 0.5f), MathF.Round(y - 3f * m.K - l.NameSize));
                if (l.Outline) MonsterHud.OutlinedText(dl, l.NameFont, l.NameSize, pos, a, name);
                dl.AddText(l.NameFont, l.NameSize, pos, MonsterHud.Mul(NameCol, a), name);
                top = pos.Y;
            }
        }
        return top;
    }

    /// <summary>The camera at (or inside) the head: AC's first-person view.</summary>
    private static bool IsFirstPerson(ref MonsterHud.Frame f)
    {
        if (!GameMatrixCapture.TryGetCameraPosition(out float cx, out float cy, out float cz)) return false;
        if (!MonsterHud.TryOffset(f.FrameCell, f.PlayerCell, out float ox, out float oy)) return false;
        // Camera is in D3D axes (x east, y up, z north); the eyes sit about 1.6 m up.
        float dx = cx - (f.Px + ox), dy = cy - (f.Pz + 1.6f), dz = cz - (f.Py + oy);
        return dx * dx + dy * dy + dz * dz < FirstPersonDistance * FirstPersonDistance;
    }

    /// <summary>
    /// One bar's step: <paramref name="max"/> is AC's (debuffed) maximum, <paramref name="full"/> the one
    /// without debuffs that the bar spans (= max when nothing is lost or the option is off).
    /// </summary>
    private static void Step(ref BarState b, uint cur, uint max, uint full, long now, float kBar, float dt)
    {
        if (full < max) full = max;
        float ratio = full > 0 ? Math.Clamp((float)cur / full, 0f, 1f) : 0f;
        float cap = full > 0 ? Math.Clamp((float)max / full, 0f, 1f) : 1f;
        b.Cur = cur; b.Max = max;
        if (!b.Init || _alpha <= 0.02f)
        {
            b.Shown = b.Trail = ratio;
            b.Cap = cap;
            b.Hold = 0;
            b.Init = true;
            return;
        }
        b.Cap += (cap - b.Cap) * kBar;
        if (MathF.Abs(cap - b.Cap) < 0.0005f) b.Cap = cap;
        MonsterHud.StepBar(ref b.Shown, ref b.Trail, ref b.Hold, ratio, now, kBar, dt);
    }

    private static void PaintOne(ImDrawListPtr dl, ref BarState b, ref Layout l, float left, float top, float a, uint col)
    {
        float w = l.M.BarW, h = l.M.BarH, size = l.SmallSize;
        ImFontPtr font = l.Small;
        var b0 = new Vector2(left, top);
        var b1 = new Vector2(left + w, top + h);
        MonsterHud.PaintBar(dl, b0, b1, l.M.Round, a, b.Shown, b.Trail, col, ticks: false, track: l.Track, chip: l.Chip, cap: b.Cap);
        if (!l.Numbers || h < 8f || b.Max == 0) return;
        long key = ((long)b.Max << 32) | b.Cur;
        string? text = b.Text;
        if (key != b.TextKey || text == null)
        {
            b.TextKey = key;
            text = b.Text = b.Cur.ToString(CultureInfo.InvariantCulture) + " / " + b.Max.ToString(CultureInfo.InvariantCulture);
        }
        float tw = MonsterHud.TextWidth(font, size, text);
        var pos = new Vector2(MathF.Round(left + (w - tw) * 0.5f), MathF.Round(top + (h - size) * 0.5f));
        // A dark band behind the text, then an outline, so it reads over bright and dark fills alike
        // (each can be turned off). Text sized bigger than the bar: the band grows to the text.
        if (l.Band)
            dl.AddRectFilled(new Vector2(pos.X - 3f, MathF.Min(top + 1f, pos.Y)), new Vector2(pos.X + tw + 3f, MathF.Max(top + h - 1f, pos.Y + size)),
                MonsterHud.Mul(0x70000000u, a), 2f);
        if (l.Outline) MonsterHud.OutlinedText(dl, font, size, pos, a, text);
        dl.AddText(font, size, pos, MonsterHud.Mul(NumCol, a), text);
    }

    private static string _name = "";
    private static uint _nameOwner;

    private static string PlayerName()
    {
        uint id = ClientHelperHooks.GetPlayerId();
        if (id != _nameOwner && ClientObjectHooks.TryGetSnapshotName(id, out string n) && n.Length > 0)
        {
            _nameOwner = id;
            _name = n;
        }
        return _name;
    }

    public static string Describe() =>
        $"Self plate {(MonsterHudSettings.SelfPlate ? "on" : "off")}" +
        (MonsterHudSettings.SelfPlacement == SelfPlacement.Fixed
            ? (MonsterHudSettings.SelfLocked ? ", fixed on screen (locked)." : ", fixed on screen (unlocked: drag it).")
            : FirstPerson ? ", follows you (first person" + (MonsterHudSettings.SelfHideFirstPerson ? ", hidden)." : ").") : ", follows you.") +
        (MonsterHudSettings.SelfDebuffs
            ? $" Debuffs on you: {_iconCount} icon(s){(PortalSpellTable.Ready ? "" : " (spell table not loaded)")}."
            : "");
}
