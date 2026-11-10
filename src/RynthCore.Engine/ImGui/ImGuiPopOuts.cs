// ============================================================================
//  RynthCore.Engine - ImGui/ImGuiPopOuts.cs
//  Popped-out ImGui panels (docs/IMGUI_PARITY_PLAN.md §4.2, P5): a panel
//  popped out of the client is still its ImGui face, drawn by the engine into
//  its own window. No ImGui multi-viewport.
//
//  Per pop-out:
//    • a LayeredWindow owned by AC's window (non-activating, hidden from
//      Alt-Tab, moves and resizes without the OS modal loop), created on the
//      game thread by a posted message, never inline in EndScene;
//    • a secondary ImGui context sharing the main font atlas, fed from that
//      window's mouse messages, plus keys and the wheel routed from the game
//      window (the pop-out never takes focus, so AC keeps rendering);
//    • a PopOutSurface: render target → readback → UpdateLayeredWindow.
//  A pop-out renders at up to 30 Hz, and on every frame for a few frames after
//  input so clicks and typing feel immediate.
//
//  Tooltips and popups: the context's display is the monitor's work area, with
//  the panel drawn where it really is on it, so ImGui places a tooltip or menu
//  as it would in the client (flipping at the screen's edges). The picture is
//  the panel plus a transparent margin holding whatever reaches past it; with
//  nothing out there the margin is 0 and the picture is just the panel. The
//  window grows by the margin and moves so the panel stays put (LayeredWindow
//  .Present). Before this, the window was the panel and a tooltip below the
//  one-button-high popped-out bar was cut off at its edge. A tooltip in the
//  margin lets clicks through; a menu, or a window of the face's own drawn
//  past the panel (RynthAi's dashboard drawers, 2026-10-24), takes them where
//  it is drawn (transparent pixels still let them through).
//
//  Threads: everything here runs on AC's main thread, which both renders
//  (EndScene) and dispatches the game and pop-out WndProcs. No locks.
//  Shutdown releases every window, context and D3D surface; nothing survives
//  an engine reload.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;
using RynthCore.Engine.UI;

namespace RynthCore.Engine.ImGuiBackend;

