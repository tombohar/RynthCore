// ============================================================================
//  RynthCore.Engine - ImGui/RynthTheme.cs
//  Colours and metrics for the ImGui panel faces, taken from the Avalonia
//  panels so a docked ImGui face looks like its popped-out Avalonia face
//  (docs/IMGUI_PARITY_PLAN.md §4.4). Tokens are ABGR-packed ImGui colours
//  (ImGui.GetColorU32 order) built from the Avalonia #AARRGGBB literals.
//
//  Colour themes (Tom 2026-10-14): the structural colours come from a palette
//  the player can change in Settings > Display: a preset (RynthCore, Ember,
//  Verdant, Amethyst, Crimson, Slate, and since 2026-10-21 Brass, Volt and
//  Ivory, the instrument dashboard's three) or their own eight colours (accent,
//  highlight, background, title bars, buttons, borders, text, muted text).
//  The other shades (selected rows, fields, hover, the dashboard's border...)
//  are worked out from those eight; the RynthCore preset keeps every original
//  colour exactly. Status colours (health, mana, the green on/red off of
//  toggles, radar gold) don't follow the theme. The faces read the tokens
//  (properties) every frame, so a change shows at once; the base ImGui style
//  is re-coloured before the next frame (RefreshStyle, each ImGui context).
//  Saved to %AppData%\RynthCore\theme.txt (preset=, then name=AARRGGBB for the
//  player's own eight colours, kept whichever theme is in use since 2026-10-22);
//  the schemes a player saved are in ThemeSchemes.cs.
//
//  Apply() sets the base style once at context creation; per-window colours
//  (bar, RynthAi, Radar) are pushed around Begin by the panel host.
// ============================================================================

using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;
using RynthCore.Engine.UI;

namespace RynthCore.Engine.ImGuiBackend;

/// <summary>One colour theme: the eight colours a player picks, then the shades worked out from them (#AARRGGBB).</summary>
internal sealed class ThemePalette
{
    public const int Primaries = 8;
    /// <summary>The eight colours a player picks, in Settings order.</summary>
    public static readonly string[] PrimaryNames = { "Accent", "Highlight", "Background", "Title bars", "Buttons", "Borders", "Text", "Muted text" };
    private static readonly string[] PrimaryKeys = { "accent", "highlight", "background", "titlebar", "button", "border", "text", "muted" };

    public readonly uint[] P = new uint[Primaries];   // Accent, Highlight, Background, TitleBar, Button, Border, Text, TextMuted

    // The shades worked out from P (Derive), or the original literals (Default).
    public uint AccentFill, AccentAlt, BoxBg, Field, Selected, WindowBorder, DashBorder, PanelGlass, BarBg, FrameBg,
        FrameHover, FrameActive, BarButtonBorder, RowAlt, MuteAlt, StyleText, PopupBg, FrameBgHover, FrameBgActive, SliderGrab;

    // The instrument look's shades (2026-10-21, plugin windows: UiColorToken KeyFace..Faint), worked
    // out from the eight for every palette, Default included (DeriveInstrument).
    public uint KeyFace, Hairline, Inset, Faint;

    /// <summary>A copy (its own array of the eight).</summary>
    public ThemePalette Clone()
    {
        var c = new ThemePalette
        {
            AccentFill = AccentFill, AccentAlt = AccentAlt, BoxBg = BoxBg, Field = Field, Selected = Selected,
            WindowBorder = WindowBorder, DashBorder = DashBorder, PanelGlass = PanelGlass, BarBg = BarBg, FrameBg = FrameBg,
            FrameHover = FrameHover, FrameActive = FrameActive, BarButtonBorder = BarButtonBorder, RowAlt = RowAlt,
            MuteAlt = MuteAlt, StyleText = StyleText, PopupBg = PopupBg, FrameBgHover = FrameBgHover,
            FrameBgActive = FrameBgActive, SliderGrab = SliderGrab,
            KeyFace = KeyFace, Hairline = Hairline, Inset = Inset, Faint = Faint,
        };
        Array.Copy(P, c.P, Primaries);
        return c;
    }

