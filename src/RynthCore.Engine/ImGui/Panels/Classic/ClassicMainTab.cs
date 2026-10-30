// ============================================================================
//  RynthCore.Engine - ImGui/Panels/Classic/ClassicMainTab.cs
//  The Classic look's Main tab: everything on VTank's options page, in our own
//  groups and words, plus what the bot is doing. Top to bottom:
//    Macro          Stop/Start, Rebuff all, Cancel; what the bot is doing and
//                   the meta state; the settings profile picker
//    Systems        combat, buffing, navigation, looting, meta: a gem each
//                   (the subsystem toggles) with a live line
//    Ranges         monster range, ring range, approach, follow/nav min
//    Behavior       autostack, autocram, peace mode / rebuff when idle,
//                   navigation first, looting first, rare corpses only,
//                   manage fellowship
//    Magic and pets dispel items / self, M. charges when off, summon pets,
//                   pet range, pet min monsters
//    Recharge       health, stamina and mana meters with the heal / restam /
//                   get-mana trigger marked (drag it) and the top-off level
//  Settings are the Settings panel's rows (ClassicSettings), saved the same
//  way; the macro, subsystems, rebuff and profile go through RynthAiCommands.
//  AC's render thread only.
// ============================================================================

using System;
using System.Numerics;
using ImGuiNET;
using RynthCore.Engine.UI;
using RynthCore.Engine.UI.Data;
using static RynthCore.Engine.ImGuiBackend.Panels.Classic.ClassicKit;

namespace RynthCore.Engine.ImGuiBackend.Panels.Classic;

internal sealed class ClassicMainTab : IClassicTab
{
    public string Name => "Main";

    private readonly ClassicSettings _settings = new();

    private readonly ClassicSettings.NumberRow[] _ranges =
    {
        new("Monster Range", "Monster range"),
        new("Ring Range", "Ring range"),
        new("Approach Range", "Approach"),
        new(SettingsSchema.NavPointReachLabel, "Follow/nav min"),
    };

    private static readonly (string Schema, string Label)[] BehaviorLeft =
    {
        ("Enable Autostack", "Autostack"), ("Auto Cram", "Autocram"),
        ("Peace Mode When Idle", "Peace mode when idle"), ("Rebuff When Idle", "Rebuff when idle"),
    };

    private static readonly (string Schema, string Label)[] BehaviorRight =
    {
        ("Boost Nav Priority", "Navigation first"), ("Boost Loot Priority", "Looting first"),
        ("Loot Only Rare Corpses", "Rare corpses only"), ("Auto Fellowship Management", "Manage fellowship"),
    };

    private static readonly (string Schema, string Label)[] MagicLeft =
    {
        ("Use Dispel Items", "Dispel items"), ("Cast Dispel Self", "Dispel self"), ("M. Charges When Off", "M. charges when off"),
    };

    private readonly ClassicSettings.NumberRow _petRange = new("Pet Range", "Pet range");
    private readonly ClassicSettings.NumberRow _petMin = new("Pet Min Monsters", "Min. monsters");

    // Recharge: (trigger row, top-off row, label).
    private static readonly (string At, string Top, string Label, Vital Vital)[] Recharge =
    {
        ("Heal At", "Top HP", "Heal at", Vital.Health),
        ("Re-stam At", "Top Stam", "Stam at", Vital.Stamina),
        ("Get Mana At", "Top Mana", "Mana at", Vital.Mana),
    };

    // Schema rows, looked up once (SettingsSchema.Find isn't for per-frame use).
    private readonly SettingRow?[] _behaviorLeft = Rows(BehaviorLeft), _behaviorRight = Rows(BehaviorRight), _magicLeft = Rows(MagicLeft);
    private readonly SettingRow? _summonPets = ClassicSettings.Row("Summon Pets");
    private readonly SettingRow?[] _rechargeAt = Array.ConvertAll(Recharge, r => ClassicSettings.Row(r.At));
    private readonly SettingRow?[] _rechargeTop = Array.ConvertAll(Recharge, r => ClassicSettings.Row(r.Top));

    private static SettingRow?[] Rows((string Schema, string Label)[] list) => Array.ConvertAll(list, x => ClassicSettings.Row(x.Schema));

    private int _dragging = -1;
    private double _dragValue;

    public void Shown() => _settings.Subscribe();

    public void Hidden() => _settings.Unsubscribe();

