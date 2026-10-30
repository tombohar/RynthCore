// ============================================================================
//  RynthCore.Engine - Plugins/UiKit.cs
//  Plugin API v89, for the windows plugins draw (docs/PLUGIN_WINDOWS.md): the
//  player's colour theme (GetUiColor), the engine's UI fonts at the current
//  panel's text size (GetUiFont), and opening or closing a panel by its key
//  (SetPanelOpen). v90 (2026-10-21), for windows that draw their own frame
//  ("chrome": "none"): dragging the panel from the plugin's own title row
//  (PanelDragWithLastItem), popping it out (SetPanelPopOut), its text size
//  (PanelTextSize), and the instrument look's colours and fonts.
//  Nothing here throws across the boundary.
// ============================================================================

using System;
using System.Runtime.InteropServices;
using RynthCore.Engine.Compatibility;
using RynthCore.Engine.ImGuiBackend;
using RynthCore.Engine.ImGuiBackend.Panels;
using RynthCore.Engine.UI;

namespace RynthCore.Engine.Plugins;

internal static unsafe class UiKit
{
    /// <summary>Longest panel key SetPanelOpen reads.</summary>
    public const int MaxKeyLength = 64;

    /// <summary>v89 GetUiColor: ImGui's packed colour (ABGR) for a token, 0 for an unknown one. Any thread.</summary>
    public static uint GetUiColor(int token)
    {
        try { return Color((UiColorToken)token); }
        catch { return 0; }
    }

    /// <summary>The theme colour behind a token (the values the engine's own faces draw with).</summary>
    internal static uint Color(UiColorToken token) => token switch
    {
        UiColorToken.Text => RynthTheme.Text,
        UiColorToken.TextMuted => RynthTheme.MuteAlt,
        UiColorToken.Accent => RynthTheme.Accent,
        UiColorToken.Highlight => RynthTheme.Highlight,
        UiColorToken.Button => RynthTheme.Button,
        UiColorToken.Border => RynthTheme.Border,
        UiColorToken.Field => RynthTheme.Field,
        UiColorToken.Selected => RynthTheme.Selected,
        UiColorToken.RowAlt => RynthTheme.RowAlt,
        UiColorToken.Background => RynthTheme.Background,
        UiColorToken.PanelGlass => RynthTheme.PanelGlass,
        UiColorToken.AccentFill => RynthTheme.AccentFill,
        UiColorToken.BoxBg => RynthTheme.BoxBg,
        UiColorToken.FrameBg => RynthTheme.FrameBg,
        UiColorToken.FrameHover => RynthTheme.FrameHover,
        UiColorToken.FrameActive => RynthTheme.FrameActive,
        UiColorToken.Green => FaceKit.Green,
        UiColorToken.Red => FaceKit.Red,
        UiColorToken.TitleBar => RynthTheme.TitleBar,
        UiColorToken.WindowBorder => RynthTheme.WindowBorder,
        UiColorToken.Muted => RynthTheme.TextMuted,
        UiColorToken.KeyFace => RynthTheme.KeyFace,
        UiColorToken.Hairline => RynthTheme.Hairline,
        UiColorToken.Inset => RynthTheme.Inset,
        UiColorToken.Faint => RynthTheme.Faint,
        _ => 0,
    };

