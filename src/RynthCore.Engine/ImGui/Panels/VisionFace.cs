// ============================================================================
//  RynthCore.Engine - ImGui/Panels/VisionFace.cs
//  ImGui face of the RynthVision settings (UI/Panels/RynthVisionPanel.cs),
//  in three tabs:
//    Terrain      the plugin's overlays (radar ring, unclimbable slopes,
//                 impassable water), colours (AARRGGBB hex + swatch) each
//                 with an opacity slider (the colour's alpha byte), tuning
//                 sliders (Ctrl+click to type a value), water terrain types,
//                 and "Log terrain types here";
//    Nameplates   the RynthVision nameplates (monster plates, debuff icons,
//                 your own plate), engine-side (Hud/MonsterHudSettingsUi);
//    Combat text  floating damage / heal numbers, kill burst and gains.
//
//  2026-10-27: RynthVision draws the Vision window itself (its manifest's
//  "takeOver" window, RynthVision Ui/VisionWindow.cs, on engines with plugin
//  API v91): this face then steps aside (ImGuiPanelHost.TakeOver; same key, so
//  the saved place, bar button, hotkeys and /rc commands carry over) and comes back
//  when RynthVision unloads. Its Nameplates and Combat text tabs are drawn into
//  RynthVision's window by the engine (SettingsDrawerBodies "nameplates" /
//  "combattext"). Kept for an older RynthVision; goes a release after.
//
//  Terrain edits a private copy and saves just the changed field
//  (VisionCommands) when a control finishes changing (a slider on release).
//  A newer snapshot replaces the copy unless a control is in use. The other
//  two tabs edit MonsterHudSettings directly (saved in the background).
// ============================================================================

using System;
using System.Numerics;
using ImGuiNET;
using RynthCore.Engine.UI.Data;
using static RynthCore.Engine.ImGuiBackend.Panels.FaceKit;

namespace RynthCore.Engine.ImGuiBackend.Panels;

internal sealed class VisionFace : IImGuiPanel
{
    public const string Title = "Vision";

    public static void Register() => ImGuiPanelHost.Register(Title,
        new PanelSpec(new Vector2(320, 560), new Vector2(280, 240), EdgeToEdge: true),
        () => new VisionFace());

    private static readonly Vector4 HeaderColor = RynthTheme.Vec(0xFF7AB8F5);

    private VisionSettings? _s;
    private long _seenVersion = -1;
    private bool _busy;
    private readonly byte[] _slopeHex = new byte[12], _waterHex = new byte[12], _radarHex = new byte[12];
    private readonly byte[] _waterTypes = new byte[128];
    private int _tab;
    private static readonly string[] TabNames = { "Terrain", "Nameplates", "Combat text" };
    private static readonly string[] TabIds = { "##vtab0", "##vtab1", "##vtab2" };

    public void OnShown()
    {
        UiSources.Vision.Subscribe();
        UiSources.Vision.RequestRefresh();
    }

    public void OnHidden() => UiSources.Vision.Unsubscribe();