    /// <summary>RynthCore's own colours, every shade exactly as it was before themes.</summary>
    public static ThemePalette Default()
    {
        var t = new ThemePalette();
        t.P[0] = 0xFF26D9E6; t.P[1] = 0xFFE8B333; t.P[2] = 0xFF0A0F14; t.P[3] = 0xFF1A1A28;
        t.P[4] = 0xFF16283A; t.P[5] = 0xFF34587A; t.P[6] = 0xFFF2F7FC; t.P[7] = 0xFFB8C8D8;
        t.AccentFill = 0xFF264C59; t.AccentAlt = 0xFF26C1A6; t.BoxBg = 0xFF0A121A; t.Field = 0xFF141F29;
        t.Selected = 0xFF1A2E42; t.WindowBorder = 0xFF008080; t.DashBorder = 0xFF264059; t.PanelGlass = 0xFF12121C;
        t.BarBg = 0xFF0A0A14; t.FrameBg = 0xFF1A2230; t.FrameHover = 0xFF2A3850; t.FrameActive = 0xFF34466A;
        t.BarButtonBorder = 0xFF3A4A5A; t.RowAlt = 0xFF101822; t.MuteAlt = 0xFFC8D6E4; t.StyleText = 0xFFE6E6E6;
        t.PopupBg = 0xFF14141E; t.FrameBgHover = 0xFF243044; t.FrameBgActive = 0xFF2C3A52; t.SliderGrab = 0xFF26A18C;
        t.DeriveInstrument();
        return t;
    }

    /// <summary>A palette from eight colours, the other shades worked out from them.</summary>
    public static ThemePalette From(params uint[] primaries)
    {
        var t = new ThemePalette();
        for (int i = 0; i < Primaries; i++) t.P[i] = 0xFF000000 | primaries[i];
        t.Derive();
        return t;
    }

    /// <summary>Works out the shades from the eight colours (each a mix of two of them, like the originals).</summary>
    public void Derive()
    {
        uint accent = P[0], bg = P[2], title = P[3], button = P[4], border = P[5], text = P[6], muted = P[7];
        AccentFill = Mix(bg, accent, 0.30f);
        AccentAlt = Mix(accent, bg, 0.12f);
        BoxBg = Mix(bg, button, 0.15f);
        Field = Mix(bg, button, 0.45f);
        Selected = Mix(button, accent, 0.12f);
        WindowBorder = Mix(bg, accent, 0.50f);
        DashBorder = Mix(bg, border, 0.65f);
        PanelGlass = Mix(bg, title, 0.40f);
        BarBg = bg;
        FrameBg = Mix(bg, button, 0.80f);
        FrameHover = Mix(button, border, 0.40f);
        FrameActive = Mix(button, border, 0.70f);
        BarButtonBorder = Mix(button, border, 0.60f);
        RowAlt = Mix(bg, button, 0.30f);
        MuteAlt = Mix(muted, text, 0.30f);
        StyleText = Mix(text, muted, 0.25f);
        PopupBg = Mix(bg, title, 0.50f);
        FrameBgHover = Mix(FrameBg, FrameHover, 0.50f);
        FrameBgActive = Mix(FrameHover, FrameActive, 0.50f);
        SliderGrab = Mix(AccentAlt, bg, 0.17f);
        DeriveInstrument();
    }

    /// <summary>
    /// The instrument look's shades from the eight: a key's face (a step up from the background),
    /// the hairlines, the inset wells (the activity line, bar tracks) and the faint text.
    /// </summary>
    public void DeriveInstrument()
    {
        uint bg = P[2], button = P[4], border = P[5], muted = P[7];
        KeyFace = Mix(bg, button, 0.60f);
        Hairline = Mix(bg, border, 0.45f);
        Inset = Mix(bg, 0xFF000000, 0.45f);
        Faint = Mix(muted, bg, 0.28f);
    }

    /// <summary>#AARRGGBB mix: <paramref name="t"/> of the way from <paramref name="a"/> to <paramref name="b"/>, opaque.</summary>
    public static uint Mix(uint a, uint b, float t)
    {
        static uint Ch(uint x, int shift) => (x >> shift) & 0xFF;
        uint r = (uint)MathF.Round(Ch(a, 16) + (Ch(b, 16) - (float)Ch(a, 16)) * t);
        uint g = (uint)MathF.Round(Ch(a, 8) + (Ch(b, 8) - (float)Ch(a, 8)) * t);
        uint bl = (uint)MathF.Round(Ch(a, 0) + (Ch(b, 0) - (float)Ch(a, 0)) * t);
        return 0xFF000000 | (r << 16) | (g << 8) | bl;
    }

