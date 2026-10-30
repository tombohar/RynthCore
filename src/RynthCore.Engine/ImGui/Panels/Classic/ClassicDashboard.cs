// ============================================================================
//  RynthCore.Engine - ImGui/Panels/Classic/ClassicDashboard.cs
//  The RynthAi dashboard's Classic look (Tom 2026-10-19: "implement the AC
//  style as an option"): one retail-style window, drawn with the client's own
//  UI pictures (ClassicKit), with eight tabs: Main (every VTank-options-page
//  setting, the macro, live systems, recharge meters), Monsters, Gear, Buffs,
//  Route, Loot, Meta and Tuning. The design is the RynthAi Classic canvas
//  (claude.ai artifact 2VCb8Lk7vGjBfoX73PJKVL, "Classic" page); our own layout
//  and wording, the retail art read from the player's install.
//
//  RynthAiFace draws this instead of the dashboard while
//  RynthAiDashboardState.Classic is on; the title bar's Modern button and
//  Settings > Display switch back. Minimized (the dashboard's shared flag)
//  shows the title bar over a compact strip: the systems, Stop/Start and the
//  three vitals meters.
//
//  Layout, in retail px times ClassicKit.S (the UI scale x the dashboard's
//  size): a 5 px gold frame; the 25 px title bar and its close button; the
//  25 px tab row; the body (8 px in from the frame). The window is 400 retail
//  px wide and as tall as the open tab's content (measured each frame, fitted
//  when it changes, like the dashboard's MeasureAndFit).
//
//  AC's render thread only.
// ============================================================================

using System;
using System.Numerics;
using ImGuiNET;
using RynthCore.Engine.UI;
using RynthCore.Engine.UI.Data;
using static RynthCore.Engine.ImGuiBackend.Panels.Classic.ClassicKit;

namespace RynthCore.Engine.ImGuiBackend.Panels.Classic;

/// <summary>One tab of the Classic window.</summary>
internal interface IClassicTab
{
    string Name { get; }
    /// <summary>The tab became the open one (subscribe to what it reads).</summary>
    void Shown();
    /// <summary>The tab stopped showing (unsubscribe).</summary>
    void Hidden();
    /// <summary>Draws the tab's body at <paramref name="o"/>, <paramref name="w"/> wide; returns the height it used.</summary>
    float Draw(ClassicContext ctx, Vector2 o, float w);
}

/// <summary>What every tab gets each frame.</summary>
internal sealed class ClassicContext
{
    public RynthAiView? View;
    public RynthAiSnapshot Raw = new();
    public readonly Popup Popup = new("##classic_popup");
    /// <summary>Opens another tab (the Main tab's "More" goes to Tuning).</summary>
    public Action<int> OpenTab = _ => { };
}

internal sealed class ClassicDashboard
{
    public const float RetailW = 400, FrameW = 5, TitleH = 25, TabH = 25, BodyPad = 8, BottomPad = 10;
    private const string SizePopupId = "##classic_size";

    private readonly IClassicTab[] _tabs;
    private readonly ClassicContext _ctx = new();
    private int _shownTab = -1;
    private float _contentH;          // the open tab's body height last frame (screen px)
    private float _fittedH = -1, _fittedW = -1;

    public ClassicDashboard()
    {
        _tabs = new IClassicTab[]
        {
            new ClassicMainTab(), new ClassicMonstersTab(), new ClassicGearTab(), new ClassicBuffsTab(),
            new ClassicRouteTab(), new ClassicLootTab(), new ClassicMetaTab(), new ClassicTuningTab(),
        };
        _ctx.OpenTab = i => RynthAiDashboardState.SetClassicTab(Math.Clamp(i, 0, _tabs.Length - 1));
    }

    /// <summary>The window's minimum (logical px): the retail width and the content's height.</summary>
    public Vector2 MinSize(float k)
    {
        float s = Scale(k);
        float h = _fittedH > 0 ? _fittedH : 200 * s;
        return new Vector2(RetailW * s / Placement(), h);
    }

    public void OnShown() { _shownTab = -1; _fittedH = -1; _fittedW = -1; }

    public void OnHidden()
    {
        if (_shownTab >= 0) _tabs[_shownTab].Hidden();
        _shownTab = -1;
    }

    private static float Placement() => Math.Max(0.01f, ImGuiPanelHost.PlacementScale());

    private static float Scale(float k)
    {
        float ui = EngineFrameController.FontScale;
        if (!(ui > 0f) || !float.IsFinite(ui)) ui = 1f;
        return Math.Max(0.5f, ui * k);
    }

