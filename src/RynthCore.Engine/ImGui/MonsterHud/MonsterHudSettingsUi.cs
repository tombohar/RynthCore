// ============================================================================
//  RynthCore.Engine - ImGui/MonsterHud/MonsterHudSettingsUi.cs
//  The RynthVision overlay settings, drawn as two tabs of the Vision panel
//  (Panels/VisionFace.cs): "Nameplates" (monster plates, NPC / player / portal names,
//  debuff icons, your plate, look) and "Combat text" (numbers, kill burst, gains). Square toggle
//  dots and amber section headers; sliders save when the drag ends. Values
//  live engine-side in MonsterHudSettings. AC's render thread, inside the
//  face's content child.
// ============================================================================

using System;
using System.Numerics;
using ImGuiNET;

namespace RynthCore.Engine.ImGuiBackend.Hud;

internal static class MonsterHudSettingsUi
{
    private static readonly uint ToggleOn = C(0xFF33FF33), ToggleOff = C(0xFF334455);
    private static uint Amber => RynthTheme.Highlight;
    private static uint TextDim => RynthTheme.Text;
    private static uint Mute => RynthTheme.TextMuted;
    private static uint BtnBord => RynthTheme.Border;
    private static uint BtnFill => RynthTheme.Button;
    private static uint C(uint argb) => RynthTheme.Argb(argb);

    private static readonly string PortalNamesTip =
        "The portal's name above it, in the radar's portal colour (purple by default), at the NPC name size. " +
        $"The nearest {MonsterHudSettings.MaxPortalLabels}, counted on their own so NPC names never crowd them out; " +
        "click one to select the portal. Chat: /rv plates portals [on|off], /rv plates portals dist <yd>.";

    private static bool Loading(float w)
    {
        if (MonsterHudSettings.Loaded) return false;
        MonsterHudSettings.EnsureLoadQueued();
        Note("Loading...", w);
        return true;
    }