    // ── Presets ─────────────────────────────────────────────────────────
    // Brass, Volt and Ivory (2026-10-21): the instrument dashboard's three, from its Claude Design
    // canvas. Their Buttons and Borders are chosen so DeriveInstrument lands on the canvas's key
    // face and hairline; appended, so the older presets keep their places in the picker.
    public static readonly string[] PresetNames = { "RynthCore", "Ember", "Verdant", "Amethyst", "Crimson", "Slate", "Brass", "Volt", "Ivory" };

    public static ThemePalette Preset(int index) => index switch
    {
        1 => From(0xFF8A3D, 0xFFD166, 0x140C0A, 0x24150F, 0x3A2116, 0x7A4A34, 0xFCF4F0, 0xD8C4B8),
        2 => From(0x4ADE80, 0xE8D333, 0x0A140D, 0x12201A, 0x16352A, 0x347A58, 0xF2FCF5, 0xB8D8C4),
        3 => From(0xB98CFF, 0xFFC857, 0x0F0A16, 0x1C1428, 0x2A1C40, 0x5A4A8A, 0xF6F2FC, 0xCBBEDC),
        4 => From(0xFF5C6C, 0xFFC857, 0x140A0C, 0x241216, 0x3A161E, 0x7A3442, 0xFCF2F3, 0xD8B8BE),
        5 => From(0xD0D8E0, 0xF0C060, 0x0E0F11, 0x1A1C20, 0x24282E, 0x4A5260, 0xF4F5F7, 0xB4BAC2),
        6 => From(0xE3B662, 0x85C7B2, 0x12100D, 0x1C1915, 0x26221E, 0x595349, 0xEEE9DC, 0xB0A89B),
        7 => From(0xCBF559, 0xF7B245, 0x0E1115, 0x161A1F, 0x1F2329, 0x4C5661, 0xEDF0F4, 0xA7ADB5),
        8 => From(0xFCF7E7, 0xF7B245, 0x100F0E, 0x191918, 0x222322, 0x535451, 0xF1EEE6, 0xABA9A5),
        _ => Default(),
    };

    // ── File (theme.txt) ────────────────────────────────────────────────
    public static string Key(int primary) => PrimaryKeys[primary];
}

internal static class RynthTheme
{
    /// <summary>Avalonia #AARRGGBB → ImGui packed colour (0xAABBGGRR).</summary>
    public static uint Argb(uint argb) =>
        (argb & 0xFF00FF00) | ((argb & 0x00FF0000) >> 16) | ((argb & 0x000000FF) << 16);

    public static Vector4 Vec(uint argb) => new(
        ((argb >> 16) & 0xFF) / 255f, ((argb >> 8) & 0xFF) / 255f, (argb & 0xFF) / 255f, (argb >> 24) / 255f);

    /// <summary>An ImGui packed colour (a token) as a Vector4 (TextColored, style colours).</summary>
    public static Vector4 VecOf(uint abgr) => ImGuiNET.ImGui.ColorConvertU32ToFloat4(abgr);

    /// <summary>A token with another alpha (0..255).</summary>
    public static uint WithAlpha(uint abgr, uint alpha) => (abgr & 0x00FFFFFF) | (alpha << 24);

    // ── The palette ────────────────────────────────────────────────────
    // The current palette's tokens, ABGR, swapped whole on a change (readers never lock).
    private sealed class Tokens
    {
        public uint Accent, Highlight, Background, TitleBar, Button, Border, Text, TextMuted,
            AccentFill, AccentAlt, BoxBg, Field, Selected, WindowBorder, DashBorder, PanelGlass, BarBg, FrameBg,
            FrameHover, FrameActive, BarButtonBorder, RowAlt, MuteAlt, StyleText, PopupBg, FrameBgHover, FrameBgActive, SliderGrab,
            KeyFace, Hairline, Inset, Faint;

