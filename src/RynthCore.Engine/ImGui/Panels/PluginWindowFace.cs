// ============================================================================
//  RynthCore.Engine - ImGui/Panels/PluginWindowFace.cs
//  A plugin's own window (its manifest's "windows", UI/Sections/PluginWindows.cs):
//  the panel host draws the window, this face hands the body to the plugin's
//  RynthPluginRenderEmbed(surface, width, height) in the default UI font, under
//  an ID scope of its own. Popped out, PluginManager.RenderEmbed adds ":popout"
//  to the surface name. An export that throws (or returns -2, "failed") is
//  switched off (PluginManager logs it); the body then says so and asks again
//  every 5 s, so a reloaded plugin draws again without the window being reopened.
//  After every call ImGui's stacks are checked against what they were before it
//  (ImGuiStackGuard): whatever the plugin left open (colours, style vars, fonts,
//  IDs, groups, child windows, tables...) is ended and popped there, so the
//  panels drawn after it start clean; logged once per window, then counted.
//  A "fitHeight" window (v90) takes the height the plugin's draw asks for
//  (wantHeight) as its minimum and is fitted to it whenever it changes and the
//  mouse is up (a frameless window's empty rows would still catch clicks meant
//  for the game); a text size change scales its width along with its fonts.
//  A "scale": "draw" window set bigger than normal (PanelDrawScale) gets the
//  fonts baked nearest above its size while the plugin draws (2026-11-01: from
//  the normal bake, stretched, its text blurred); the font pushed for its menus
//  and tooltips stays at the panel's text size.
//  docs/PLUGIN_WINDOWS.md. AC's render thread.
// ============================================================================

using System;
using System.Numerics;
using System.Text;
using RynthCore.Engine.Plugins;
using RynthCore.Engine.UI;
using static RynthCore.Engine.ImGuiBackend.Panels.FaceKit;

namespace RynthCore.Engine.ImGuiBackend.Panels;

internal sealed class PluginWindowFace : IImGuiPanel
{
    private readonly string _plugin, _title;
    private readonly byte[] _surface;   // NUL-terminated UTF-8, built once
    private readonly string _idScope;
    // The export threw: PluginManager switched the plugin's surfaces off. Asked again every
    // RetryMs, so a reloaded plugin (a fresh export) draws again without reopening the window.
    private long _failedAtMs = -1;
    private const long RetryMs = 5000;

    // "fitHeight" (v90): the content height the plugin asked for (logical units; 0 = not known
    // yet), and the text size factor and width (logical) the window was last fitted at.
    private readonly bool _fitHeight, _drawScale;
    private readonly float _minWidth;
    private float _contentHeight, _fitK, _fitWidth;

    public PluginWindowFace(string plugin, string surface, string title, bool fitHeight = false, float minWidth = 0f,
        bool drawScale = false)
    {
        _plugin = plugin;
        _title = title;
        _surface = Encoding.UTF8.GetBytes(surface + "\0");
        _idScope = "##pw_" + surface;
        _fitHeight = fitHeight;
        _minWidth = minWidth;
        _drawScale = drawScale;
    }

    /// <summary>The font step the plugin draws with: a draw-scale window's own size's (baked), else the text size's.</summary>
    private int DrawFontStep(int textStep)
    {
        if (!_drawScale) return textStep;
        int pct = PanelDrawScale.Get(_title);
        return pct > 0 ? ImGuiFonts.BakedStepNear(PanelDrawScale.FontStep(pct, EngineFrameController.FontScale)) : textStep;
    }

    /// <summary>A fitHeight window's minimum: its minimum width at its text size, and the content's height.</summary>
    public Vector2? MinSize => _fitHeight && _contentHeight > 0f
        ? new Vector2(_minWidth * TextFactor(), _contentHeight)
        : null;

    /// <summary>The panel's text size factor at the size its fonts are baked for.</summary>
    private float TextFactor() => PanelTextScale.Factor(ImGuiFonts.BakedStepNear(PanelTextScale.Get(_title)));