    /// <summary>The "Nameplates" tab body at the cursor, <paramref name="w"/> wide.</summary>
    public static void DrawNameplates(float w)
    {
        if (Loading(w)) return;
        bool changed = false;

        Section("Monster plates", w);
        changed |= Toggle("Show monster nameplates", ref MonsterHudSettings.Enabled,
            "Health bars and names over monsters in the world. Chat: /rv plates on|off.");
        changed |= Toggle("Click a plate to select", ref MonsterHudSettings.ClickToSelect,
            "Left-click a monster's plate (name or health bar) to select that monster, as clicking it in the world does. " +
            "Off: plates never take a click. Chat: /rv plates click.", sub: true);
        Note("Plates sit under every panel; a click on a plate selects its monster, every other click goes to the game.", w);
        bool all = MonsterHudSettings.Filter == PlateFilter.All;
        if (Toggle("All monsters in range", ref all, "Every attackable monster within the distance, nearest first.", radio: true))
        {
            MonsterHudSettings.Filter = PlateFilter.All;
            changed = true;
        }
        bool engaged = MonsterHudSettings.Filter == PlateFilter.Engaged;
        if (Toggle("Only target + attackers", ref engaged,
                "Your selected target, monsters that hit you in the last 20 s, and monsters that are hurt.", radio: true))
        {
            MonsterHudSettings.Filter = PlateFilter.Engaged;
            changed = true;
        }
        changed |= SliderF("Max distance (yd)", ref MonsterHudSettings.MaxDistance,
            MonsterHudSettings.MinDistance, MonsterHudSettings.MaxDistanceLimit, "%.0f", w);
        changed |= SliderI("Max plates", ref MonsterHudSettings.MaxPlates,
            MonsterHudSettings.MinPlates, MonsterHudSettings.MaxPlatesLimit, w);
        changed |= Toggle("Refresh others' health", ref MonsterHudSettings.RefreshOthersHealth,
            "The server only keeps the selected (or fought) monster's health up to date. On: the HUD also asks for " +
            "the other plates' health, one monster at a time - first the ones in the fight, then any it hasn't seen - " +
            "and hands the live feed straight back to your target. Two small queries per refresh. " +
            "Off: only your target's plate is live. Chat: /rv plates refresh [on|off], /rv plates refresh <seconds>.");
        if (MonsterHudSettings.RefreshOthersHealth)
            changed |= SliderF("Refresh every (s)", ref MonsterHudSettings.HealthRefreshSec,
                HealthProbe.MinIntervalSec, HealthProbe.MaxIntervalSec, "%.1f s", w);

        Section("On the plate", w);
        changed |= Toggle("Name", ref MonsterHudSettings.ShowNames, null);
        changed |= Toggle("HP numbers", ref MonsterHudSettings.ShowHpNumbers,
            "\"142 / 190\" when the monster's max HP is known (appraised), else a percentage. Hidden until its health has been seen.");
        changed |= Toggle("Level badge", ref MonsterHudSettings.ShowLevel,
            "Coloured against your level: grey, green, white, orange, red. Needs an appraisal.");
        changed |= Toggle("Weak-to element", ref MonsterHudSettings.ShowWeakness,
            "What the monster takes most damage from, from RynthAi's Damage data (needs RynthAi).");
        changed |= Toggle("Distance", ref MonsterHudSettings.ShowDistance, null);
        changed |= SizeSlider("Bar width", ref MonsterHudSettings.MonsterBarWidth, MonsterHudSettings.MinBarWidth, MonsterHudSettings.MaxBarWidth, w,
            "How wide a monster's health bar is (1.00x = the default). On top of Look > Scale.");
        changed |= SizeSlider("Bar height", ref MonsterHudSettings.MonsterBarHeight, MonsterHudSettings.MinBarHeight, MonsterHudSettings.MaxBarHeight, w,
            "How tall (thick) a monster's health bar is (1.00x = the default). HP numbers need a bar at least 9 px tall.");
        changed |= SizeSlider("Text size", ref MonsterHudSettings.MonsterTextSize, MonsterHudSettings.MinTextSize, MonsterHudSettings.MaxTextSize, w,
            "The monster plate's text: name, level badge, HP numbers, weak-to pill and distance (1.00x = the default).");

        Section("Names over NPCs, players and portals", w);
        changed |= Toggle("NPC names", ref MonsterHudSettings.NpcNames,
            "The name over vendors, quest givers, town criers and other NPCs: yellow, vendors gold (the radar's colours). " +
            "Name only, no health bar; click one to select it. Chat: /rv plates npcs.");
        changed |= SliderF("NPC name distance (yd)", ref MonsterHudSettings.NpcMaxDistance,
            MonsterHudSettings.MinNpcDistance, MonsterHudSettings.MaxNpcDistanceLimit, "%.0f", w);
        changed |= SliderI("Max names", ref MonsterHudSettings.MaxNpcLabels,
            MonsterHudSettings.MinNpcLabels, MonsterHudSettings.MaxNpcLabelsLimit, w);
        changed |= SizeSlider("NPC name size", ref MonsterHudSettings.NpcTextSize, MonsterHudSettings.MinTextSize, MonsterHudSettings.MaxTextSize, w,
            "Text size of the names over NPCs and vendors, and over portals (1.00x = the default). Names have no bar.");
        changed |= Toggle("Player names", ref MonsterHudSettings.PlayerNames,
            "The name over other players too (blue), within the same distance and count. Chat: /rv plates players.");
        changed |= Toggle("Player health bars", ref MonsterHudSettings.PlayerBars,
            "A health bar under other players' names (and their name, even with Player names off). The game only tells you a player's health " +
            "once you select them or they share it (fellowship), so a bar shows from then on; others keep just the name. Chat: /rv plates playerbars.");
        changed |= SizeSlider("Player name size", ref MonsterHudSettings.PlayerTextSize, MonsterHudSettings.MinTextSize, MonsterHudSettings.MaxTextSize, w,
            "Text size of the names over other players (1.00x = the default). Names have no bar.");
        changed |= Toggle("Portal names", ref MonsterHudSettings.PortalNames, PortalNamesTip);
        changed |= SliderF("Portal name distance (yd)", ref MonsterHudSettings.PortalMaxDistance,
            MonsterHudSettings.MinPortalDistance, MonsterHudSettings.MaxPortalDistanceLimit, "%.0f", w);
        Note("Names count apart from the monster plates, so they never push one out; the nearest win.", w);

        Section("Debuffs", w);
        changed |= Toggle("Debuff icons", ref MonsterHudSettings.ShowDebuffs,
            "Your own debuffs on each monster (vulnerabilities, Imperil, Magic Yield, attribute and skill debuffs...), " +
            "one icon per kind with a line that runs out with the spell. From your \"You cast ... on ...\" lines. Chat: /rv plates debuffs.");
        changed |= Toggle("Debuffed by others", ref MonsterHudSettings.ShowOthersDebuff,
            "A small marker when an appraisal shows a lowered attribute or vital that none of your own debuffs explains. " +
            "It only updates when the monster is appraised (your combat target is appraised often).", sub: true);
        changed |= PercentSlider("Debuff icon size", ref MonsterHudSettings.MonsterDebuffIconSize,
            MonsterHudSettings.MinDebuffIconSize, MonsterHudSettings.MaxDebuffIconSize, w,
            "How big the debuff icons on monster plates are (100% = the default): the icons, their countdown lines, " +
            "the stack digits and \"+N\". On its own: the text size doesn't change it. On top of Look > Scale. " +
            "Chat: /rv plates debuffs size 50-200.");

        Section("Your plate", w);
        changed |= Toggle("Show your plate", ref MonsterHudSettings.SelfPlate,
            "Your own health, stamina and mana bars: fixed on screen, or following your character. Chat: /rv plates self.");
        changed |= Toggle("Numbers on your bars", ref MonsterHudSettings.SelfNumbers, "\"cur / max\" inside each bar (taller bars).", sub: true);
        changed |= Toggle("Your name", ref MonsterHudSettings.SelfName, null, sub: true);
        changed |= Toggle("Debuffs on you", ref MonsterHudSettings.SelfDebuffs,
            "Icons under your mana bar for harmful spells on you (vulnerabilities, Imperil, Magic Yield, attribute and skill debuffs...), " +
            "the monster plates' icons: one per kind, a line that runs out with the spell, blinking in its last 5 seconds, \"+N\" when more than fit. " +
            "Only shows while you have one. Chat: /rv plates self debuffs.", sub: true);
        changed |= PercentSlider("Your debuff icon size", ref MonsterHudSettings.SelfDebuffIconSize,
            MonsterHudSettings.MinDebuffIconSize, MonsterHudSettings.MaxDebuffIconSize, w,
            "How big the icons under your bars are (100% = the default), apart from the monster plates' and from " +
            "Your text size. On top of Look > Scale. Chat: /rv plates self debuffs size 50-200.");
        changed |= SizeSlider("Your bar width", ref MonsterHudSettings.SelfBarWidth, MonsterHudSettings.MinBarWidth, MonsterHudSettings.MaxBarWidth, w,
            "How wide your health, stamina and mana bars are (1.00x = the default).");
        changed |= SizeSlider("Your bar height", ref MonsterHudSettings.SelfBarHeight, MonsterHudSettings.MinBarHeight, MonsterHudSettings.MaxBarHeight, w,
            "How tall (thick) each of your bars is (1.00x = the default). The numbers need a bar at least 8 px tall.");
        changed |= SizeSlider("Your text size", ref MonsterHudSettings.SelfTextSize, MonsterHudSettings.MinTextSize, MonsterHudSettings.MaxTextSize, w,
            "Your plate's text: the cur / max numbers and your name (1.00x = the default).");
        // The plate's shading, piece by piece (your plate only; the monster plates keep theirs).
        changed |= Toggle("Dark track behind the bars", ref MonsterHudSettings.SelfBarTrack,
            "The dark background and thin black edge behind each of your bars. Off: only the coloured fill shows.", sub: true);
        changed |= Toggle("Damage trail", ref MonsterHudSettings.SelfBarChip,
            "The pale gold strip that shows what a drop just took, then drains away.", sub: true);
        changed |= Toggle("Text outline", ref MonsterHudSettings.SelfTextOutline,
            "The thin black outline around your numbers and your name.", sub: true);
        changed |= Toggle("Band behind the numbers", ref MonsterHudSettings.SelfNumberBand,
            "The dark band behind the cur / max numbers (with Numbers on your bars).", sub: true);

        bool fixedOn = MonsterHudSettings.SelfPlacement == SelfPlacement.Fixed;
        if (Toggle("Fixed on screen", ref fixedOn,
                "The bars stay where you put them on the screen, so they don't move with your character. " +
                "Unlock to drag them. Chat: /rv plates self fixed.", radio: true, sub: true))
        {
            MonsterHudSettings.SelfPlacement = SelfPlacement.Fixed;
            changed = true;
        }
        if (fixedOn)
        {
            bool unlocked = !MonsterHudSettings.SelfLocked;
            bool locked = MonsterHudSettings.SelfLocked;
            if (Toggle("Lock position", ref locked,
                    "Locked, the plate can't be grabbed or clicked, so play never moves it. " +
                    "Untick it, then drag the plate (it shows a frame) and tick it again. Also in Settings > UI > Vitals. " +
                    "Chat: /rv plates self lock on|off.", indent: 32))
            {
                MonsterHudSettings.SetSelfLocked(locked);
                changed = true;
            }
            if (unlocked)
                Note("Unlocked: drag your plate anywhere on the screen, then lock it again.", w, indent: 32);
            ImGuiNET.ImGui.Dummy(new Vector2(0, 2));
            ImGuiNET.ImGui.SetCursorScreenPos(ImGuiNET.ImGui.GetCursorScreenPos() + new Vector2(32, 0));
            if (SmallButton("Reset position"))
                MonsterHudSettings.ResetSelfPosition();   // saves
            if (ImGuiNET.ImGui.IsItemHovered())
                ImGuiNET.ImGui.SetTooltip("Back to the default spot, just under your character. Chat: /rv plates self reset.");
        }

        bool follow = MonsterHudSettings.SelfPlacement == SelfPlacement.Follow;
        if (Toggle("Follow your character", ref follow,
                "The bars ride along with your character in the world (they move as it moves). Chat: /rv plates self follow.",
                radio: true, sub: true))
        {
            MonsterHudSettings.SelfPlacement = SelfPlacement.Follow;
            changed = true;
        }
        if (follow)
        {
            changed |= SelfPositionPick("Over your head", SelfPlatePosition.Above);
            changed |= SelfPositionPick("Under your feet", SelfPlatePosition.Below);
            changed |= SelfPositionPick("Beside you, left", SelfPlatePosition.Left);
            changed |= SelfPositionPick("Beside you, right", SelfPlatePosition.Right);
            changed |= Toggle("Hide in first person", ref MonsterHudSettings.SelfHideFirstPerson,
                "When the camera is at your head the plate would sit mid-screen; hide it then.", indent: 32);
        }

        Section("Look", w);
        changed |= SliderF("Scale", ref MonsterHudSettings.Scale, MonsterHudSettings.MinScale, MonsterHudSettings.MaxScale, "%.2f", w);
        changed |= Toggle("Keep on screen", ref MonsterHudSettings.KeepOnScreen,
            "When the camera closes in (walls), plates and numbers stay pinned to the screen edge instead of leaving it. Chat: /rv plates onscreen.");
        changed |= SliderF("Opacity", ref MonsterHudSettings.Opacity, MonsterHudSettings.MinOpacity, MonsterHudSettings.MaxOpacity, "%.2f", w);
        changed |= SliderF("Height lift (m)", ref MonsterHudSettings.HeightLift, MonsterHudSettings.MinLift, MonsterHudSettings.MaxLift, "%.1f", w);
        changed |= Toggle("Fade and shrink with distance", ref MonsterHudSettings.FadeWithDistance, null);

        ImGuiNET.ImGui.Dummy(new Vector2(0, 8));
        if (SmallButton("Reset bar, text and icon sizes"))
        {
            MonsterHudSettings.ResetSizes();   // saves
            changed = false;
        }
        if (ImGuiNET.ImGui.IsItemHovered())
            SafeImGui.SetTooltip("Every plate type's bar width, bar height and text size back to 1.00x, and both debuff icon sizes to 100%.");
        ImGuiNET.ImGui.Dummy(new Vector2(0, 2));
        if (SmallButton("Reset all to defaults"))
        {
            MonsterHudSettings.ResetToDefaults();
            changed = false;   // ResetToDefaults saved already
        }

        if (changed)
            MonsterHudSettings.Save();
    }