        public Tokens(ThemePalette p)
        {
            Accent = Argb(p.P[0]); Highlight = Argb(p.P[1]); Background = Argb(p.P[2]); TitleBar = Argb(p.P[3]);
            Button = Argb(p.P[4]); Border = Argb(p.P[5]); Text = Argb(p.P[6]); TextMuted = Argb(p.P[7]);
            AccentFill = Argb(p.AccentFill); AccentAlt = Argb(p.AccentAlt); BoxBg = Argb(p.BoxBg); Field = Argb(p.Field);
            Selected = Argb(p.Selected); WindowBorder = Argb(p.WindowBorder); DashBorder = Argb(p.DashBorder);
            PanelGlass = Argb(p.PanelGlass); BarBg = Argb(p.BarBg); FrameBg = Argb(p.FrameBg); FrameHover = Argb(p.FrameHover);
            FrameActive = Argb(p.FrameActive); BarButtonBorder = Argb(p.BarButtonBorder); RowAlt = Argb(p.RowAlt);
            MuteAlt = Argb(p.MuteAlt); StyleText = Argb(p.StyleText); PopupBg = Argb(p.PopupBg);
            FrameBgHover = Argb(p.FrameBgHover); FrameBgActive = Argb(p.FrameBgActive); SliderGrab = Argb(p.SliderGrab);
            KeyFace = Argb(p.KeyFace); Hairline = Argb(p.Hairline); Inset = Argb(p.Inset); Faint = Argb(p.Faint);
        }
    }

    private static readonly object Sync = new();
    private static ThemePalette _palette = ThemePalette.Default();
    private static volatile Tokens _t = new(_palette);
    private static int _preset;            // ThemePalette.PresetNames index, or Custom
    private static int _version;
    private static bool _loaded;
    public const int Custom = -1;

    /// <summary>Bumped on every change: each ImGui context re-colours its base style when it differs.</summary>
    public static int Version => System.Threading.Volatile.Read(ref _version);

    // Theme tokens (ABGR). The eight a player picks:
    public static uint Accent => _t.Accent;
    public static uint Highlight => _t.Highlight;
    public static uint Background => _t.Background;
    public static uint TitleBar => _t.TitleBar;
    public static uint Button => _t.Button;
    public static uint Border => _t.Border;
    public static uint Text => _t.Text;
    public static uint TextMuted => _t.TextMuted;
    // ...and the shades worked out from them:
    public static uint AccentFill => _t.AccentFill;
    public static uint AccentAlt => _t.AccentAlt;
    public static uint BoxBg => _t.BoxBg;
    public static uint Field => _t.Field;
    public static uint Selected => _t.Selected;
    public static uint WindowBorder => _t.WindowBorder;
    public static uint DashBorder => _t.DashBorder;
    public static uint FrameBg => _t.FrameBg;
    public static uint FrameHover => _t.FrameHover;
    public static uint FrameActive => _t.FrameActive;
    public static uint BarButtonBorder => _t.BarButtonBorder;
    public static uint RowAlt => _t.RowAlt;
    public static uint MuteAlt => _t.MuteAlt;
    // ...and the instrument look's (plugin windows, UiColorToken KeyFace..Faint):
    public static uint KeyFace => _t.KeyFace;
    public static uint Hairline => _t.Hairline;
    public static uint Inset => _t.Inset;
    public static uint Faint => _t.Faint;

    // ── Bar (AvaloniaOverlay.cs BuildRoot) ─────────────────────────────
    public static uint BarBackground => WithAlpha(_t.BarBg, 0xE6);
    public const float BarRounding = 4f;
    public static uint BarButton => _t.FrameBg;
    // A button whose window is open (docked or popped out, BarOpenState): the accent fill
    // (Chat's active tab), halfway to the accent under the mouse, and an accent border.
    public static uint BarButtonOpen => _t.AccentFill;
    public static uint BarButtonOpenHover => _t.WindowBorder;
    public static uint BarButtonOpenBorder => _t.Accent;
    public static readonly uint BarText = Argb(0xFFFFFFFF);
    public static readonly uint RcLabel = Argb(0xFF90EE90);      // LightGreen
    public static readonly uint ReloadLabel = Argb(0xFFF0E68C);  // Khaki
    public static readonly uint PopOutLabel = Argb(0xFF87CEFA);  // LightSkyBlue