    public void Draw()
    {
        TakeSnapshot();
        float w = Begin(out Vector2 origin, out Vector2 size);
        TabStrip(w);

        if (_tab == 0)
        {
            _busy = DrawTerrain(w, origin, size);
        }
        else
        {
            _busy = false;
            ImGuiNET.ImGui.BeginChild("##vision_overlays", new Vector2(w, Math.Max(60, Remaining(origin, size))));
            ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui11));
            float cw = ImGuiNET.ImGui.GetContentRegionAvail().X - 4;
            if (_tab == 1) Hud.MonsterHudSettingsUi.DrawNameplates(cw);
            else Hud.MonsterHudSettingsUi.DrawCombatText(cw);
            ImGuiNET.ImGui.PopFont();
            ImGuiNET.ImGui.EndChild();
        }
        End(origin, size);
    }

    /// <summary>Terrain | Nameplates | Combat text, as a row of flat buttons.</summary>
    private void TabStrip(float w)
    {
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        float x = p.X;
        float bw = MathF.Floor((w - 8) / TabNames.Length);
        for (int i = 0; i < TabNames.Length; i++)
        {
            bool sel = i == _tab;
            if (Button(TabIds[i], TabNames[i], new Vector2(x, p.Y), new Vector2(bw, 24), sel ? Teal : Mute,
                    sel ? Selected : BtnFill, border: sel ? Teal : 0))
                _tab = i;
            x += bw + 4;
        }
        NextLine(p, 30);
    }

    /// <summary>The plugin's own settings. True while a control is in use.</summary>
    private bool DrawTerrain(float w, Vector2 origin, Vector2 size)
    {
        bool busy = false;
        if (_s == null)
        {
            Label("Waiting for the RynthVision plugin…", Mute);
            return false;
        }
        VisionSettings s = _s;

        ImGuiNET.ImGui.BeginChild("##vision_body", new Vector2(w, Math.Max(60, Remaining(origin, size))));
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, Text);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBg, BtnFill);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.CheckMark, Teal);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.SliderGrab, Teal);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.SliderGrabActive, Amber);

        Header("Overlays");
        if (ImGuiNET.ImGui.Checkbox("Radar range ring", ref s.Radar)) Save(VisionKeys.Radar);
        if (ImGuiNET.ImGui.Checkbox("Unclimbable slopes", ref s.Slopes)) Save(VisionKeys.Slopes);
        if (ImGuiNET.ImGui.Checkbox("Impassable water", ref s.Water)) Save(VisionKeys.Water);

        Header("Colors (hex AARRGGBB) and opacity");
        ImGuiNET.ImGui.PushItemWidth(Math.Max(120, ImGuiNET.ImGui.GetContentRegionAvail().X - 8));
        busy |= ColorRow(SlopeColorF, _slopeHex, ref s.SlopeColor);
        busy |= OpacitySlider(SlopeOpacityF, _slopeHex, ref s.SlopeColor);
        busy |= ColorRow(WaterColorF, _waterHex, ref s.WaterColor);
        busy |= OpacitySlider(WaterOpacityF, _waterHex, ref s.WaterColor);
        busy |= ColorRow(RadarColorF, _radarHex, ref s.RadarColor);
        busy |= OpacitySlider(RadarOpacityF, _radarHex, ref s.RadarColor);
        ImGuiNET.ImGui.PopItemWidth();

        Header("Tuning");
        ImGuiNET.ImGui.PushItemWidth(Math.Max(120, ImGuiNET.ImGui.GetContentRegionAvail().X - 8));
        busy |= Slider(RadarRangeF, ref s.RadarRange, 24, 300, "%.0f");
        busy |= Slider(RingThickF, ref s.RingThick, 0.5, 6, "%.1f");
        busy |= Slider(RingHeightF, ref s.RingHeight, 0.5, 30, "%.1f");
        busy |= IntSlider(SlopeRadiusF, ref s.SlopeRadius, 1, 24);
        busy |= Slider(SlopeFloorZF, ref s.SlopeFloorZ, 0.30, 0.95, "%.3f");
        busy |= Slider(SlopeBiasF, ref s.SlopeBias, 0.00, 1.00, "%.2f");
        busy |= IntSlider(WaterRadiusF, ref s.WaterRadius, 1, 24);
        ImGuiNET.ImGui.PopItemWidth();

        Header("Water terrain types");
        if (ImGuiNET.ImGui.Checkbox("Highlight cell if ANY corner is water (else all four)", ref s.WaterAnyCorner)) Save(VisionKeys.WaterAnyCorner);
        if (ImGuiNET.ImGui.Checkbox("Only paint water cells with an unwalkable triangle (real impassable)", ref s.WaterImpassableOnly)) Save(VisionKeys.WaterImpassableOnly);
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        bool enter = TextBox("##water_types", _waterTypes, p, 160, "18,19,20", out bool typing, ImGuiInputTextFlags.EnterReturnsTrue);
        bool done = ImGuiNET.ImGui.IsItemDeactivatedAfterEdit();
        busy |= typing;
        bool apply = Button("##water_apply", "Apply", new Vector2(p.X + 164, p.Y), new Vector2(56, 22), Text, BtnFill);
        if (enter || done || apply)
        {
            // An empty list resets to the defaults in the plugin; the republish
            // after the save brings them back here.
            s.WaterTypes = VisionSettings.ParseCsv(Utf8(_waterTypes));
            WriteUtf8(_waterTypes, string.Join(",", s.WaterTypes));
            Save(VisionKeys.WaterTypes);
        }
        NextLine(p, 26);

        p = ImGuiNET.ImGui.GetCursorScreenPos();
        if (Button("##inspect", "Log terrain types here", p, new Vector2(ButtonWidth("Log terrain types here") + 8, 24), Text, BtnFill))
            VisionCommands.Inspect();
        NextLine(p, 28);

        ImGuiNET.ImGui.PopStyleColor(5);
        ImGuiNET.ImGui.EndChild();
        return busy;
    }

    private void TakeSnapshot()
    {
        var snap = UiSources.Vision.Current;
        if (snap == null || snap.Version == _seenVersion || _busy) return;
        _seenVersion = snap.Version;
        _s = snap.Value.Settings.Clone();
        WriteUtf8(_slopeHex, _s.SlopeColor.ToString("X8"));
        WriteUtf8(_waterHex, _s.WaterColor.ToString("X8"));
        WriteUtf8(_radarHex, _s.RadarColor.ToString("X8"));
        WriteUtf8(_waterTypes, string.Join(",", _s.WaterTypes));
    }

    private void Save(string key)
    {
        if (_s != null) VisionCommands.SaveField(_s, key);
    }

    /// <summary>A control's label, ImGui id and settings key, built once (no per-frame strings).</summary>
    private sealed class Field
    {
        public readonly string Label, Id, Key;
        public Field(string label, string id, string key) { Label = label; Id = id; Key = key; }
        public static Field Slider(string name, string key) => new(name + ":", "##" + name, key);
        public static Field Color(string name, string key) => new(name, "##hex_" + name, key);
    }

    private static readonly Field SlopeColorF = Field.Color("Slope", VisionKeys.SlopeColor);
    private static readonly Field WaterColorF = Field.Color("Water", VisionKeys.WaterColor);
    private static readonly Field RadarColorF = Field.Color("Radar", VisionKeys.RadarColor);
    // Opacity is the colour's alpha byte: the slider saves the colour field.
    private static readonly Field SlopeOpacityF = Field.Slider("Slope opacity", VisionKeys.SlopeColor);
    private static readonly Field WaterOpacityF = Field.Slider("Water opacity", VisionKeys.WaterColor);
    private static readonly Field RadarOpacityF = Field.Slider("Radar ring opacity", VisionKeys.RadarColor);
    private static readonly Field RadarRangeF = Field.Slider("Radar range", VisionKeys.RadarRange);
    private static readonly Field RingThickF = Field.Slider("Ring thickness", VisionKeys.RingThick);
    private static readonly Field RingHeightF = Field.Slider("Ring height (m)", VisionKeys.RingHeight);
    private static readonly Field SlopeRadiusF = Field.Slider("Slope radius (cells)", VisionKeys.SlopeRadius);
    private static readonly Field SlopeFloorZF = Field.Slider("Slope floor Z", VisionKeys.SlopeFloorZ);
    private static readonly Field SlopeBiasF = Field.Slider("Slope height bias (m)", VisionKeys.SlopeBias);
    private static readonly Field WaterRadiusF = Field.Slider("Water radius (cells)", VisionKeys.WaterRadius);

    private static void Header(string text)
    {
        ImGuiNET.ImGui.Dummy(new Vector2(0, 4));
        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.UiBold11));
        SafeImGui.TextColored(HeaderColor, text);
        ImGuiNET.ImGui.PopFont();
    }

    /// <summary>Name | hex box | swatch. Commits on Enter or focus loss; bad input reverts. True while typing.</summary>
    private bool ColorRow(Field f, byte[] hex, ref uint argb)
    {
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        dl.AddText(new Vector2(p.X, p.Y + (22 - ImGuiNET.ImGui.GetFontSize()) * 0.5f), Text, f.Label);
        bool enter = TextBox(f.Id, hex, new Vector2(p.X + 48, p.Y), 92, "", out bool typing,
            ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.CharsHexadecimal | ImGuiInputTextFlags.CharsUppercase);
        bool done = ImGuiNET.ImGui.IsItemDeactivatedAfterEdit();
        if (enter || done)
        {
            if (VisionSettings.TryParseHex(Utf8(hex), out uint c)) { argb = c; Save(f.Key); }
            WriteUtf8(hex, argb.ToString("X8"));
        }
        // Swatch: AARRGGBB -> ImGui ABGR.
        uint abgr = (argb & 0xFF00FF00) | ((argb >> 16) & 0xFF) | ((argb & 0xFF) << 16);
        Vector2 sw = new(p.X + 146, p.Y + 3);
        dl.AddRectFilled(sw, sw + new Vector2(22, 16), abgr);
        dl.AddRect(sw, sw + new Vector2(22, 16), Mute);
        NextLine(p, 26);
        return typing;
    }

    /// <summary>
    /// The colour's alpha as 0-100 %: label, then the slider. Rewrites the alpha byte (RGB kept) and the hex
    /// box as it moves, saves the colour on release. Untouched, the colour is never rewritten. True while in use.
    /// </summary>
    private bool OpacitySlider(Field f, byte[] hex, ref uint argb)
    {
        ImGuiNET.ImGui.TextUnformatted(f.Label);
        float pct = ArgbOpacity.Percent(argb);
        if (ImGuiNET.ImGui.SliderFloat(f.Id, ref pct, 0f, 100f, "%.0f%%", ImGuiSliderFlags.AlwaysClamp))
        {
            argb = ArgbOpacity.WithPercent(argb, pct);
            WriteUtf8(hex, argb.ToString("X8"));
        }
        if (ImGuiNET.ImGui.IsItemDeactivatedAfterEdit()) Save(f.Key);
        SafeImGui.SetItemTooltip("0% = invisible, 100% = solid. Ctrl+click to type a value");
        return ImGuiNET.ImGui.IsItemActive();
    }

    /// <summary>Label, then the slider; saves on release (or after Ctrl+click typing). True while in use.</summary>
    private bool Slider(Field f, ref double value, double min, double max, string format)
    {
        ImGuiNET.ImGui.TextUnformatted(f.Label);
        float v = (float)value;
        ImGuiNET.ImGui.SliderFloat(f.Id, ref v, (float)min, (float)max, format, ImGuiSliderFlags.AlwaysClamp);
        value = v;
        if (ImGuiNET.ImGui.IsItemDeactivatedAfterEdit()) Save(f.Key);
        ImGuiNET.ImGui.SetItemTooltip("Ctrl+click to type a value");
        return ImGuiNET.ImGui.IsItemActive();
    }

    private bool IntSlider(Field f, ref int value, int min, int max)
    {
        ImGuiNET.ImGui.TextUnformatted(f.Label);
        ImGuiNET.ImGui.SliderInt(f.Id, ref value, min, max, "%d", ImGuiSliderFlags.AlwaysClamp);
        if (ImGuiNET.ImGui.IsItemDeactivatedAfterEdit()) Save(f.Key);
        ImGuiNET.ImGui.SetItemTooltip("Ctrl+click to type a value");
        return ImGuiNET.ImGui.IsItemActive();
    }
}