    /// <summary>The "Combat text" tab body at the cursor, <paramref name="w"/> wide.</summary>
    public static void DrawCombatText(float w)
    {
        if (Loading(w)) return;
        bool changed = false;

        Section("Combat numbers", w);
        changed |= Toggle("Show combat numbers", ref MonsterHudSettings.Numbers,
            "Floating numbers over monsters and over you. Chat: /rv plates numbers.");
        changed |= Toggle("Damage you deal", ref MonsterHudSettings.NumDealt,
            "White to red by size; gold and bigger with \"!\" on a crit. A killing blow is estimated from the last health seen.", sub: true);
        changed |= Toggle("Damage you take", ref MonsterHudSettings.NumTaken, "Red numbers over your head.", sub: true);
        changed |= Toggle("Heals", ref MonsterHudSettings.NumHeals,
            "Green \"+N\" when you or a monster gains more health than a regen tick.", sub: true);
        changed |= Toggle("Kill burst", ref MonsterHudSettings.NumKills, "Expanding rings where a monster you damaged dies.", sub: true);
        changed |= Toggle("Stamina and mana restores", ref MonsterHudSettings.NumRestores,
            "\"+N stam\" (amber) and \"+N mana\" (blue) over you when a spell, potion or kit restores more than a regen tick.", sub: true);
        changed |= SliderF("Number size", ref MonsterHudSettings.NumSize, MonsterHudSettings.MinNumSize, MonsterHudSettings.MaxNumSize, "%.2f", w);
        changed |= SliderF("Number duration", ref MonsterHudSettings.NumTime, MonsterHudSettings.MinNumTime, MonsterHudSettings.MaxNumTime, "%.2fx", w);

        Section("Gains", w);
        changed |= Toggle("Show gains", ref MonsterHudSettings.Gains,
            "Rising text over your head when you earn. Chat: /rv plates gains.");
        changed |= Toggle("Experience", ref MonsterHudSettings.GainXp, "\"+12,345 XP\"", sub: true);
        changed |= Toggle("Luminance", ref MonsterHudSettings.GainLum, "\"+500 Lum\"", sub: true);
        changed |= Toggle("Radiance", ref MonsterHudSettings.GainRadiance,
            "\"+10 Radiance\", from Aelrynth's \"You gain N Radiance.\" lines.", sub: true);
        changed |= SliderF("Gain text size", ref MonsterHudSettings.GainSize, MonsterHudSettings.MinNumSize, MonsterHudSettings.MaxNumSize, "%.2f", w);
        changed |= SliderF("Gain text duration", ref MonsterHudSettings.GainTime, MonsterHudSettings.MinNumTime, MonsterHudSettings.MaxNumTime, "%.2fx", w);
        Note("Spending experience or luminance never shows.", w);

        if (changed)
            MonsterHudSettings.Save();
    }