    public void Draw(RynthAiView? view, RynthAiSnapshot raw, float k)
    {
        S = Scale(k);
        _ctx.View = view;
        _ctx.Raw = raw;
        bool minimized = RynthAiDashboardState.Minimized;
        int tab = Math.Clamp(RynthAiDashboardState.ClassicTab, 0, _tabs.Length - 1);
        if (minimized) tab = -1;
        if (tab != _shownTab)
        {
            if (_shownTab >= 0) _tabs[_shownTab].Hidden();
            if (tab >= 0) _tabs[tab].Shown();
            _shownTab = tab;
        }

        Vector2 wp = ImGuiNET.ImGui.GetWindowPos(), ws = ImGuiNET.ImGui.GetWindowSize();
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        dl.PushClipRect(wp, wp + ws, false);
        try
        {
            float w = ws.X;
            float frame = Px(FrameW);
            var inner0 = wp + new Vector2(frame, frame);
            float innerW = w - 2 * frame;
            byte alpha = view?.PanelAlpha ?? 242;

            // Body backdrop first (the title and tabs draw over its top).
            Backdrop(dl, new Vector2(inner0.X, inner0.Y + Px(TitleH)), new Vector2(inner0.X + innerW, wp.Y + ws.Y - frame), alpha);

            TitleRow(dl, inner0, innerW, raw, minimized);
            float y = inner0.Y + Px(TitleH);
            float used;
            if (minimized)
            {
                used = CompactStrip(dl, new Vector2(inner0.X + Px(BodyPad), y + Px(6)), innerW - 2 * Px(BodyPad), view, raw) + Px(6);
            }
            else
            {
                TabRow(dl, new Vector2(inner0.X, y), innerW, tab);
                y += Px(TabH);
                float body = _tabs[tab].Draw(_ctx, new Vector2(inner0.X + Px(BodyPad), y + Px(BodyPad)), innerW - 2 * Px(BodyPad));
                used = Px(TabH) + Px(BodyPad) + body + Px(BottomPad);
            }
            Frame(dl, wp, wp + ws);

            _contentH = Px(TitleH) + used + 2 * frame;
            Fit(w);
        }
        finally
        {
            dl.PopClipRect();
        }
        _ctx.Popup.Draw();
        SizePopup();
    }

    /// <summary>Fits the window to the retail width and the content's height when either changed.</summary>
    private void Fit(float w)
    {
        if (ImGuiPopOuts.InPopOutFrame) return;
        float place = Placement();
        float wantW = RetailW * S, wantH = MathF.Ceiling(_contentH);
        if (Math.Abs(wantH - _fittedH) < 1 && Math.Abs(wantW - _fittedW) < 1) return;
        _fittedH = wantH;
        _fittedW = wantW;
        ImGuiPanelHost.RequestSize(RynthAiFace.Title, new Vector2(wantW / place, wantH / place));
        // Applied after the size, clamped to the screen at it (the dashboard's MeasureAndFit does the same):
        // a taller tab near the bottom of the screen moves the window up instead of running off it.
        ImGuiPanelHost.RequestPos(RynthAiFace.Title, ImGuiNET.ImGui.GetWindowPos() / place);
    }

    // ── Title bar ───────────────────────────────────────────────────────

    private void TitleRow(ImDrawListPtr dl, Vector2 o, float w, RynthAiSnapshot raw, bool minimized)
    {
        float h = Px(TitleH), closeW = Px(24);
        float barW = w - closeW;
        if (!HSlice(dl, Art.TitleBar, o, o + new Vector2(barW, h), 276, 14, 20))
        {
            dl.AddRectFilled(o, o + new Vector2(barW, h - 1), Black);
            dl.AddRect(o, o + new Vector2(barW, h - 1), Gold);
        }

        // Buttons at the bar's right end: Modern look, minimize / expand.
        float btn = Px(17), by = o.Y + (h - btn) * 0.5f;
        float bx = o.X + barW - Px(20) - btn;
        if (TitleIcon("##cl_min", minimized ? PhosphorIcons.ArrowsOutLineVertical : PhosphorIcons.ArrowsInLineVertical, new Vector2(bx, by), btn))
            RynthAiDashboardState.SetMinimized(!minimized);
        SafeImGui.SetItemTooltip(minimized ? "Expand" : "Minimize");
        bx -= btn + Px(3);
        if (TitleIcon("##cl_modern", PhosphorIcons.SquaresFour, new Vector2(bx, by), btn))
            RynthAiFace.SwitchLook(classic: false);
        SafeImGui.SetItemTooltip("Modern look (the compact dashboard)");
        float buttonsLeft = bx - Px(4);

        // Drag surface: the bar left of the buttons. Right-click: the window's size.
        ImGuiNET.ImGui.SetCursorScreenPos(o);
        ImGuiNET.ImGui.InvisibleButton("##cl_drag", new Vector2(Math.Max(1, buttonsLeft - o.X), h));
        ImGuiPanelHost.DragWindowWithLastItem();
        SafeImGui.SetItemTooltip("Drag to move.  Right-click: size.  Ctrl+wheel: bigger or smaller.");
        if (ImGuiNET.ImGui.IsItemClicked(ImGuiMouseButton.Right)) ImGuiNET.ImGui.OpenPopup(SizePopupId);
        // Ctrl+wheel: the panel host steps its size (over any panel).

        // "RynthAi" in gold, then the profile and what the bot is doing.
        float tx = o.X + Px(10);
        TextMid(dl, tx, o.Y, h, "RynthAi", Gold, 12, bold: true);
        tx += Measure("RynthAi", 12, true).X + Px(8);
        string sub = (raw.MacroRunning ? "running" : "stopped") + " · " + (_ctx.View?.ProfileText ?? "Default");
        TextClip(dl, tx, o.Y, h, buttonsLeft, sub, raw.MacroRunning ? Green : Dim, 10);

        // The retail close button.
        if (CloseButton("##cl_close", new Vector2(o.X + barW, o.Y)))
            ImGuiPanelHost.Close(RynthAiFace.Title);
        SafeImGui.SetItemTooltip("Close");
    }