    // ── Standard panel frame ───────────────────────────────────────────
    public static uint PanelGlass => WithAlpha(_t.PanelGlass, 0xF2);   // was D2: the game showed through behind text
    public static uint PanelBorder => _t.WindowBorder;
    public const float PanelRounding = 4f;
    public static uint PanelHeader => _t.TitleBar;
    public static uint DragLabel => _t.WindowBorder;
    public const float HeaderHeight = 24f;
    public static uint GripIdle => WithAlpha(_t.AccentAlt, 0x20);
    public static uint GripHover => WithAlpha(_t.AccentAlt, 0x55);
    public static uint GripGlyph => _t.WindowBorder;

    // ── RynthAi dashboard ──────────────────────────────────────────────
    public static uint RynthAiBackground => WithAlpha(_t.Background, 0xF2);
    public static uint RynthAiBorder => _t.DashBorder;
    public const float RynthAiRounding = 3f;

    // ── Radar ──────────────────────────────────────────────────────────
    public static readonly uint RadarGold = Argb(0xFFE6C766);
    public static readonly uint RadarGripIdle = Argb(0x400A121A);
    public static readonly uint RadarGripHover = Argb(0xA00A121A);

    // ── Choosing a theme (Settings > Display) ───────────────────────────
    //
    // Three kinds of theme (Tom 2026-10-22: a new preset must never cost a player the colours they
    // made, and players can keep their own schemes):
    //   a preset         RynthCore, Ember ... Brass, Volt, Ivory (ThemePalette.PresetNames)
    //   a saved scheme   a named set of the eight the player saved (ThemeSchemes, themes\*.theme)
    //   Custom           the player's own eight colours. Kept whichever theme is in use and always
    //                    written to theme.txt, so picking Custom again brings them back.
    // Changing a colour makes the theme Custom with that change; when the player's own colours would
    // be replaced that way (the change was made on a preset or a scheme) and aren't kept anywhere
    // else, they are saved as a scheme first ("My colours"), so no click loses them.

    public const int Scheme = -2;
    private static ThemePalette _custom = ThemePalette.Default();   // the player's own eight (Custom)
    private static bool _customSet;                                 // theme.txt had them, or the player made them
    private static string _scheme = string.Empty;                   // the saved scheme in use (Scheme)

    /// <summary>The preset in use (ThemePalette.PresetNames index), Scheme, or Custom.</summary>
    public static int Preset { get { EnsureLoaded(); return _preset; } }

    /// <summary>The saved scheme in use ("" unless <see cref="Preset"/> is Scheme).</summary>
    public static string SchemeName { get { EnsureLoaded(); lock (Sync) return _preset == Scheme ? _scheme : string.Empty; } }

    public static bool SchemeInUse => Preset == Scheme;

    /// <summary>Settings' Theme picker now: the presets, the saved schemes, then Custom.</summary>
    public static string[] PickerNames()
    {
        string[] schemes = ThemeSchemes.Names;
        var names = new string[ThemePalette.PresetNames.Length + schemes.Length + 1];
        Array.Copy(ThemePalette.PresetNames, names, ThemePalette.PresetNames.Length);
        Array.Copy(schemes, 0, names, ThemePalette.PresetNames.Length, schemes.Length);
        names[^1] = "Custom";
        return names;
    }

    public static int PickerIndex
    {
        get
        {
            int p = Preset;
            string[] schemes = ThemeSchemes.Names;
            if (p >= 0) return p;
            if (p == Scheme)
            {
                int i = Array.FindIndex(schemes, n => n.Equals(SchemeName, StringComparison.OrdinalIgnoreCase));
                if (i >= 0) return ThemePalette.PresetNames.Length + i;
            }
            return ThemePalette.PresetNames.Length + schemes.Length;
        }
    }

    /// <summary>The picker's item <paramref name="index"/>: a preset, a saved scheme, or Custom (the player's own colours).</summary>
    public static void SetPicker(int index)
    {
        if (index >= 0 && index < ThemePalette.PresetNames.Length) { SetPreset(index); return; }
        string[] schemes = ThemeSchemes.Names;
        int s = index - ThemePalette.PresetNames.Length;
        if (s >= 0 && s < schemes.Length) { UseScheme(schemes[s]); return; }
        UseCustom();
    }