    /// <summary>The engine font behind a v89 font token, or null for an unknown one.</summary>
    internal static UiFont? Font(UiFontToken token) => token switch
    {
        UiFontToken.Ui9 => UiFont.Ui9,
        UiFontToken.Ui10 => UiFont.Ui10,
        UiFontToken.Ui11 => UiFont.Ui11,
        UiFontToken.Ui14 => UiFont.Ui14,
        UiFontToken.Ui18 => UiFont.Ui18,
        UiFontToken.UiBold11 => UiFont.UiBold11,
        UiFontToken.UiBold14 => UiFont.UiBold14,
        UiFontToken.Mono9 => UiFont.Mono9,
        UiFontToken.Mono10 => UiFont.Mono10,
        UiFontToken.Mono12 => UiFont.Mono12,
        UiFontToken.Chat10 => UiFont.Chat10,
        UiFontToken.Chat12 => UiFont.Chat12,
        UiFontToken.Chat14 => UiFont.Chat14,
        UiFontToken.Dash9 => UiFont.Dash9,
        UiFontToken.Dash10 => UiFont.Dash10,
        UiFontToken.Dash11 => UiFont.Dash11,
        UiFontToken.Dash14 => UiFont.Dash14,
        UiFontToken.Narrow9 => UiFont.Narrow9,
        UiFontToken.Narrow10 => UiFont.Narrow10,
        UiFontToken.Narrow11 => UiFont.Narrow11,
        UiFontToken.NarrowBold11 => UiFont.NarrowBold11,
        UiFontToken.NarrowBold13 => UiFont.NarrowBold13,
        _ => null,
    };

    /// <summary>
    /// v89 GetUiFont: the <c>ImFont*</c> for a token at the text size of the panel being drawn
    /// (the host sets it around each face), null for an unknown token or off AC's thread.
    /// </summary>
    public static IntPtr GetUiFont(int font)
    {
        try
        {
            if (!MainThreadGuard.IsOnMainThread()) return IntPtr.Zero;
            if (Font((UiFontToken)font) is not UiFont f) return IntPtr.Zero;
            return (IntPtr)ImGuiFonts.Get(f).NativePtr;
        }
        catch { return IntPtr.Zero; }
    }

    /// <summary>
    /// v89 SetPanelOpen: mode 1 open, 0 close, 2 toggle, -1 ask. 1 open / 0 closed afterwards (the
    /// request lands on the next frame), -1 for no such panel or a bad call. Any thread.
    /// </summary>
    public static int SetPanelOpen(byte* key, int mode)
    {
        try
        {
            if (key == null) return -1;
            string? title = Marshal.PtrToStringUTF8((IntPtr)key);
            if (string.IsNullOrWhiteSpace(title) || title.Length > MaxKeyLength) return -1;
            title = PanelRouter.Canonical(title.Trim());
            if (!ImGuiPanelHost.HasFace(title)) return -1;
            bool open = ImGuiPanelHost.IsOpen(title);
            switch (mode)
            {
                case -1: return open ? 1 : 0;
                case 0: if (open) ImGuiPanelHost.Close(title); return 0;
                case 1: if (!open) ImGuiPanelHost.Open(title); return 1;
                case 2: PanelRouter.Toggle(title); return open ? 0 : 1;
                default: return -1;
            }
        }
        catch { return -1; }
    }

    /// <summary>
    /// v90 PanelDragWithLastItem: the panel being drawn follows the mouse while ImGui's last item is
    /// held and dragged (ImGuiPanelHost.DragWindowWithLastItem: docked, the window moves, clamped to
    /// the client; popped out, its OS window). Only inside a panel's draw on AC's thread.
    /// </summary>
    public static void PanelDragWithLastItem()
    {
        try
        {
            if (!MainThreadGuard.IsOnMainThread() || ImGuiPanelHost.DrawingTitle == null) return;
            ImGuiPanelHost.DragWindowWithLastItem();
        }
        catch { }
    }

    /// <summary>
    /// v90 SetPanelPopOut: mode 1 pop out, 0 dock, 2 toggle, -1 ask. 1 popped out / 0 docked
    /// afterwards (the request lands on the next frame), -1 for no such panel, one that isn't open,
    /// or a bad call. Popping out needs the pop-out windows (else it stays docked: 0). Any thread.
    /// </summary>
    public static int SetPanelPopOut(byte* key, int mode)
    {
        try
        {
            if (ReadKey(key) is not string title || !ImGuiPanelHost.IsOpen(title)) return -1;
            bool popped = ImGuiPanelHost.IsPopped(title);
            bool want = mode switch { 0 => false, 1 => true, 2 => !popped, -1 => popped, _ => popped };
            if (mode is < -1 or > 2) return -1;
            if (want && !PanelRouter.CanPopOut) return popped ? 1 : 0;
            if (want != popped)
            {
                if (want) ImGuiPanelHost.PopOut(title);
                else ImGuiPanelHost.Redock(title);
            }
            return want ? 1 : 0;
        }
        catch { return -1; }
    }