internal static unsafe class ImGuiPopOuts
{
    private const uint WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_CHAR = 0x0102, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const uint WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_RBUTTONDOWN = 0x0204,
        WM_RBUTTONUP = 0x0205, WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208, WM_MOUSEWHEEL = 0x020A, WM_MOUSEHWHEEL = 0x020E;
    private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;
    private const int HotFramesAfterInput = 4;
    private const int MaxContentSide = 4096;   // a size-to-content pop-out's largest size (px)
    private const int MarginStep = 32;         // margins grow in steps, so a tooltip that follows the mouse doesn't resize the window every frame
    private const float OverflowSlack = 2f;    // anti-aliased edges reach ~1 px past the panel: not a tooltip
    // A margin's largest size (px). Room for any tooltip or menu; it bounds the readback
    // if something follows the mouse far off the panel (the inventory's drag ghost).
    private const int MaxMargin = 640;
    private static readonly long FrameInterval = Stopwatch.Frequency / 30;       // hovered or just used
    // Otherwise the face's own idle rate (IImGuiPanel.PopOutIdleHz, default 10 Hz).

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref POINT p);
    [DllImport("user32.dll")] private static extern short GetKeyState(int vk);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    private readonly record struct InputMessage(uint Msg, IntPtr WParam, IntPtr LParam);

    private sealed class PopOut
    {
        public required string Title;
        public required IntPtr Context;
        public int Left, Top, Width, Height;          // requested geometry (screen px)
        public int CaptionHeight, CaptionRightInset;  // native drag band (px)
        public LayeredWindow? Window;
        public bool Visible;
        public readonly PopOutSurface Surface = new();
        public readonly List<InputMessage> Input = new(32);
        public readonly bool[] Buttons = new bool[3];
        public long LastFrameTicks;
        public int ThemeApplied;                      // RynthTheme.Version this context's style was coloured at
        public int HotFrames = HotFramesAfterInput;
        public bool WantTextInput, DropTextFocus;
        public bool Hovered;
        // A pop-out that isn't a panel (the bar): its own draw, sized to its content.
        public Action<float, Vector2>? Draw;
        public Action<int, int>? OnMoved;
        public bool SizeToContent, ResizeGrip = true;
        public Vector2 ContentSize;
        public int ShownWidth, ShownHeight;           // the picture size last pushed
        public long IdleInterval = Stopwatch.Frequency / 10;
        public ulong LastHash;
        // The context's display (the work area around the panel) and the panel's place on it.
        public int OriginX, OriginY, DisplayW, DisplayH;
        // The picture's margins around the panel (px): room for tooltips and popups.
        public int MarginL, MarginT, MarginR, MarginB;
        // Cost, for /rc imgui diag: frames built / pictures pushed, and the time spent.
        public long Built, Pushed, BuildTicks, PushTicks;
    }

    private static readonly Dictionary<string, PopOut> Pops = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<PopOut> Order = new();
    private static PopOut? _current;       // the pop-out whose frame is being built
    private static PopOut? _keyboard;      // the pop-out typing into a text box
    private static int _loggedErrors;

    /// <summary>True while a pop-out's ImGui frame is being built (inside a face's Draw). AC thread.</summary>
    public static bool InPopOutFrame => _current != null;

    private static int _mainFrame;

    /// <summary>
    /// The main context's ImGui frame number, the same inside a pop-out's frame (whose own
    /// context counts its frames from 0): for frame-numbered state shared across contexts
    /// (a script window's local values and its replay budget, ScriptWindowReplay). AC thread.
    /// </summary>
    public static int FrameNumber =>
        _current != null || ImGuiNET.ImGui.GetCurrentContext() == IntPtr.Zero ? _mainFrame : ImGuiNET.ImGui.GetFrameCount();

    /// <summary>
    /// Records the main context's frame number for <see cref="FrameNumber"/>. Call right after
    /// the main context's NewFrame, with it current (EngineFrameController.BuildImGuiFrame):
    /// the main context is not current when the pop-outs render. AC thread.
    /// </summary>
    public static void NoteMainFrame() => _mainFrame = ImGuiNET.ImGui.GetFrameCount();

    /// <summary>
    /// Where a popped-out face puts its window (its top-left in the pop-out's ImGui
    /// coordinates): the panel's place on the work area. (0,0) outside a pop-out frame.
    /// AC thread, in the draw.
    /// </summary>
    public static Vector2 PanelOrigin => _current is { } pop ? new Vector2(pop.OriginX, pop.OriginY) : Vector2.Zero;

    public static bool IsPopped(string title) => Pops.ContainsKey(title);

    /// <summary>
    /// A pop-out's text box holds the keyboard: the game WndProc routes keys to it
    /// (RouteInput) and lets none of them reach AC. AC thread.
    /// </summary>
    public static bool HasTextFocus => _keyboard is { WantTextInput: true };

    /// <summary>The pop-out typing into a text box, or null (for the input log). AC thread.</summary>
    public static string? TextFocusTitle => _keyboard is { WantTextInput: true } k ? k.Title : null;

    /// <summary>
    /// True when the cursor is over <paramref name="title"/>'s pop-out window right now
    /// (UiDropTargets: an AC item released over a popped-out panel). AC thread.
    /// </summary>
    public static bool IsCursorOver(string title)
    {
        if (!Pops.TryGetValue(title, out PopOut? pop) || pop.Window == null || pop.Window.Hwnd == IntPtr.Zero)
            return false;
        return GetCursorPos(out POINT cursor) && WindowFromPoint(cursor) == pop.Window.Hwnd;
    }

    /// <summary>The screen position of the game window's client origin (to place a pop-out where the docked panel was).</summary>
    public static Vector2 ClientOriginOnScreen()
    {
        var p = new POINT();
        IntPtr game = Win32Backend.GameHwnd;
        if (game != IntPtr.Zero) ClientToScreen(game, ref p);
        return new Vector2(p.X, p.Y);
    }

    /// <summary>
    /// Pops a panel out at the given screen geometry. Call with the main context
    /// current (its font atlas is shared). AC thread.
    /// </summary>
    public static void Open(string title, int left, int top, int width, int height, int captionHeight, int captionRightInset, float uiScale,
        Action<float, Vector2>? draw = null, Action<int, int>? onMoved = null, bool sizeToContent = false, bool resizable = true)
    {
        if (Pops.ContainsKey(title)) return;
        IntPtr main = ImGuiNET.ImGui.GetCurrentContext();
        if (main == IntPtr.Zero) return;

        IntPtr ctx = ImGuiNET.ImGui.CreateContext(ImGuiNET.ImGui.GetIO().Fonts);
        ImGuiNET.ImGui.SetCurrentContext(ctx);
        try
        {
            ImGuiIOPtr io = ImGuiNET.ImGui.GetIO();
            io.NativePtr->IniFilename = null;
            io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;
            RynthTheme.Apply(ImGuiNET.ImGui.GetStyle(), uiScale);
        }
        finally
        {
            ImGuiNET.ImGui.SetCurrentContext(main);
        }

        var pop = new PopOut
        {
            Title = title, Context = ctx,
            Left = left, Top = top, Width = Math.Max(80, width), Height = Math.Max(40, height),
            CaptionHeight = captionHeight, CaptionRightInset = captionRightInset,
            Draw = draw, OnMoved = onMoved, SizeToContent = sizeToContent, ResizeGrip = resizable && !sizeToContent,
        };
        Pops[title] = pop;
        Order.Add(pop);
        // Creating an HWND from inside EndScene isn't safe: the game WndProc does it.
        if (!Win32Backend.PostToGameThread(() => CreateWindow(pop)))
            RynthLog.UI($"ImGuiPopOuts: {title}: no game window to create the pop-out from.");
    }

    private static void CreateWindow(PopOut pop)
    {
        if (!Pops.TryGetValue(pop.Title, out PopOut? live) || live != pop || pop.Window != null) return;
        try
        {
            // A saved spot off the screen (a monitor since unplugged, a bad save) comes back on it.
            if (LayeredWindow.ClampToWorkArea(ref pop.Left, ref pop.Top, pop.Width, pop.Height))
                RynthLog.UI($"ImGuiPopOuts: {pop.Title}: saved spot was off the screen; moved to ({pop.Left},{pop.Top}).");
            var window = new LayeredWindow(pop.Width, pop.Height, pop.Left, pop.Top, Win32Backend.GameHwnd)
            {
                CaptionHeight = pop.CaptionHeight,
                CaptionRightInset = pop.CaptionRightInset,
                ResizeGripEnabled = pop.ResizeGrip,
            };
            window.OnInput = (msg, wParam, lParam, _, _) => OnWindowInput(pop, msg, wParam, lParam);
            window.OnResized = (_, _) => pop.HotFrames = Math.Max(pop.HotFrames, 2);
            window.OnMoved = (x, y) => NotifyMoved(pop, window, x, y);
            window.OnResizeEnd = (w, h) => ImGuiPanelHost.OnPopOutGeometry(pop.Title, window.ScreenLeft, window.ScreenTop, w, h);
            LayeredWindow.ClaimTitle(pop.Title, window.Hwnd);
            pop.Window = window;
            pop.HotFrames = HotFramesAfterInput;
            RynthLog.UI($"ImGuiPopOuts: {pop.Title} popped out at ({pop.Left},{pop.Top}) {pop.Width}x{pop.Height}.");
        }
        catch (Exception ex)
        {
            RynthLog.UI($"ImGuiPopOuts: {pop.Title}: window creation failed ({ex.GetType().Name}: {ex.Message}).");
        }
    }

    /// <summary>The window moved (a drag ended, or it was pulled back onto the screen): save the spot. AC thread.</summary>
    private static void NotifyMoved(PopOut pop, LayeredWindow window, int x, int y)
    {
        if (pop.OnMoved != null) pop.OnMoved(x, y);
        else ImGuiPanelHost.OnPopOutGeometry(pop.Title, x, y, window.Width, window.Height);
    }

    /// <summary>Closes a pop-out: its window, surfaces and context. AC thread.</summary>
    public static void Close(string title)
    {
        if (!Pops.Remove(title, out PopOut? pop)) return;
        Order.Remove(pop);
        if (_keyboard == pop) _keyboard = null;
        ImGuiTextFocus.ForgetPopOut(title);
        try { pop.Window?.Dispose(); } catch (Exception ex) { RynthLog.UI($"ImGuiPopOuts: {title} window dispose threw {ex.Message}"); }
        pop.Window = null;
        pop.Surface.Dispose();
        ImGuiFonts.WaitForWorkers();   // no context goes while a font worker runs (ImGuiFonts, re-bake note)
        ImGuiNET.ImGui.DestroyContext(pop.Context);   // restores the current context
        RynthLog.UI($"ImGuiPopOuts: {title} closed.");
    }

    /// <summary>
    /// The main context's font atlas was replaced (ImGuiFonts re-bake): every pop-out
    /// shares it, so each one's context is pointed at the new atlas too. AC thread,
    /// between frames.
    /// </summary>
    public static void ReplaceFontAtlas(IntPtr oldAtlas, IntPtr newAtlas)
    {
        if (Order.Count == 0) return;
        IntPtr previous = ImGuiNET.ImGui.GetCurrentContext();
        try
        {
            foreach (PopOut pop in Order)
            {
                ImGuiNET.ImGui.SetCurrentContext(pop.Context);
                ImGuiIOPtr io = ImGuiNET.ImGui.GetIO();
                if ((IntPtr)io.NativePtr->Fonts == oldAtlas)
                    io.NativePtr->Fonts = (ImFontAtlas*)newAtlas;
                pop.LastHash = 0;   // redraw with the new glyphs even if the lists hash the same
            }
        }
        finally
        {
            ImGuiNET.ImGui.SetCurrentContext(previous);
        }
    }

    /// <summary>Engine shutdown / reload: every pop-out goes. Any thread that may touch the device.</summary>
    /// <summary>
    /// The device is about to be Reset (D3D9.DeviceResetHook): release every
    /// pop-out's D3DPOOL_DEFAULT surfaces, or the Reset fails and AC stops with
    /// "Could not initialize Direct3D". They are recreated on the next frame;
    /// the windows keep their last picture meanwhile. AC thread. Returns the
    /// number of pop-outs released.
    /// </summary>
    public static int ReleaseDeviceResources()
    {
        int released = 0;
        foreach (PopOut pop in Order)
        {
            pop.Surface.Dispose();
            pop.LastHash = 0;   // draw the next frame even if nothing changed
            released++;
        }
        return released;
    }

    public static void Shutdown()
    {
        var titles = new List<string>(Pops.Keys);
        foreach (string title in titles) Close(title);
    }

    /// <summary>
    /// A size-to-content pop-out's draw reports how big its content came out;
    /// the window takes that size on the next frame. AC thread, in the draw.
    /// </summary>
    public static void ReportContentSize(Vector2 size)
    {
        PopOut? pop = _current;
        if (pop == null || !pop.SizeToContent || size == pop.ContentSize) return;
        pop.ContentSize = size;
        pop.HotFrames = Math.Max(pop.HotFrames, 2);
    }

    /// <summary>Starts moving the current pop-out's window (a chromeless face dragged by its own surface). AC thread, in a face's Draw.</summary>
    public static void BeginMoveCurrent() => _current?.Window?.BeginContentMove();

    // ── Input ──────────────────────────────────────────────────────────

    private static void OnWindowInput(PopOut pop, uint msg, IntPtr wParam, IntPtr lParam)
    {
        pop.Input.Add(new InputMessage(msg, wParam, lParam));
        pop.HotFrames = HotFramesAfterInput;
        if (msg is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN)
        {
            // A click in this pop-out ends text entry everywhere else.
            EngineFrameController.DropMainTextFocus();
            foreach (PopOut other in Order)
                if (other != pop && other.WantTextInput) { other.DropTextFocus = true; other.HotFrames = 2; }
        }
    }

    /// <summary>
    /// Called by the game WndProc for every message. True when a pop-out took it:
    /// keys while a pop-out's text box is being edited, and the wheel over a
    /// pop-out. A click on the game ends a pop-out's text edit. AC thread.
    /// </summary>
    public static bool RouteInput(uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (Order.Count == 0) return false;
        switch (msg)
        {
            case WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN:
                foreach (PopOut pop in Order)
                    if (pop.WantTextInput) { pop.DropTextFocus = true; pop.HotFrames = 2; }
                return false;

            case WM_MOUSEWHEEL or WM_MOUSEHWHEEL:
            {
                var p = new POINT { X = (short)((long)lParam & 0xFFFF), Y = (short)(((long)lParam >> 16) & 0xFFFF) };
                IntPtr under = WindowFromPoint(p);
                foreach (PopOut pop in Order)
                {
                    if (pop.Window == null || pop.Window.Hwnd != under) continue;
                    OnWindowInput(pop, msg, wParam, lParam);
                    return true;
                }
                return false;
            }

            case WM_KEYDOWN or WM_KEYUP or WM_CHAR or WM_SYSKEYDOWN or WM_SYSKEYUP:
            {
                PopOut? sink = _keyboard;
                if (sink == null || !sink.WantTextInput) return false;
                sink.Input.Add(new InputMessage(msg, wParam, lParam));
                sink.HotFrames = HotFramesAfterInput;
                return true;
            }
        }
        return false;
    }

    /// <param name="offsetX">Added to a message's client x to make it ImGui's (the window's origin in ImGui coordinates).</param>
    private static void FeedInput(PopOut pop, ImGuiIOPtr io, int offsetX, int offsetY)
    {
        foreach (InputMessage m in pop.Input)
        {
            // Mouse messages from the window carry the position (client coords). It has to
            // reach ImGui before the button: a press is applied where ImGui last saw the
            // mouse, and an idle pop-out may last have seen it outside the window, so a
            // quick grab of a drag surface pressed nothing (Tom, 2026-09-29: the popped-out
            // dashboard wouldn't move). Wheel lParams are screen coordinates: skipped.
            if (m.Msg is WM_MOUSEMOVE or WM_LBUTTONDOWN or WM_LBUTTONUP or WM_RBUTTONDOWN or WM_RBUTTONUP or WM_MBUTTONDOWN or WM_MBUTTONUP)
                io.AddMousePosEvent((short)((long)m.LParam & 0xFFFF) + offsetX, (short)(((long)m.LParam >> 16) & 0xFFFF) + offsetY);
            switch (m.Msg)
            {
                case WM_LBUTTONDOWN: SetButton(pop, io, 0, true); break;
                case WM_LBUTTONUP: SetButton(pop, io, 0, false); break;
                case WM_RBUTTONDOWN: SetButton(pop, io, 1, true); break;
                case WM_RBUTTONUP: SetButton(pop, io, 1, false); break;
                case WM_MBUTTONDOWN: SetButton(pop, io, 2, true); break;
                case WM_MBUTTONUP: SetButton(pop, io, 2, false); break;
                case WM_MOUSEWHEEL: io.AddMouseWheelEvent(0f, (short)((long)m.WParam >> 16) / 120f); break;
                case WM_MOUSEHWHEEL: io.AddMouseWheelEvent((short)((long)m.WParam >> 16) / 120f, 0f); break;
                case WM_KEYDOWN or WM_SYSKEYDOWN or WM_KEYUP or WM_SYSKEYUP:
                {
                    ImGuiKey key = Win32Backend.VkToImGuiKey((int)(long)m.WParam);
                    if (key != ImGuiKey.None) io.AddKeyEvent(key, m.Msg is WM_KEYDOWN or WM_SYSKEYDOWN);
                    break;
                }
                case WM_CHAR:
                {
                    uint ch = (uint)(long)m.WParam;
                    if (ch > 0 && ch < 0x10000) io.AddInputCharacter(ch);
                    break;
                }
            }
        }
        pop.Input.Clear();
    }

    private static void SetButton(PopOut pop, ImGuiIOPtr io, int button, bool down)
    {
        if (pop.Buttons[button] == down) return;
        pop.Buttons[button] = down;
        io.AddMouseButtonEvent(button, down);
    }

    // ── Frames ─────────────────────────────────────────────────────────

    /// <summary>
    /// Draws and presents every pop-out that is due. <paramref name="live"/> is
    /// false between characters: pop-outs hide, like the in-client panels.
    /// AC thread, inside EndScene, after the main frame.
    /// </summary>
    public static void RenderAll(IntPtr device, float uiScale, bool live)
    {
        if (Order.Count == 0) return;
        if (!live)
        {
            foreach (PopOut pop in Order)
                if (pop.Visible) { pop.Window?.Hide(); pop.Visible = false; }
            return;
        }

        // No ImGui call before a pop-out's own context is current: the main context is NOT current
        // here (EngineFrameController restores the previous one, normally none, after each frame), and
        // igGetFrameCount with no context is an access violation that ends the client (11-10). The main
        // frame number comes from NoteMainFrame instead.
        IntPtr previous = ImGuiNET.ImGui.GetCurrentContext();
        long now = Stopwatch.GetTimestamp();
        try
        {
            for (int i = 0; i < Order.Count; i++)
            {
                PopOut pop = Order[i];
                if (pop.Window == null || pop.Window.Hwnd == IntPtr.Zero) continue;
                long interval = pop.HotFrames > 0 || pop.Hovered || pop.WantTextInput ? 0 : pop.IdleInterval;
                if (pop.HotFrames <= 0 && now - pop.LastFrameTicks < Math.Max(interval, FrameInterval)) continue;
                // UiCost: the window's own frame, render and present; its panel is timed by the host.
                long cPop = UiCost.Start(), costMark = UiCost.Mark;
                RenderOne(device, pop, uiScale, now);
                UiCost.StopPopOutWindow(pop.Title, cPop, costMark);
            }
        }
        finally
        {
            _current = null;
            ImGuiNET.ImGui.SetCurrentContext(previous);
        }
    }

    private static void RenderOne(IntPtr device, PopOut pop, float uiScale, long now)
    {
        LayeredWindow window = pop.Window!;
        int w = Math.Max(1, window.Width), h = Math.Max(1, window.Height);
        if (pop.SizeToContent && pop.ContentSize.X >= 1 && pop.ContentSize.Y >= 1)
        {
            // The window follows the content (Present resizes it to the picture).
            w = Math.Min(MaxContentSide, (int)MathF.Ceiling(pop.ContentSize.X));
            h = Math.Min(MaxContentSide, (int)MathF.Ceiling(pop.ContentSize.Y));
        }
        PlaceOnDisplay(pop, window, w, h);
        // The screen spot of ImGui's (0,0).
        int anchorX = window.ScreenLeft - pop.OriginX, anchorY = window.ScreenTop - pop.OriginY;
        ImGuiNET.ImGui.SetCurrentContext(pop.Context);
        ImGuiIOPtr io = ImGuiNET.ImGui.GetIO();
        bool started = false;
        try
        {
            // The display is the work area, not the window: a size-to-content face must
            // lay out against a large display (ImGui clamps an auto-resizing window to the
            // display; when the display was the window, each shrank the other, 400x40 ->
            // 8x6 -> 4x4, and the popped-out bar was a 4 px speck), and tooltips and
            // popups get room around the panel.
            io.DisplaySize = new Vector2(pop.DisplayW, pop.DisplayH);
            float dt = pop.LastFrameTicks == 0 ? 1f / 30f : (float)(now - pop.LastFrameTicks) / Stopwatch.Frequency;
            io.DeltaTime = Math.Clamp(dt, 1f / 240f, 0.5f);
            pop.LastFrameTicks = now;

            FeedInput(pop, io, window.SurfaceLeft - anchorX, window.SurfaceTop - anchorY);
            // The cursor, from the screen: over this window (or dragging from it) it's in
            // ImGui's coordinates, otherwise nowhere, so hover highlights clear.
            GetCursorPos(out POINT cursor);
            bool held = pop.Buttons[0] || pop.Buttons[1] || pop.Buttons[2];
            pop.Hovered = WindowFromPoint(cursor) == window.Hwnd;
            if (held || pop.Hovered)
                io.AddMousePosEvent(cursor.X - anchorX, cursor.Y - anchorY);
            else
                io.AddMousePosEvent(-float.MaxValue, -float.MaxValue);
            bool gameFocused = GetForegroundWindow() == Win32Backend.GameHwnd;
            io.AddFocusEvent(gameFocused || held);
            io.AddKeyEvent(ImGuiKey.ModCtrl, (GetKeyState(VK_CONTROL) & 0x8000) != 0);
            io.AddKeyEvent(ImGuiKey.ModShift, (GetKeyState(VK_SHIFT) & 0x8000) != 0);
            io.AddKeyEvent(ImGuiKey.ModAlt, (GetKeyState(VK_MENU) & 0x8000) != 0);

            _current = pop;
            ImGuiTextFocus.BeginFrame();
            RynthTheme.RefreshStyle(ref pop.ThemeApplied);   // this context's base style follows a theme change
            ImGuiNET.ImGui.NewFrame();
            started = true;
            if (pop.DropTextFocus)
            {
                pop.DropTextFocus = false;
                ImGuiNET.ImGui.SetWindowFocus(null);
            }
            if (pop.Draw != null) pop.Draw(uiScale, new Vector2(w, h));
            else ImGuiPanelHost.DrawPopped(pop.Title, uiScale, new Vector2(w, h));
            ImGuiNET.ImGui.EndFrame();
            started = false;
            ImGuiNET.ImGui.Render();
            _current = null;
            long built = Stopwatch.GetTimestamp();
            pop.Built++;
            pop.BuildTicks += built - now;

            // The picture: the panel, plus a margin for anything drawn past it (a tooltip,
            // a menu). Only a menu there takes clicks; a tooltip lets them through.
            ImDrawDataPtr drawData = ImGuiNET.ImGui.GetDrawData();
            UpdateMargins(pop, drawData.NativePtr, w, h);
            window.MarginsClickThrough = !ImGuiNET.ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel)
                && !WindowPastPanel(drawData.NativePtr, pop.OriginX, pop.OriginY, pop.OriginX + w, pop.OriginY + h, pop.DisplayW, pop.DisplayH);
            int sx = pop.OriginX - pop.MarginL, sy = pop.OriginY - pop.MarginT;
            int sw = w + pop.MarginL + pop.MarginR, sh = h + pop.MarginT + pop.MarginB;
            if (drawData.NativePtr != null)
            {
                drawData.NativePtr->DisplayPos = new Vector2(sx, sy);   // render into the sw x sh picture
                drawData.NativePtr->DisplaySize = new Vector2(sw, sh);
            }

            // Nothing changed since the last picture: skip the render, readback and
            // window update (most panels are still most of the time).
            ulong hash = HashDrawData(drawData, sw, sh) ^ Mix(sx, sy, w, h);
            if (hash == pop.LastHash && pop.Visible && pop.Surface.SnapshotPath == null)
            {
                pop.Surface.Flush(device, window);
            }
            else
            {
                pop.LastHash = hash;
                if (pop.Surface.Render(device, drawData, sw, sh, new PopOutSurface.Placement(pop.MarginL, pop.MarginT, w, h), window))
                {
                    // Shown, or its picture changed size (the bar grows to its content):
                    // none of it may hang off the screen.
                    if ((!pop.Visible || w != pop.ShownWidth || h != pop.ShownHeight) && window.EnsureOnScreen())
                        NotifyMoved(pop, window, window.ScreenLeft, window.ScreenTop);
                    pop.ShownWidth = w;
                    pop.ShownHeight = h;
                    if (!pop.Visible)
                    {
                        window.Show();
                        pop.Visible = true;
                    }
                }
                pop.Pushed++;
                pop.PushTicks += Stopwatch.GetTimestamp() - built;
            }

            // io.WantTextInput lags a frame behind the box (ImGui publishes it from the
            // previous frame's widgets); a box that went active this frame counts too, so
            // the keys typed right after the click already come here, not to AC.
            pop.WantTextInput = ImGuiTextFocus.EndPopOutFrame(pop.Title, io.WantTextInput);
            pop.IdleInterval = Stopwatch.Frequency / ImGuiPanelHost.PopOutIdleHz(pop.Title);
            if (pop.WantTextInput) _keyboard = pop;
            else if (_keyboard == pop) _keyboard = null;
            if (pop.HotFrames > 0) pop.HotFrames--;
        }
        catch (Exception ex)
        {
            try { if (started) ImGuiNET.ImGui.EndFrame(); } catch { }
            _current = null;
            pop.HotFrames = 0;
            if (_loggedErrors++ < 10)
                RynthLog.UI($"ImGuiPopOuts: {pop.Title} frame threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ── Tooltips and popups: the display and the margins ───────────────

    /// <summary>
    /// Sets the context's display to the work area of the panel's monitor (and the
    /// panel, where it hangs off it), and the panel's place on it. ImGui then keeps
    /// tooltips and popups on the screen and flips them at its edges, as in the client.
    /// </summary>
    private static void PlaceOnDisplay(PopOut pop, LayeredWindow window, int w, int h)
    {
        if (window.IsDragging && pop.DisplayW > 0)
        {
            // Mid-drag the display travels with the panel: the panel keeps its ImGui
            // position, so a moved picture is the same picture (not redrawn every step).
            // It is placed afresh once the drag ends.
            pop.DisplayW = Math.Max(pop.DisplayW, pop.OriginX + w);
            pop.DisplayH = Math.Max(pop.DisplayH, pop.OriginY + h);
            return;
        }
        int left = window.ScreenLeft, top = window.ScreenTop;
        int l = left, t = top, r = left + w, b = top + h;
        if (LayeredWindow.TryGetWorkArea(left, top, w, h, out int wl, out int wt, out int wr, out int wb))
        {
            l = Math.Min(l, wl); t = Math.Min(t, wt);
            r = Math.Max(r, wr); b = Math.Max(b, wb);
        }
        pop.OriginX = left - l;
        pop.OriginY = top - t;
        pop.DisplayW = r - l;
        pop.DisplayH = b - t;
    }

    /// <summary>
    /// The picture's margins: room for everything drawn past the panel, in steps, held
    /// while something is out there (a tooltip following the mouse doesn't resize the
    /// window on every frame) and dropped to 0 when nothing is. Never past the display.
    /// </summary>
    private static void UpdateMargins(PopOut pop, ImDrawData* dd, int w, int h)
    {
        float px0 = pop.OriginX, py0 = pop.OriginY, px1 = px0 + w, py1 = py0 + h;
        if (!OverflowBounds(dd, px0, py0, px1, py1, pop.DisplayW, pop.DisplayH, out float x0, out float y0, out float x1, out float y1))
        {
            pop.MarginL = pop.MarginT = pop.MarginR = pop.MarginB = 0;
            return;
        }
        pop.MarginL = Math.Clamp(Math.Max(Step(px0 - x0), pop.MarginL), 0, pop.OriginX);
        pop.MarginT = Math.Clamp(Math.Max(Step(py0 - y0), pop.MarginT), 0, pop.OriginY);
        pop.MarginR = Math.Clamp(Math.Max(Step(x1 - px1), pop.MarginR), 0, Math.Max(0, pop.DisplayW - pop.OriginX - w));
        pop.MarginB = Math.Clamp(Math.Max(Step(y1 - py1), pop.MarginB), 0, Math.Max(0, pop.DisplayH - pop.OriginY - h));
    }

    private static int Step(float overflow)
        => overflow <= OverflowSlack ? 0 : Math.Min(MaxMargin, ((int)MathF.Ceiling(overflow) + MarginStep - 1) / MarginStep * MarginStep);

    /// <summary>
    /// The bounds of what is drawn outside the panel rect (tooltips, popups), as it will
    /// show: each draw command's triangles, cut to its clip rect and the display. False
    /// when nothing reaches more than <see cref="OverflowSlack"/> px past the panel.
    /// Only commands whose clip rect reaches past the panel are walked (a window's
    /// background, tooltips and popups); the panel's own content is clipped inside it.
    /// </summary>
    private static bool OverflowBounds(ImDrawData* dd, float px0, float py0, float px1, float py1, float displayW, float displayH,
        out float x0, out float y0, out float x1, out float y1)
    {
        x0 = px0; y0 = py0; x1 = px1; y1 = py1;
        if (dd == null) return false;
        ImDrawList** lists = (ImDrawList**)dd->CmdLists.Data;
        for (int n = 0; n < dd->CmdListsCount; n++)
            ListOverflow(lists[n], px0, py0, px1, py1, displayW, displayH, ref x0, ref y0, ref x1, ref y1);
        return px0 - x0 > OverflowSlack || py0 - y0 > OverflowSlack || x1 - px1 > OverflowSlack || y1 - py1 > OverflowSlack;
    }

    /// <summary>
    /// A window other than a tooltip draws past the panel: a face's own window (a dashboard's
    /// drawers). Popups count too, but IsPopupOpen already says so. ImGui names a tooltip
    /// window "##Tooltip_NN" and its draw list after its window.
    /// </summary>
    private static bool WindowPastPanel(ImDrawData* dd, float px0, float py0, float px1, float py1, float displayW, float displayH)
    {
        if (dd == null) return false;
        ImDrawList** lists = (ImDrawList**)dd->CmdLists.Data;
        for (int n = 0; n < dd->CmdListsCount; n++)
        {
            ImDrawList* list = lists[n];
            if (list == null || IsTooltipList(list)) continue;
            float x0 = px0, y0 = py0, x1 = px1, y1 = py1;
            ListOverflow(list, px0, py0, px1, py1, displayW, displayH, ref x0, ref y0, ref x1, ref y1);
            if (px0 - x0 > OverflowSlack || py0 - y0 > OverflowSlack || x1 - px1 > OverflowSlack || y1 - py1 > OverflowSlack)
                return true;
        }
        return false;
    }

    private static bool IsTooltipList(ImDrawList* list)
    {
        byte* name = list->_OwnerName;
        if (name == null) return false;
        ReadOnlySpan<byte> prefix = "##Tooltip"u8;
        for (int i = 0; i < prefix.Length; i++)
            if (name[i] != prefix[i]) return false;
        return true;
    }

    /// <summary>Widens the bounds by what one draw list shows outside the panel (see <see cref="OverflowBounds"/>).</summary>
    private static void ListOverflow(ImDrawList* list, float px0, float py0, float px1, float py1, float displayW, float displayH,
        ref float x0, ref float y0, ref float x1, ref float y1)
    {
        if (list == null) return;
        int vtxCount = list->VtxBuffer.Size, idxCount = list->IdxBuffer.Size;
        ImDrawVert* vtx = (ImDrawVert*)list->VtxBuffer.Data;
        ushort* idx = (ushort*)list->IdxBuffer.Data;
        ImDrawCmd* cmds = (ImDrawCmd*)list->CmdBuffer.Data;
        for (int c = 0; c < list->CmdBuffer.Size; c++)
        {
            ImDrawCmd* cmd = &cmds[c];
            if (cmd->UserCallback != IntPtr.Zero || cmd->ElemCount == 0) continue;
            float cx0 = Math.Max(cmd->ClipRect.X, 0f), cy0 = Math.Max(cmd->ClipRect.Y, 0f);
            float cx1 = Math.Min(cmd->ClipRect.Z, displayW), cy1 = Math.Min(cmd->ClipRect.W, displayH);
            if (cx1 <= cx0 || cy1 <= cy0) continue;
            if (cx0 >= px0 && cy0 >= py0 && cx1 <= px1 && cy1 <= py1) continue;   // can't draw outside the panel

            float vx0 = float.MaxValue, vy0 = float.MaxValue, vx1 = float.MinValue, vy1 = float.MinValue;
            long start = cmd->IdxOffset, end = Math.Min((long)idxCount, start + cmd->ElemCount);
            long vbase = cmd->VtxOffset;
            for (long i = start; i < end; i++)
            {
                long v = vbase + idx[i];
                if (v >= vtxCount) continue;
                Vector2 pos = vtx[v].pos;
                if (pos.X < vx0) vx0 = pos.X;
                if (pos.Y < vy0) vy0 = pos.Y;
                if (pos.X > vx1) vx1 = pos.X;
                if (pos.Y > vy1) vy1 = pos.Y;
            }
            vx0 = Math.Max(vx0, cx0); vy0 = Math.Max(vy0, cy0);
            vx1 = Math.Min(vx1, cx1); vy1 = Math.Min(vy1, cy1);
            if (vx1 <= vx0 || vy1 <= vy0) continue;
            x0 = Math.Min(x0, vx0); y0 = Math.Min(y0, vy0);
            x1 = Math.Max(x1, vx1); y1 = Math.Max(y1, vy1);
        }
    }

    /// <summary>Folds the picture's placement into the draw-data hash.</summary>
    private static ulong Mix(int a, int b, int c, int d)
        => ((ulong)(uint)a * 0x9E3779B97F4A7C15UL) ^ ((ulong)(uint)b * 0xC2B2AE3D27D4EB4FUL)
         ^ ((ulong)(uint)c * 0x165667B19E3779F9UL) ^ ((ulong)(uint)d * 0x27D4EB2F165667C5UL);

    /// <summary>Diagnostics: the pop-out's next frame is also saved as a BMP in the log folder. AC thread.</summary>
    public static string? RequestSnapshot(string title)
    {
        if (!Pops.TryGetValue(title, out PopOut? pop)) return null;
        // A script window's key ("script:RynthLua/loot-inbox/Loot") isn't a file name as it is.
        var name = new System.Text.StringBuilder(title.Length);
        foreach (char c in title) name.Append(c == ' ' || Array.IndexOf(System.IO.Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
        string path = System.IO.Path.Combine(LogPaths.LogDirectory, $"popout_{name}.bmp");
        pop.Surface.SnapshotPath = path;
        pop.HotFrames = Math.Max(pop.HotFrames, 2);
        return path;
    }

    /// <summary>One line per pop-out for /rc imgui diag.</summary>
    public static List<string> Describe()
    {
        var lines = new List<string>();
        double ms = 1000.0 / Stopwatch.Frequency;
        foreach (PopOut pop in Order)
            lines.Add($"popout {pop.Title}: window={(pop.Window == null ? "pending" : $"0x{pop.Window.Hwnd.ToInt64():X} at ({pop.Window.ScreenLeft},{pop.Window.ScreenTop}) {pop.Window.Width}x{pop.Window.Height}")} visible={pop.Visible} text={pop.WantTextInput}"
                + $" margins=({pop.MarginL},{pop.MarginT},{pop.MarginR},{pop.MarginB}) display={pop.DisplayW}x{pop.DisplayH}"
                + $" built={pop.Built} ({(pop.Built > 0 ? pop.BuildTicks * ms / pop.Built : 0):0.00} ms) pushed={pop.Pushed} ({(pop.Pushed > 0 ? pop.PushTicks * ms / pop.Pushed : 0):0.00} ms)");
        return lines;
    }

    /// <summary>A 64-bit hash of everything that affects the picture: vertices, indices, commands and the size.</summary>
    private static ulong HashDrawData(ImDrawDataPtr drawData, int w, int h)
    {
        ImDrawData* dd = drawData.NativePtr;
        ulong hash = 0xcbf29ce484222325UL ^ (ulong)(uint)w ^ ((ulong)(uint)h << 32);
        if (dd == null) return hash;
        ImDrawList** lists = (ImDrawList**)dd->CmdLists.Data;
        for (int n = 0; n < dd->CmdListsCount; n++)
        {
            ImDrawList* list = lists[n];
            if (list == null) continue;
            hash = HashBytes(hash, (byte*)list->VtxBuffer.Data, list->VtxBuffer.Size * sizeof(ImDrawVert));
            hash = HashBytes(hash, (byte*)list->IdxBuffer.Data, list->IdxBuffer.Size * sizeof(ushort));
            hash = HashBytes(hash, (byte*)list->CmdBuffer.Data, list->CmdBuffer.Size * sizeof(ImDrawCmd));
        }
        return hash;
    }

    private static ulong HashBytes(ulong hash, byte* data, int length)
    {
        if (data == null || length <= 0) return hash;
        ulong* p = (ulong*)data;
        int words = length >> 3;
        for (int i = 0; i < words; i++)
            hash = (hash ^ p[i]) * 0x100000001B3UL + (hash >> 29);
        for (int i = words << 3; i < length; i++)
            hash = (hash ^ data[i]) * 0x100000001B3UL;
        return hash ^ (ulong)length;
    }
}