    private static bool TitleIcon(string id, string glyph, Vector2 pos, float size)
    {
        ImGuiNET.ImGui.SetCursorScreenPos(pos);
        bool clicked = ImGuiNET.ImGui.InvisibleButton(id, new Vector2(size, size));
        bool hot = ImGuiNET.ImGui.IsItemHovered();
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        if (hot) dl.AddRect(pos, pos + new Vector2(size, size), FaceKit.Faded(Gold), Px(2));
        PhosphorIcons.DrawCentered(dl, ImGuiFonts.Get(UiFont.Ui11), glyph, pos, new Vector2(size, size), hot ? Gold : GoldDim);
        return clicked;
    }

    private void SizePopup()
    {
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.PopupBg, RynthTheme.Background);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, Gold);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8, 8));
        bool open = ImGuiNET.ImGui.BeginPopup(SizePopupId);
        ImGuiNET.ImGui.PopStyleVar();
        ImGuiNET.ImGui.PopStyleColor(2);
        if (!open) return;
        try { ImGuiPanelHost.TextSizeRows(RynthAiFace.Title, S, "Window size"); }
        finally { ImGuiNET.ImGui.EndPopup(); }
    }

    // ── Tabs ────────────────────────────────────────────────────────────

    private void TabRow(ImDrawListPtr dl, Vector2 o, float w, int open)
    {
        dl.AddRectFilled(o, o + new Vector2(w, Px(TabH)), Black);
        float x = o.X;
        for (int i = 0; i < _tabs.Length; i++)
        {
            // Even widths that add up to the row exactly.
            float next = o.X + w * (i + 1) / _tabs.Length;
            float tw = MathF.Round(next) - MathF.Round(x);
            if (Tab("##cl_tab" + i, _tabs[i].Name, new Vector2(MathF.Round(x), o.Y), tw, i == open))
                RynthAiDashboardState.SetClassicTab(i);
            x = next;
        }
    }

    // ── Minimized: systems, Stop/Start, vitals ──────────────────────────

    private static readonly string[] ShortNames = { "Combat", "Buff", "Nav", "Loot", "Meta" };

    private static float CompactStrip(ImDrawListPtr dl, Vector2 o, float w, RynthAiView? view, RynthAiSnapshot raw)
    {
        float y = o.Y;
        // Five gems (subsystem toggles), then Stop / Start on a gold plate.
        float plateW = Px(46);
        float gemW = (w - plateW - Px(6)) / 5;
        for (int i = 0; i < 5; i++)
        {
            bool on = SubsystemOn(raw, i);
            if (Gem("##cl_cs" + i, ShortNames[i], new Vector2(o.X + i * gemW, y), gemW, on, 20))
                RynthAiCommands.SetSubsystemEnabled(i, !on);
            SafeImGui.SetItemTooltip(SubsystemNames[i] + (on ? " on. Click to turn it off." : " off. Click to turn it on."));
        }
        if (Plate("##cl_cs_run", raw.MacroRunning ? "Stop" : "Start", new Vector2(o.X + w - plateW, y + Px(1.5f))))
            RynthAiCommands.ToggleMacro();
        SafeImGui.SetItemTooltip(raw.MacroRunning ? "Macro running. Click to stop it." : "Macro stopped. Click to start it.");
        y += Px(24);

        VitalView hp = view?.Health ?? default, st = view?.Stamina ?? default, mn = view?.Mana ?? default;
        y = CompactVital(dl, o.X, y, w, Vital.Health, hp);
        y = CompactVital(dl, o.X, y, w, Vital.Stamina, st);
        y = CompactVital(dl, o.X, y, w, Vital.Mana, mn);
        return y - o.Y;
    }

    private static float CompactVital(ImDrawListPtr dl, float x, float y, float w, Vital vital, VitalView v)
    {
        float rowH = Px(25), valW = Px(36);
        Heart(dl, new Vector2(x, y), vital);
        float mx = x + Px(31), mw = w - Px(31) - valW - Px(6);
        Meter(dl, new Vector2(mx, y + (rowH - Px(16)) * 0.5f), mw, vital, v.Fraction);
        TextRight(dl, x + w, y, rowH, (int)MathF.Round(v.Fraction * 100) + "%", Text, 10);
        return y + rowH;
    }

    internal static readonly string[] SubsystemNames = { "Combat", "Buffing", "Navigation", "Looting", "Meta" };

    internal static bool SubsystemOn(RynthAiSnapshot raw, int i) => i switch
    {
        0 => raw.CombatEnabled, 1 => raw.BuffingEnabled, 2 => raw.NavigationEnabled, 3 => raw.LootingEnabled, _ => raw.MetaEnabled,
    };
}