    public float Draw(ClassicContext ctx, Vector2 o, float w)
    {
        _settings.Sync();
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        RynthAiSnapshot raw = ctx.Raw;
        RynthAiView? view = ctx.View;
        float y = o.Y;
        bool busy = false;

        // ── Macro ──
        Header(dl, new Vector2(o.X, y), w, "Macro", view?.BotActivityText ?? "Idle");
        y += Px(23);
        {
            float runW = Px(118), rebuffW = Px(104);
            bool run = raw.MacroRunning;
            if (Button("##cm_run", run ? "Stop macro" : "Start macro", new Vector2(o.X, y), runW, run ? ButtonColor.Red : ButtonColor.Blue))
                RynthAiCommands.ToggleMacro();
            SafeImGui.SetItemTooltip(run ? "Macro running. Click to stop it." : "Macro stopped. Click to start it.");
            float x = o.X + runW + Px(6);
            if (Button("##cm_rebuff", "Rebuff all", new Vector2(x, y), rebuffW, ButtonColor.Blue))
                RynthAiCommands.ForceRebuff();
            SafeImGui.SetItemTooltip("Force rebuff: recast every buff now.");
            x += rebuffW + Px(6);
            if (Plate("##cm_cancel", "Cancel", new Vector2(x, y + Px(7.5f))))
                RynthAiCommands.CancelForceRebuff();
            SafeImGui.SetItemTooltip("Cancel a force rebuff");
            x += Px(52);
            float lineH = Px(14);
            Print(dl, new Vector2(x, y + Px(2)), "Doing", Dim, 10);
            TextClip(dl, x + Measure("Doing ", 10).X, y + Px(2) - Px(1), lineH, o.X + w, view?.BotActivityText ?? "Idle",
                run ? Red : Dim, 10);
            Print(dl, new Vector2(x, y + Px(17)), "Meta", Dim, 10);
            TextClip(dl, x + Measure("Meta ", 10).X, y + Px(17) - Px(1), lineH, o.X + w, view?.MetaStateText ?? "Default", Gold, 10);
        }
        y += Px(36);
        {
            float rowH = Px(22), labelW = Px(52);
            TextMid(dl, o.X + Px(3), y, rowH, "Profile", Text);
            var ddPos = new Vector2(o.X + labelW + Px(6), y + (rowH - Px(19)) * 0.5f);
            float ddW = w - labelW - Px(6);
            if (Dropdown("##cm_profile", view?.ProfileText ?? raw.SelectedProfile, ddPos, ddW, ctx.Popup.IsOpen))
                ctx.Popup.Open(ddPos + new Vector2(0, Px(19)), ddW, raw.Profiles, raw.SelectedProfileIdx,
                    i => RynthAiCommands.SelectProfile(3, i));
            SafeImGui.SetItemTooltip("Settings profile");
            y += rowH;
        }

        // ── Systems ──
        y += Px(10);
        Header(dl, new Vector2(o.X, y), w, "Systems");
        y += Px(23);
        for (int i = 0; i < 5; i++)
        {
            bool on = ClassicDashboard.SubsystemOn(raw, i);
            var pos = new Vector2(o.X, y);
            if (Gem("##cm_sys" + i, ClassicDashboard.SubsystemNames[i], pos, w, on))
                RynthAiCommands.SetSubsystemEnabled(i, !on);
            SafeImGui.SetItemTooltip(ClassicDashboard.SubsystemNames[i] + (on ? " on. Click to turn it off." : " off. Click to turn it on."));
            TextRight(dl, o.X + w - Px(3), y, Px(18), on ? SystemLine(i, view, raw) : "off", on ? Amber : Faint, 10);
            y += Px(18);
        }

        if (_settings.Data == null)
        {
            y += Px(10);
            TextMid(dl, o.X + Px(3), y, Px(20), UiSources.Settings.Current == null ? "Waiting for RynthAi…" : "Loading…", Dim);
            return y + Px(20) - o.Y;
        }

        // ── Ranges ──
        y += Px(10);
        Header(dl, new Vector2(o.X, y), w, "Ranges");
        y += Px(23);
        float colW = (w - Px(8)) / 2;
        for (int i = 0; i < _ranges.Length; i++)
        {
            var pos = new Vector2(o.X + (i % 2) * (colW + Px(8)), y + (i / 2) * Px(20));
            busy |= _settings.DrawNumber(_ranges[i], pos, colW);
        }
        y += Px(40);

        // ── Behavior ──
        y += Px(10);
        Header(dl, new Vector2(o.X, y), w, "Behavior");
        y += Px(23);
        for (int i = 0; i < BehaviorLeft.Length; i++)
        {
            _settings.DrawGem(_behaviorLeft[i], BehaviorLeft[i].Label, new Vector2(o.X, y + i * Px(18)), colW);
            _settings.DrawGem(_behaviorRight[i], BehaviorRight[i].Label, new Vector2(o.X + colW + Px(8), y + i * Px(18)), colW);
        }
        y += Px(18) * BehaviorLeft.Length;

        // ── Magic and pets ──
        y += Px(10);
        Header(dl, new Vector2(o.X, y), w, "Magic and pets");
        y += Px(23);
        for (int i = 0; i < MagicLeft.Length; i++)
            _settings.DrawGem(_magicLeft[i], MagicLeft[i].Label, new Vector2(o.X, y + i * Px(19)), colW, 19);
        float rx = o.X + colW + Px(8);
        _settings.DrawGem(_summonPets, "Summon pets", new Vector2(rx, y), colW, 19);
        busy |= _settings.DrawNumber(_petRange, new Vector2(rx, y + Px(19)), colW, 32, 19);
        busy |= _settings.DrawNumber(_petMin, new Vector2(rx, y + Px(38)), colW, 32, 19);
        y += Px(19) * 3;

        // ── Recharge ──
        y += Px(10);
        Header(dl, new Vector2(o.X, y), w, "Recharge", "drag a bar to move its trigger");
        y += Px(23);
        VitalView[] vitals = { view?.Health ?? default, view?.Stamina ?? default, view?.Mana ?? default };
        for (int i = 0; i < Recharge.Length; i++)
        {
            busy |= RechargeRow(i, vitals[i], new Vector2(o.X, y), w);
            y += Px(25);
        }

        // ── More ──
        y += Px(6);
        if (Plate("##cm_more", "More", new Vector2(o.X + w - Px(46), y)))
            ctx.OpenTab(7);
        SafeImGui.SetItemTooltip("Every setting, in the Tuning tab");
        TextMid(dl, o.X + Px(3), y, Px(17), "Every other setting is in Tuning.", Faint, 10);
        y += Px(17);

        _settings.Busy = busy;
        return y - o.Y;
    }