    public void Draw()
    {
        Vector2 start = ImGuiNET.ImGui.GetCursorScreenPos();
        Vector2 avail = ImGuiNET.ImGui.GetContentRegionAvail();
        long now = Environment.TickCount64;
        int result = PluginManager.EmbedFailed;
        Vector2 want = Vector2.Zero;
        if (_failedAtMs < 0 || now - _failedAtMs >= RetryMs)
        {
            // Two stack records: the body's base (where a recovery goes back to: the panel host
            // calls Draw right after its BeginChild, the ID stack at its base) and the depth the
            // plugin gets, which it must leave as it found.
            bool guarded = ImGuiStackGuard.Available;
            ImGuiStackGuard.Mark bodyBase = guarded ? ImGuiStackGuard.Take() : default;
            guarded = guarded && ImGuiStackGuard.AtWindowBase(bodyBase);   // drawn anywhere else: as before
            ImGuiNET.ImGui.PushID(_idScope);
            ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui11));
            int textStep = ImGuiFonts.CurrentStep;
            ImGuiFonts.CurrentStep = DrawFontStep(textStep);
            ImGuiStackGuard.Mark given = guarded ? ImGuiStackGuard.Take() : default;
            try { result = PluginManager.RenderEmbed(_plugin, _surface, avail, out want); }
            finally
            {
                ImGuiFonts.CurrentStep = textStep;
                ImGuiStackGuard.Mark after = default;
                StackCheck check = guarded ? ImGuiStackGuard.Check(given, out after) : StackCheck.Balanced;
                if (check == StackCheck.Balanced)
                {
                    ImGuiNET.ImGui.PopFont();
                    ImGuiNET.ImGui.PopID();
                }
                else
                {
                    // Ends and pops what the plugin left, with this face's ID scope and font.
                    ImGuiStackGuard.RecoverTo(bodyBase);
                    NoteUnbalanced(check, ImGuiStackGuard.Describe(given, after));
                }
            }
            if (result == PluginManager.EmbedFailed) _failedAtMs = now;
            else if (result == PluginManager.EmbedDrawn) _failedAtMs = -1;
            else if (_failedAtMs >= 0) { _failedAtMs = now; result = PluginManager.EmbedFailed; }   // still off since the throw
        }
        if (result == PluginManager.EmbedDrawn)
        {
            if (_fitHeight) Fit(want.Y, avail.X, want.X);
            return;
        }

        ImGuiNET.ImGui.SetCursorScreenPos(start + new Vector2(6, 6));
        Label(result switch
        {
            PluginManager.EmbedNoPlugin => $"{_title} comes with {_plugin}; it isn't loaded.",
            PluginManager.EmbedFailed => $"{_plugin} couldn't draw {_title} (see the log). It comes back when the plugin reloads.",
            _ => $"Waiting for {_plugin}…",
        }, Mute);
    }

    /// <summary>
    /// fitHeight: keeps the window as tall as the content the plugin drew (<paramref name="wantPx"/>,
    /// client px) and scales its width with a text size change. Docked only (popped out, the OS window
    /// is the player's to size); never while the mouse is down, so a drag or a resize isn't fought.
    /// </summary>
    private void Fit(float wantPx, float widthPx, float wantWidthPx = 0f)
    {
        if (wantPx <= 0f || ImGuiPopOuts.InPopOutFrame) return;
        float place = Math.Max(0.01f, ImGuiPanelHost.PlacementScale());
        float height = MathF.Ceiling(wantPx / place);
        float width = widthPx / place;
        // A plugin that sizes its own width too (2026-11-07: RynthDreamWeave's retail Character panel, a
        // fixed design width at its text size) reports a width other than the one it was given: the window
        // takes it, as it takes the height. Every other plugin hands back the width it got: no change.
        if (wantWidthPx > 0f && MathF.Abs(wantWidthPx - widthPx) > 1f && ImGuiNET.ImGui.IsMouseDown(ImGuiNET.ImGuiMouseButton.Left) == false)
        {
            _contentHeight = height;
            _fitWidth = MathF.Ceiling(wantWidthPx / place);
            _fitK = PanelTextScale.Factor(ImGuiFonts.CurrentStep);
            ImGuiPanelHost.RequestSize(_title, new Vector2(_fitWidth, height));
            return;
        }
        float k = PanelTextScale.Factor(ImGuiFonts.CurrentStep);
        float shownHeight = ImGuiNET.ImGui.GetWindowHeight() / place;
        bool mouseUp = !ImGuiNET.ImGui.IsMouseDown(ImGuiNET.ImGuiMouseButton.Left);
        _contentHeight = height;
        if (_fitK > 0f && MathF.Abs(k - _fitK) > 0.001f)
        {
            // A text size change: the width from the one it had at the old size.
            width = MathF.Max(_fitWidth * (k / _fitK), _minWidth * k);
            float screenW = ImGuiNET.ImGui.GetIO().DisplaySize.X / place;
            if (screenW > 1f) width = MathF.Min(width, screenW);
            ImGuiPanelHost.RequestSize(_title, new Vector2(width, height));
            // Clamped to the screen at the new size: near the right or bottom edge it moves in.
            ImGuiPanelHost.RequestPos(_title, ImGuiNET.ImGui.GetWindowPos() / place);
        }
        else if (mouseUp && MathF.Abs(shownHeight - height) > 0.5f)
        {
            ImGuiPanelHost.RequestSize(_title, new Vector2(width, height));
        }
        _fitK = k;
        _fitWidth = width;
    }

    // Unbalanced draws by plugin + surface: logged the first time, then every RepeatEvery-th.
    private static readonly System.Collections.Generic.Dictionary<string, int> Unbalanced = new(StringComparer.Ordinal);
    private const int RepeatEvery = 1000;

    private void NoteUnbalanced(StackCheck check, string what)
    {
        string key = _plugin + "/" + _title;
        Unbalanced.TryGetValue(key, out int n);
        Unbalanced[key] = ++n;
        if (n != 1 && n % RepeatEvery != 0) return;
        string again = n == 1 ? "" : $" ({n} times so far)";
        RynthLog.Warn(check == StackCheck.ClosedTooMuch
            ? $"PluginWindows: {_plugin} drawing '{_title}' ended or popped what it didn't open ({what}){again}. " +
              "What it left open was put back; what it took can't be, so the window may draw wrongly."
            : $"PluginWindows: {_plugin} drawing '{_title}' left ImGui's stacks unbalanced ({what}){again}. " +
              "Put back after its draw; the panels after it draw as normal.");
    }
}