    /// <summary>Uses preset <paramref name="index"/>; out of range means Custom (the player's own colours).</summary>
    public static void SetPreset(int index)
    {
        EnsureLoaded();
        if (index < 0 || index >= ThemePalette.PresetNames.Length)
        {
            UseCustom();
            return;
        }
        lock (Sync)
        {
            _preset = index;
            _palette = ThemePalette.Preset(index);
        }
        Changed();
    }

    /// <summary>The player's own colours (Custom), as they last left them.</summary>
    public static void UseCustom()
    {
        EnsureLoaded();
        lock (Sync)
        {
            _preset = Custom;
            _palette = _custom.Clone();
        }
        Changed();
    }

    /// <summary>A saved scheme by name; false when there is none by that name (or it can't be read).</summary>
    public static bool UseScheme(string name)
    {
        EnsureLoaded();
        ThemePalette? p = ThemeSchemes.Load(name);
        if (p == null) return false;
        lock (Sync)
        {
            _preset = Scheme;
            _scheme = ThemeSchemes.DisplayName(name);
            _palette = p;
        }
        Changed();
        return true;
    }

    /// <summary>
    /// Saves the colours in use as scheme <paramref name="name"/> (replacing one of that name) and
    /// switches to it. Returns the name saved, or "" for an empty or unusable name.
    /// </summary>
    public static string SaveScheme(string name)
    {
        EnsureLoaded();
        name = ThemeSchemes.CleanName(name);
        if (name.Length == 0) return string.Empty;
        ThemePalette p;
        lock (Sync) p = _palette.Clone();
        if (!ThemeSchemes.Save(name, p)) return string.Empty;
        lock (Sync)
        {
            _preset = Scheme;
            _scheme = name;
        }
        Changed();
        return name;
    }

    /// <summary>Deletes the scheme in use; its colours stay on screen as the player's own (Custom).</summary>
    public static bool DeleteCurrentScheme()
    {
        EnsureLoaded();
        string name = SchemeName;
        if (name.Length == 0 || !ThemeSchemes.Delete(name)) return false;
        lock (Sync)
        {
            _custom = _palette.Clone();
            _customSet = true;
            _preset = Custom;
            _scheme = string.Empty;
        }
        Changed();
        return true;
    }

    /// <summary>One of the eight colours (#AARRGGBB).</summary>
    public static uint GetPrimary(int i)
    {
        EnsureLoaded();
        lock (Sync) return _palette.P[Math.Clamp(i, 0, ThemePalette.Primaries - 1)];
    }

    /// <summary>
    /// Changes one of the eight colours (#RRGGBB, opaque): the theme becomes Custom with the change and
    /// its shades follow. Made on a preset or a scheme, it replaces the player's own colours, which are
    /// saved as a scheme first when they aren't kept anywhere yet.
    /// </summary>
    public static void SetPrimary(int i, uint argb)
    {
        EnsureLoaded();
        if (i < 0 || i >= ThemePalette.Primaries) return;
        argb |= 0xFF000000;
        ThemePalette? keep = null;
        lock (Sync)
        {
            if (_palette.P[i] == argb) return;
            if (_preset != Custom && _customSet) keep = _custom.Clone();
        }
        if (keep != null) ThemeSchemes.KeepIfNew("My colours", keep);
        lock (Sync)
        {
            var p = _palette.Clone();
            p.P[i] = argb;
            p.Derive();
            _palette = p;
            _custom = p.Clone();
            _customSet = true;
            _preset = Custom;
            _scheme = string.Empty;
        }
        Changed();
    }

    private static void Changed()
    {
        lock (Sync) _t = new Tokens(_palette);
        System.Threading.Interlocked.Increment(ref _version);
        UiBackgroundWriter.Enqueue("theme", Save);
    }

    /// <summary>
    /// Re-colours the current context's base style when the theme changed since <paramref name="applied"/>
    /// (a field the context's owner keeps). Call before NewFrame. AC thread.
    /// </summary>
    public static void RefreshStyle(ref int applied)
    {
        int v = Version;
        if (v == applied) return;
        applied = v;
        ApplyColors(ImGuiNET.ImGui.GetStyle());
    }