    /// <summary>A system's live line: what it's working on, from the dashboard snapshot.</summary>
    private static string SystemLine(int i, RynthAiView? view, RynthAiSnapshot raw) => i switch
    {
        0 => raw.CurrentTargetId != 0 ? (view?.TargetHeadline ?? raw.TargetLabel) + " · " + (int)MathF.Round(Math.Clamp(raw.TargetHealthPercent, 0, 1) * 100) + "%" : "no target",
        1 => "profile " + (view?.BuffText ?? "Built-in"),
        2 => view?.NavText is { } nav && nav != "None" ? nav : "no route loaded",
        3 => view?.LootText is { } loot && loot != "None" ? loot : "no loot profile",
        _ => "state " + (view?.MetaStateText ?? "Default"),
    };

    /// <summary>One recharge row: heart, meter with the trigger (drag) and top-off marks, "Heal at 60%". True while dragged.</summary>
    private bool RechargeRow(int i, VitalView v, Vector2 o, float w)
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        var (atLabel, topLabel, label, vital) = Recharge[i];
        SettingRow? at = _rechargeAt[i], top = _rechargeTop[i];
        float rowH = Px(25), labW = Px(70);
        Heart(dl, o, vital);
        float mx = o.X + Px(31), mw = w - Px(31) - labW - Px(6), my = o.Y + (rowH - Px(16)) * 0.5f;
        Meter(dl, new Vector2(mx, my), mw, vital, v.Fraction);
        TextMid(dl, mx + mw * 0.5f - Measure("100%", 10).X * 0.5f, my, Px(16), (int)MathF.Round(v.Fraction * 100) + "%", White, 10);

        bool dragging = false;
        double atValue = at != null ? _settings.Get(at) : 0;
        if (at != null)
        {
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(mx, my - Px(3)));
            ImGuiNET.ImGui.InvisibleButton("##cm_trig" + i, new Vector2(mw, Px(22)));
            if (ImGuiNET.ImGui.IsItemActive())
            {
                float f = Math.Clamp((ImGuiNET.ImGui.GetMousePos().X - mx) / Math.Max(1, mw), 0f, 1f);
                _dragValue = Math.Clamp(Math.Round(f * 100 / 5) * 5, Math.Max(at.Min, 5), Math.Min(at.Max, 95));
                _dragging = i;
                dragging = true;
                atValue = _dragValue;
            }
            if (ImGuiNET.ImGui.IsItemDeactivated() && _dragging == i)
            {
                _dragging = -1;
                if (_dragValue != _settings.Get(at)) _settings.Set(at, _dragValue);
                atValue = _dragValue;
            }
            SafeImGui.SetItemTooltip(label + " " + (int)atValue + "%: drag along the bar to move it.");
            float tx = mx + mw * (float)(atValue / 100);
            dl.AddRectFilled(new Vector2(tx - Px(1), my - Px(3)), new Vector2(tx + Px(1), my + Px(19)), Gold);
            dl.AddRect(new Vector2(tx - Px(2), my - Px(3)), new Vector2(tx + Px(2), my + Px(19)), Black);
        }
        if (top != null)
        {
            float tx = mx + mw * (float)(_settings.Get(top) / 100);
            dl.AddRectFilled(new Vector2(tx - Px(0.75f), my - Px(1)), new Vector2(tx + Px(0.75f), my + Px(17)), FaceKit.Faded(White));
        }
        float lx = o.X + w - labW;
        TextMid(dl, lx, o.Y, rowH, label, Text, 10);
        TextMid(dl, lx + Measure(label + " ", 10).X, o.Y, rowH, (int)atValue + "%", Gold, 10);
        return dragging;
    }
}