    // ── Rows ─────────────────────────────────────────────────────────────

    /// <summary>A square dot + label; the row toggles (a radio row only ever turns on; a sub row is indented).</summary>
    private static bool SelfPositionPick(string label, SelfPlatePosition pos)
    {
        bool on = MonsterHudSettings.SelfPosition == pos;
        if (!Toggle(label, ref on, "Where your plate sits. Chat: /rv plates self position above|below|left|right.", radio: true, indent: 32))
            return false;
        MonsterHudSettings.SelfPosition = pos;
        return true;
    }

    private static bool Toggle(string label, ref bool value, string? tip, bool radio = false, bool sub = false, float indent = 0)
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        ImGuiNET.ImGui.Dummy(new Vector2(0, 2));
        if (sub && indent <= 0) indent = 16;
        if (indent > 0) ImGuiNET.ImGui.SetCursorScreenPos(ImGuiNET.ImGui.GetCursorScreenPos() + new Vector2(indent, 0));
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        float labelW = ImGuiNET.ImGui.CalcTextSize(label).X;
        ImGuiNET.ImGui.PushID(label);
        bool clicked = ImGuiNET.ImGui.InvisibleButton("##t", new Vector2(18 + labelW, 16));
        ImGuiNET.ImGui.PopID();
        if (ImGuiNET.ImGui.IsItemHovered())
        {
            ImGuiNET.ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (tip != null) SafeImGui.SetTooltip(tip);
        }
        if (clicked) value = radio || !value;
        dl.AddRectFilled(p + new Vector2(0, 2), p + new Vector2(12, 14), value ? ToggleOn : ToggleOff, 2);
        dl.AddText(p + new Vector2(18, (16 - ImGuiNET.ImGui.GetFontSize()) * 0.5f), TextDim, label);
        ImGuiNET.ImGui.Dummy(new Vector2(0, 2));
        return clicked;
    }

    private static bool SliderF(string label, ref float value, float min, float max, string fmt, float w)
    {
        BeginSliderRow(label, w);
        bool changed = ImGuiNET.ImGui.SliderFloat("##v", ref value, min, max, fmt, ImGuiSliderFlags.AlwaysClamp);
        return EndSliderRow(changed);
    }

    /// <summary>A size multiplier slider ("1.00x" = the default look) with a tooltip. Labels are the ids: keep them unique.</summary>
    private static bool SizeSlider(string label, ref float value, float min, float max, float w, string tip)
    {
        BeginSliderRow(label, w);
        bool changed = ImGuiNET.ImGui.SliderFloat("##v", ref value, min, max, "%.2fx", ImGuiSliderFlags.AlwaysClamp);
        if (ImGuiNET.ImGui.IsItemHovered()) SafeImGui.SetTooltip(tip);
        return EndSliderRow(changed);
    }

    /// <summary>A percent slider ("100%" = the default look) with a tooltip. Labels are the ids: keep them unique.</summary>
    private static bool PercentSlider(string label, ref int value, int min, int max, float w, string tip)
    {
        BeginSliderRow(label, w);
        bool changed = ImGuiNET.ImGui.SliderInt("##v", ref value, min, max, "%d%%", ImGuiSliderFlags.AlwaysClamp);
        if (ImGuiNET.ImGui.IsItemHovered()) SafeImGui.SetTooltip(tip);
        return EndSliderRow(changed);
    }

    private static bool SliderI(string label, ref int value, int min, int max, float w)
    {
        BeginSliderRow(label, w);
        bool changed = ImGuiNET.ImGui.SliderInt("##v", ref value, min, max, "%d", ImGuiSliderFlags.AlwaysClamp);
        return EndSliderRow(changed);
    }

    private static void BeginSliderRow(string label, float w)
    {
        ImGuiNET.ImGui.Dummy(new Vector2(0, 2));
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        ImGuiNET.ImGui.GetWindowDrawList().AddText(p + new Vector2(0, 2), TextDim, label);
        float labelCol = Math.Min(130, w * 0.45f);
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(p.X + labelCol, p.Y));
        ImGuiNET.ImGui.SetNextItemWidth(Math.Max(60, w - labelCol));
        ImGuiNET.ImGui.PushID(label);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBg, BtnFill);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, BtnBord);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
    }

    /// <summary>True once the edit is finished (drag released or value typed), so saves aren't per-step.</summary>
    private static bool EndSliderRow(bool changed)
    {
        bool done = changed && !ImGuiNET.ImGui.IsItemActive() || ImGuiNET.ImGui.IsItemDeactivatedAfterEdit();
        ImGuiNET.ImGui.PopStyleVar();
        ImGuiNET.ImGui.PopStyleColor(2);
        ImGuiNET.ImGui.PopID();
        ImGuiNET.ImGui.Dummy(new Vector2(0, 2));
        return done;
    }

    private static void Section(string text, float w)
    {
        ImGuiNET.ImGui.Dummy(new Vector2(0, 6));
        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.UiBold11));
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        ImGuiNET.ImGui.GetWindowDrawList().AddText(p, Amber, text);
        ImGuiNET.ImGui.Dummy(new Vector2(w, ImGuiNET.ImGui.GetFontSize() + 2));
        ImGuiNET.ImGui.PopFont();
    }

    private static void Note(string text, float w, float indent = 0)
    {
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, Mute);
        if (indent > 0)
        {
            ImGuiNET.ImGui.SetCursorPosX(ImGuiNET.ImGui.GetCursorPosX() + indent);
            w -= indent;
        }
        ImGuiNET.ImGui.PushTextWrapPos(ImGuiNET.ImGui.GetCursorPosX() + w);
        ImGuiNET.ImGui.TextUnformatted(text);
        ImGuiNET.ImGui.PopTextWrapPos();
        ImGuiNET.ImGui.PopStyleColor();
    }

    private static bool SmallButton(string label)
    {
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Button, BtnFill);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, BtnBord);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(8, 3));
        bool clicked = ImGuiNET.ImGui.Button(label);
        ImGuiNET.ImGui.PopStyleVar(2);
        ImGuiNET.ImGui.PopStyleColor(2);
        return clicked;
    }
}