    /// <summary>
    /// Reads theme.txt if it hasn't been read yet. Engine init calls this so AC's thread never does.
    /// "preset=" names a preset, "scheme:Name" a saved scheme, or Custom; the eight colour lines are the
    /// player's own colours whatever the preset (before 2026-10-22 they were written only for Custom).
    /// A scheme that is gone falls back to the player's own colours.
    /// </summary>
    public static void EnsureLoaded()
    {
        if (_loaded) return;
        lock (Sync)
        {
            if (_loaded) return;
            try
            {
                if (File.Exists(FilePath))
                {
                    int preset = 0;
                    string scheme = string.Empty;
                    var custom = ThemePalette.Default();
                    bool anyColour = false;
                    foreach (string raw in File.ReadAllLines(FilePath))
                    {
                        string line = raw.Trim();
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        string key = line[..eq].Trim(), value = line[(eq + 1)..].Trim();
                        if (key.Equals("preset", StringComparison.OrdinalIgnoreCase))
                        {
                            if (value.StartsWith("scheme:", StringComparison.OrdinalIgnoreCase))
                            {
                                scheme = value["scheme:".Length..].Trim();
                                preset = Scheme;
                                continue;
                            }
                            int idx = Array.FindIndex(ThemePalette.PresetNames, n => n.Equals(value, StringComparison.OrdinalIgnoreCase));
                            preset = idx >= 0 ? idx : Custom;
                            continue;
                        }
                        for (int i = 0; i < ThemePalette.Primaries; i++)
                            if (key.Equals(ThemePalette.Key(i), StringComparison.OrdinalIgnoreCase)
                                && uint.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out uint c))
                            {
                                custom.P[i] = 0xFF000000 | c;
                                anyColour = true;
                            }
                    }
                    custom.Derive();
                    _custom = custom;
                    _customSet = anyColour;
                    ThemePalette? fromScheme = preset == Scheme ? ThemeSchemes.Load(scheme) : null;
                    if (preset == Scheme && fromScheme == null) preset = Custom;
                    _preset = preset;
                    _scheme = preset == Scheme ? ThemeSchemes.DisplayName(scheme) : string.Empty;
                    _palette = preset == Custom ? custom.Clone() : preset == Scheme ? fromScheme! : ThemePalette.Preset(preset);
                    _t = new Tokens(_palette);
                    _version++;
                }
            }
            catch { /* unreadable: RynthCore's colours */ }
            _loaded = true;
        }
    }

    /// <summary>%AppData%\RynthCore, or RYNTHCORE_THEME_DIR when set (tools\ThemeTests' temp folder).</summary>
    internal static string Folder =>
        Environment.GetEnvironmentVariable("RYNTHCORE_THEME_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RynthCore");

    private static string FilePath => Path.Combine(Folder, "theme.txt");

    private static void Save()
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            lock (Sync)
            {
                sb.Append("preset=")
                  .Append(_preset == Custom ? "Custom" : _preset == Scheme ? "scheme:" + _scheme : ThemePalette.PresetNames[_preset])
                  .Append('\n');
                // The player's own colours, whatever is in use (an older engine reads them only for Custom).
                if (_customSet || _preset == Custom)
                    for (int i = 0; i < ThemePalette.Primaries; i++)
                        sb.Append(ThemePalette.Key(i)).Append('=').Append((_custom.P[i] & 0xFFFFFF).ToString("X6")).Append('\n');
            }
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, sb.ToString());
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Base style for every ImGui face, scaled for <paramref name="scale"/>
    /// (DPI / 96). AC thread, once, right after the context is created.
    /// </summary>
    public static void Apply(ImGuiStylePtr style, float scale)
    {
        ImGuiNET.ImGui.StyleColorsDark();

        style.WindowRounding = PanelRounding;
        style.ChildRounding = 3f;
        style.FrameRounding = 2f;
        style.PopupRounding = 3f;
        style.GrabRounding = 2f;
        style.ScrollbarRounding = 3f;
        style.WindowBorderSize = 1f;
        style.FrameBorderSize = 0f;
        style.WindowPadding = new Vector2(6f, 6f);
        style.FramePadding = new Vector2(5f, 2f);
        style.ItemSpacing = new Vector2(6f, 4f);
        style.ScrollbarSize = 12f;
        style.WindowMinSize = new Vector2(20f, 20f);

        ApplyColors(style);

        if (scale != 1f)
            style.ScaleAllSizes(scale);
    }

    /// <summary>The base style's colours from the theme (Apply, and RefreshStyle after a change).</summary>
    private static void ApplyColors(ImGuiStylePtr style)
    {
        Tokens t = _t;
        Set(style, ImGuiCol.Text, t.StyleText);
        Set(style, ImGuiCol.TextDisabled, Argb(0xFF808080));
        Set(style, ImGuiCol.WindowBg, WithAlpha(t.PanelGlass, 0xD2));
        Set(style, ImGuiCol.ChildBg, 0);
        Set(style, ImGuiCol.PopupBg, WithAlpha(t.PopupBg, 0xF2));
        Set(style, ImGuiCol.Border, t.WindowBorder);
        Set(style, ImGuiCol.FrameBg, t.FrameBg);
        Set(style, ImGuiCol.FrameBgHovered, t.FrameBgHover);
        Set(style, ImGuiCol.FrameBgActive, t.FrameBgActive);
        Set(style, ImGuiCol.TitleBg, t.TitleBar);
        Set(style, ImGuiCol.TitleBgActive, t.TitleBar);
        Set(style, ImGuiCol.Button, t.FrameBg);
        Set(style, ImGuiCol.ButtonHovered, t.FrameHover);
        Set(style, ImGuiCol.ButtonActive, t.FrameActive);
        Set(style, ImGuiCol.Header, t.FrameBg);
        Set(style, ImGuiCol.HeaderHovered, t.FrameHover);
        Set(style, ImGuiCol.HeaderActive, t.FrameActive);
        Set(style, ImGuiCol.CheckMark, t.AccentAlt);
        Set(style, ImGuiCol.SliderGrab, t.SliderGrab);
        Set(style, ImGuiCol.SliderGrabActive, t.AccentAlt);
        Set(style, ImGuiCol.Separator, WithAlpha(t.BarButtonBorder, 0x50));
        Set(style, ImGuiCol.ResizeGrip, WithAlpha(t.AccentAlt, 0x20));
        Set(style, ImGuiCol.ResizeGripHovered, WithAlpha(t.AccentAlt, 0x55));
        Set(style, ImGuiCol.ResizeGripActive, WithAlpha(t.AccentAlt, 0x80));
        Set(style, ImGuiCol.ScrollbarBg, Argb(0x40000000));
        Set(style, ImGuiCol.ScrollbarGrab, t.FrameHover);
        Set(style, ImGuiCol.TableHeaderBg, t.TitleBar);
        Set(style, ImGuiCol.TableBorderStrong, t.BarButtonBorder);
        Set(style, ImGuiCol.TableBorderLight, WithAlpha(t.BarButtonBorder, 0x50));
        Set(style, ImGuiCol.TableRowBgAlt, Argb(0x10FFFFFF));
        Set(style, ImGuiCol.ModalWindowDimBg, 0);
    }

    private static void Set(ImGuiStylePtr style, ImGuiCol col, uint abgr) => style.Colors[(int)col] = new Vector4(
        (abgr & 0xFF) / 255f, ((abgr >> 8) & 0xFF) / 255f, ((abgr >> 16) & 0xFF) / 255f, (abgr >> 24) / 255f);
}

/// <summary>DPI scale for the ImGui faces.</summary>
internal static class UiScale
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>
    /// GetDpiForWindow(game) / 96, at least 1. For a DPI-unaware acclient this
    /// is 1 (Windows scales the whole window); ImGui then works in the same
    /// client pixels AC does.
    /// </summary>
    public static float ForWindow(IntPtr hwnd)
    {
        try
        {
            uint dpi = hwnd != IntPtr.Zero ? GetDpiForWindow(hwnd) : 0;
            return dpi > 0 ? Math.Max(1f, dpi / 96f) : 1f;
        }
        catch (EntryPointNotFoundException)
        {
            return 1f; // pre-1607 Windows 10
        }
    }
}