    /// <summary>
    /// v90 PanelTextSize: steps the panel's text size by <paramref name="delta"/> (its own size, as
    /// Ctrl+wheel on a header does: PanelTextScale.SetForWindow) and returns the size as a percentage
    /// of normal; 0 just asks. -1 for no such panel. Any thread.
    /// </summary>
    public static int PanelTextSize(byte* key, int delta)
    {
        try
        {
            if (ReadKey(key) is not string title) return -1;
            int step = PanelTextScale.Get(title);
            if (delta != 0)
            {
                step = Math.Clamp(step + Math.Clamp(delta, -10, 10), 0, PanelTextScale.SizeNames.Length - 1);
                PanelTextScale.SetForWindow(title, step);
            }
            return (int)MathF.Round(PanelTextScale.Factor(step) * 100f);
        }
        catch { return -1; }
    }

    /// <summary>
    /// v90 PanelScale: how big a frameless window draws itself (PanelDrawScale), in percent of normal:
    /// <paramref name="percent"/> 0 asks, -1 goes back to following the text size, 30-250 sets it.
    /// Returns the size now (0 = it follows the text size), -1 for a bad call. Any thread.
    /// </summary>
    public static int PanelScale(byte* key, int percent)
    {
        try
        {
            if (ReadKey(key) is not string title) return -1;
            if (percent == -1) PanelDrawScale.Set(title, 0);
            else if (percent > 0) PanelDrawScale.Set(title, percent);
            return PanelDrawScale.Get(title);
        }
        catch { return -1; }
    }

    /// <summary>
    /// v90 RenderSettingsDrawer: the body of a dashboard drawer that shows Settings rows ("ranges",
    /// "patrol", "pinned"; SettingsDrawerBodies) at the cursor, <paramref name="width"/> pixels wide
    /// and at most <paramref name="room"/> tall (0 = its own height); flags 1: open My settings' list
    /// of settings to pin. Since 2026-10-27 also the Vision window's engine-owned tabs for RynthVision's
    /// own window ("nameplates", "combattext"). All of them draw popped out too (2026-10-24: RynthAi's
    /// popped-out dashboard has its drawers; SettingsDrawerBodies keeps a body's sources between its
    /// draws). Returns the height it wants (pixels), -1 for another key or outside a panel's draw on
    /// AC's thread.
    /// </summary>
    public static float RenderSettingsDrawer(byte* key, float width, float room, int flags)
    {
        try
        {
            if (key == null || !MainThreadGuard.IsOnMainThread() || ImGuiPanelHost.DrawingTitle == null
                || !float.IsFinite(width) || !float.IsFinite(room)) return -1f;
            string? name = Marshal.PtrToStringUTF8((IntPtr)key);
            if (string.IsNullOrEmpty(name) || name.Length > MaxKeyLength) return -1f;
            return SettingsDrawerBodies.Draw(name, width, MathF.Max(0f, room), (flags & 1) != 0);
        }
        catch (Exception ex)
        {
            if (!_settingsDrawerFailLogged)
            {
                _settingsDrawerFailLogged = true;
                RynthLog.Warn("UiKit.RenderSettingsDrawer: " + ex.GetType().Name + ": " + ex.Message);
            }
            return -1f;
        }
    }

    private static bool _settingsDrawerFailLogged;

    /// <summary>A registered panel's key from a plugin's UTF-8 text (the router's name for it), or null.</summary>
    private static string? ReadKey(byte* key)
    {
        if (key == null) return null;
        string? title = Marshal.PtrToStringUTF8((IntPtr)key);
        if (string.IsNullOrWhiteSpace(title) || title.Length > MaxKeyLength) return null;
        title = PanelRouter.Canonical(title.Trim());
        return ImGuiPanelHost.HasFace(title) ? title : null;
    }
}
