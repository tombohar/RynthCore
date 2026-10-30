// ============================================================================
//  RynthCore.Engine - ImGui/Panels/SkillsFace.Progression.cs
//  The Skills panel's Progression tab, shown on ILT-like worlds only:
//    - XP planner: exact XP for the next N raises of every attribute and vital,
//      from the client's XP tables (raising stays on the Attributes & Vitals tab).
//    - Attribute raiser: infinite attributes past the retail cap. RynthILT reads the
//      server's costs ("/xp all") and raises with "/attr", by hand or on a timer from
//      unassigned XP, in the order and mode set here. Laid out like UB's Leaftide XP
//      tab: "+1 Level" / "+10 Levels" buttons labelled with their (projected) cost.
//    - Augmentations: UB's Augs tab - Refresh current (/aug), Current / Target / Inc /
//      Luminance / Coins with a totals row, Reset targets, Copy Discord / In-Game.
//    - Augmentations / Enlightenment: the ILT Hub's planners (RynthILT's since
//      2026-10-13; RynthAi's before). The plugin owns the math and the saved inputs
//      (UiSources.Progression snapshot); edits and actions go back as "prog ..."
//      commands (IltCommands.Progression). /enl and arming
//      auto-enlighten ask here first; the server still shows its own dialog.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using ImGuiNET;
using RynthCore.Engine.UI;
using RynthCore.Engine.UI.Data;
using static RynthCore.Engine.ImGuiBackend.Panels.FaceKit;

namespace RynthCore.Engine.ImGuiBackend.Panels;

internal sealed partial class SkillsFace
{
    private const string ProgConfirmId = "##skills_prog_confirm";

    private int _planLevels = 10;          // XP planner: raises to price per row
    private bool _progSubscribed;

    // The confirm the Progression tab asked for; the command is sent only on its Yes button.
    private string _progConfirmTitle = "", _progConfirmText = "", _progConfirmYes = "", _progConfirmCommand = "";
    private bool _progConfirmOpenRequested;

    // Set by ShowProgression (any panel, AC thread); the tab is selected once RynthILT's snapshot
    // says it exists (the first poll lands a step after the panel opens).
    private static bool _progressionRequested;

    /// <summary>RynthILT reports an ILT-like world (its ILT Hub exists and the server has ILT features).</summary>
    private static bool ProgressionTabVisible => UiSources.Progression.Current?.Value.Available == true;

    /// <summary>Opens the Skills panel (if closed) on the Progression tab. AC thread.</summary>
    public static void ShowProgression()
    {
        _progressionRequested = true;
        if (!ImGuiPanelHost.IsOpen(Title)) PanelRouter.Toggle(Title);
    }

    /// <summary>Draw: applies a pending ShowProgression once the tab exists.</summary>
    private void ApplyProgressionRequest()
    {
        if (!_progressionRequested || !ProgressionTabVisible) return;
        _progressionRequested = false;
        _tab = 2;
    }

    private void ProgressionShown()
    {
        if (_progSubscribed) return;
        _progSubscribed = true;
        UiSources.Progression.Subscribe();
        UiSources.Progression.RequestRefresh();
    }

    private void ProgressionHidden()
    {
        _progressionRequested = false;
        if (!_progSubscribed) return;
        _progSubscribed = false;
        UiSources.Progression.Unsubscribe();
    }

    /// <summary>Sends "prog &lt;value&gt;" to RynthILT (an older RynthAi takes it too); the snapshot re-polls after.</summary>
    private static void SendProg(string value)
    {
        IltCommands.Progression(value);
        UiSources.Progression.RequestRefreshAfterPluginTick();
    }

    private void AskProgConfirm(string title, string text, string yes, string command)
    {
        _progConfirmTitle = title;
        _progConfirmText = text;
        _progConfirmYes = yes;
        _progConfirmCommand = command;
        _progConfirmOpenRequested = true;
    }

    // ── Body ────────────────────────────────────────────────────────────

    private void DrawProgression(float w)
    {
        ProgressionInfo? info = UiSources.Progression.Current?.Value;
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(6, 4));
        try
        {
            DrawXpPlanner(w);
            ImGuiNET.ImGui.Dummy(new Vector2(0, 6));
            if (info == null || !info.Available)
            {
                Message("Augmentations and Enlightenment need RynthILT's ILT features on an ILT world.", w);
                return;
            }
            DrawAttrRaiser(info.Attr, w);
            ImGuiNET.ImGui.Dummy(new Vector2(0, 6));
            DrawAugPlanner(info.Aug, w);
            ImGuiNET.ImGui.Dummy(new Vector2(0, 6));
            DrawEnlPlanner(info.Enl, w);
            ImGuiNET.ImGui.Dummy(new Vector2(0, 8));
        }
        finally
        {
            ImGuiNET.ImGui.PopStyleVar();
        }
    }

    private void DrawXpPlanner(float w)
    {
        Section("XP planner", w);
        if (_snap == null || _dat == null || _attributes.Count == 0)
        {
            Message("Waiting for your character's data...", w);
            return;
        }
        ImGuiNET.ImGui.Indent(8);
        long have = _snap.UnassignedXp;
        Label($"Unassigned XP: {have:N0}", have > 0 ? Teal : Mute);
        ImGuiNET.ImGui.SetNextItemWidth(160);
        ImGuiNET.ImGui.SliderInt("Raises to plan##prog_lv", ref _planLevels, 1, 100);

        if (ImGuiNET.ImGui.BeginTable("##prog_xp", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp,
                new Vector2(w - 16, 0)))
        {
            ImGuiNET.ImGui.TableSetupColumn("Stat");
            ImGuiNET.ImGui.TableSetupColumn("Base", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGuiNET.ImGui.TableSetupColumn("Next raise", ImGuiTableColumnFlags.WidthFixed, 100);
            ImGuiNET.ImGui.TableSetupColumn($"Next {_planLevels}", ImGuiTableColumnFlags.WidthFixed, 120);
            ImGuiNET.ImGui.TableHeadersRow();
            foreach (Row r in _attributes) PlannerRow(r, have);
            foreach (Row r in _vitals) PlannerRow(r, have);
            ImGuiNET.ImGui.EndTable();
        }
        Label("Exact costs from the client's XP tables. Raise on the Attributes & Vitals tab"
              + (ProgressionTabVisible ? ", or past the cap with the Attribute raiser below." : "."), Dim);
        ImGuiNET.ImGui.Unindent(8);
    }

    private void PlannerRow(Row r, long have)
    {
        ImGuiNET.ImGui.TableNextRow();
        ImGuiNET.ImGui.TableNextColumn(); ImGuiNET.ImGui.TextUnformatted(r.Name);
        ImGuiNET.ImGui.TableNextColumn(); ImGuiNET.ImGui.TextUnformatted(r.Base.ToString(CultureInfo.InvariantCulture));
        uint[]? table = r.Table;
        int left = table == null ? 0 : Math.Max(0, table.Length - 1 - (int)Math.Min(r.Ranks, (uint)int.MaxValue));
        ImGuiNET.ImGui.TableNextColumn();
        if (table == null || left == 0) { Label(table == null ? "-" : "max", Dim); ImGuiNET.ImGui.TableNextColumn(); Label("-", Dim); return; }
        long one = SkillDat.CostOf(table, r.Ranks, r.Spent, 1);
        Label(one >= 0 ? one.ToString("N0", CultureInfo.InvariantCulture) : "-", one >= 0 && one <= have ? Teal : Dim);
        ImGuiNET.ImGui.TableNextColumn();
        int n = Math.Min(_planLevels, left);
        long many = SkillDat.CostOf(table, r.Ranks, r.Spent, n);
        string text = many >= 0 ? many.ToString("N0", CultureInfo.InvariantCulture) : "-";
        if (n < _planLevels) text += $" ({n} to max)";
        Label(text, many >= 0 && many <= have ? Teal : Dim);
    }

    private static readonly string[] AttrModes =
    {
        "Priority - top of the order first",
        "Round robin - one level each, in order",
        "Cheapest next level first",
    };

    private string? _attrKeepEdit;   // reserve box being typed in (null = show RynthAi's value)
    private int? _attrEveryEdit;     // interval box being typed in

    private void DrawAttrRaiser(AttrRaiser? a, float w)
    {
        Section("Attribute raiser (infinite attributes)", w);
        ImGuiNET.ImGui.Indent(8);
        float wrap = ImGuiNET.ImGui.GetCursorPosX() + w - 24;
        ImGuiNET.ImGui.PushTextWrapPos(wrap);
        try
        {
            if (a == null) { Label("Update RynthAi to use the attribute raiser.", Mute); return; }
            if (a.Off) { Label(a.Reason, Mute); return; }

            Label(a.XpHeader.Length > 0 ? a.XpHeader : a.Unassigned, Teal);
            if (a.XpHeader.Length > 0 && a.Unassigned.Contains("spendable", StringComparison.Ordinal)) Label(a.Unassigned, Dim);
            Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
            if (a.Running)
            {
                if (Button("##attr_stop", "Stop", p, new Vector2(90, 24), Text, StopBg)) SendProg("attrstop");
            }
            else if (Button("##attr_run", "Raise now", p, new Vector2(110, 24), Text, StartBg, border: Teal))
                SendProg("attrrun");
            if (Button("##attr_costs", "Refresh XP", new Vector2(p.X + 116, p.Y), new Vector2(100, 24), Text, BtnFill, enabled: !a.Running))
                SendProg("attrcosts");
            ImGuiNET.ImGui.SetItemTooltip("Re-reads every next-level cost with /xp all.");
            if (a.Updated.Length > 0)
            {
                ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(p.X + 224, p.Y + 4));
                Label("Last updated: " + a.Updated, Dim);
            }
            NextLine(p, 28);
            if (a.Status.Length > 0) SafeImGui.TextWrapped(a.Status);
            if (a.LastRun.Length > 0) Label("Last run: " + a.LastRun, Dim);

            ImGuiNET.ImGui.Separator();
            bool auto = a.Auto;
            if (ImGuiNET.ImGui.Checkbox("Auto-raise from unassigned XP##attr_auto", ref auto))
                SendProg("attrauto " + (auto ? "on" : "off"));
            ImGuiNET.ImGui.SameLine();
            // Commit on Enter, the +/- buttons or focus loss (not on every keystroke: each send
            // restarts the auto-raise timer).
            int every = _attrEveryEdit ?? a.Every;
            ImGuiNET.ImGui.SetNextItemWidth(90);
            bool everyCommit = ImGuiNET.ImGui.InputInt("every (min)##attr_every", ref every, 1, 10, ImGuiInputTextFlags.EnterReturnsTrue);
            bool everyActive = ImGuiNET.ImGui.IsItemActive();
            if (everyCommit || ImGuiNET.ImGui.IsItemDeactivatedAfterEdit())
            {
                _attrEveryEdit = null;
                if (every != a.Every) SendProg("attrevery " + Math.Clamp(every, 1, 1440).ToString(CultureInfo.InvariantCulture));
            }
            else _attrEveryEdit = everyActive ? every : null;
            if (a.Auto && a.Next.Length > 0) Label($"Next auto-raise: {a.Next}", Dim);

            int mode = Math.Clamp(a.Mode, 0, AttrModes.Length - 1);
            ImGuiNET.ImGui.SetNextItemWidth(260);
            if (ImGuiNET.ImGui.Combo("Raise order##attr_mode", ref mode, AttrModes, AttrModes.Length))
                SendProg("attrmode " + mode.ToString(CultureInfo.InvariantCulture));

            // The box holds the exact reserve (KeepFull); it is sent only when the text changed, so an
            // Enter on an untouched box can't round the reserve.
            string shownKeep = a.KeepFull.Length > 0 ? a.KeepFull : a.Keep;
            string keep = _attrKeepEdit ?? shownKeep;
            ImGuiNET.ImGui.SetNextItemWidth(140);
            bool enter = ImGuiNET.ImGui.InputTextWithHint("Keep in reserve (XP)##attr_keep", "0, 500m, 2b", ref keep, 32u,
                ImGuiInputTextFlags.EnterReturnsTrue);
            bool keepActive = ImGuiNET.ImGui.IsItemActive();
            if (enter || ImGuiNET.ImGui.IsItemDeactivatedAfterEdit())
            {
                _attrKeepEdit = null;
                if (!string.Equals(keep.Trim(), shownKeep, StringComparison.Ordinal))
                    SendProg("attrkeep " + keep.Replace(" ", "", StringComparison.Ordinal).Replace(",", "", StringComparison.Ordinal));
            }
            else _attrKeepEdit = keepActive ? keep : null;
            ImGuiNET.ImGui.SetItemTooltip("Unassigned XP the raiser never spends. Accepts k / m / b / t suffixes.");

            DrawAttrTable(a, w);
            Label("Auto: the stats Raise now / auto-raise may spend on; arrows set the order. "
                  + "+1 / +10 raise by hand (red = not enough unassigned XP). "
                  + "+10 is an estimate (7.7% dearer per level); the server charges the real cost. " + a.CostsAge + ".", Dim);
        }
        finally
        {
            ImGuiNET.ImGui.PopTextWrapPos();
            ImGuiNET.ImGui.Unindent(8);
        }
    }

    /// <summary>
    /// UB Leaftide XP tab layout: Auto tick, stat, base, then "+1 Level" / "+10 Levels" buttons
    /// labelled with what they cost, then the priority arrows.
    /// </summary>
    private void DrawAttrTable(AttrRaiser a, float w)
    {
        if (!ImGuiNET.ImGui.BeginTable("##prog_attr", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp,
                new Vector2(w - 16, 0)))
            return;
        ImGuiNET.ImGui.TableSetupColumn("Auto", ImGuiTableColumnFlags.WidthFixed, 34);
        ImGuiNET.ImGui.TableSetupColumn("Stat");
        ImGuiNET.ImGui.TableSetupColumn("Base", ImGuiTableColumnFlags.WidthFixed, 56);
        ImGuiNET.ImGui.TableSetupColumn("+1 Level", ImGuiTableColumnFlags.WidthFixed, 84);
        ImGuiNET.ImGui.TableSetupColumn("+10 Levels", ImGuiTableColumnFlags.WidthFixed, 84);
        ImGuiNET.ImGui.TableSetupColumn("Order", ImGuiTableColumnFlags.WidthFixed, 48);
        ImGuiNET.ImGui.TableHeadersRow();
        for (int i = 0; i < a.Rows.Length; i++)
        {
            AttrRow r = a.Rows[i];
            ImGuiNET.ImGui.PushID("attr_" + r.Key);
            ImGuiNET.ImGui.TableNextRow();

            ImGuiNET.ImGui.TableNextColumn();
            bool on = r.On;
            if (ImGuiNET.ImGui.Checkbox("##on", ref on)) SendProg($"attron {r.Key} {(on ? "on" : "off")}");

            ImGuiNET.ImGui.TableNextColumn();
            Label($"{i + 1}. {r.Label}", r.On ? Text : Mute);

            ImGuiNET.ImGui.TableNextColumn();
            int clientBase = ClientBase(r);
            Label(clientBase > 0 ? clientBase.ToString("N0", CultureInfo.InvariantCulture)
                  : r.Base >= 0 ? r.Base.ToString("N0", CultureInfo.InvariantCulture) : "-", Text);

            // Older RynthAi sends no cost10 / afford1: plain buttons, the server decides.
            bool rich = r.Cost10.Length > 0;
            ImGuiNET.ImGui.TableNextColumn();
            RaiseButton(a, r, 1, rich ? r.Cost : "+1", rich ? r.Afford1 : true, r.CostFull, rich);
            ImGuiNET.ImGui.TableNextColumn();
            RaiseButton(a, r, 10, rich ? r.Cost10 : "+10", rich ? r.Afford10 : true, r.Cost10Full, rich);

            ImGuiNET.ImGui.TableNextColumn();
            ImGuiNET.ImGui.BeginDisabled(i == 0);
            if (ImGuiNET.ImGui.ArrowButton("##up", ImGuiDir.Up)) SendProg($"attrmove {r.Key} up");
            ImGuiNET.ImGui.EndDisabled();
            ImGuiNET.ImGui.SameLine(0, 2);
            ImGuiNET.ImGui.BeginDisabled(i == a.Rows.Length - 1);
            if (ImGuiNET.ImGui.ArrowButton("##down", ImGuiDir.Down)) SendProg($"attrmove {r.Key} down");
            ImGuiNET.ImGui.EndDisabled();
            ImGuiNET.ImGui.PopID();
        }
        ImGuiNET.ImGui.EndTable();
    }

    /// <summary>
    /// One by-hand raise button filling its cell. Unknown cost ("?") stays clickable (the server
    /// checks); a known cost over the unassigned XP is red and disabled.
    /// </summary>
    private static void RaiseButton(AttrRaiser a, AttrRow r, int levels, string label, bool afford, string exact, bool rich)
    {
        bool known = rich && r.Known;
        bool enabled = !a.Running && (!known || afford);
        uint fg = !known ? Text : afford ? Teal : Red;
        Vector2 pos = ImGuiNET.ImGui.GetCursorScreenPos();
        float width = Math.Max(40f, ImGuiNET.ImGui.GetContentRegionAvail().X);
        if (Button("##raise" + levels, label, pos, new Vector2(width, 20), fg, BtnFill, enabled, border: known && afford ? Teal : 0))
            SendProg($"attrraise {r.Key} {levels}");
        string what = $"{r.Label} +{levels} level{(levels > 1 ? "s" : "")}";
        string tip = !known ? $"{what}: cost not read yet (Refresh XP)."
                   : levels > 1 ? $"{what}: about {exact} XP (estimate)."
                   : $"{what}: {exact} XP.";
        if (known && !afford) tip += " Not enough unassigned XP.";
        if (a.Running) tip += " Wait for the raise run to finish.";
        SafeImGui.SetItemTooltip(tip);
    }

    /// <summary>The client's own base value for a raiser row (0 when the snapshot has none).</summary>
    private int ClientBase(AttrRow r)
    {
        List<Row> rows = r.Vital ? _vitals : _attributes;
        foreach (Row row in rows)
            if (row.Name.Equals(r.Label, StringComparison.OrdinalIgnoreCase)) return row.Base;
        return 0;
    }

    private void DrawAugPlanner(AugPlanner a, float w)
    {
        Section("Augmentations", w);
        ImGuiNET.ImGui.Indent(8);
        try
        {
            if (a.Off) { Label("/aug is not available on this server.", Mute); return; }

            // UB Augs tab buttons: Refresh Current, Reset Target, Copy Discord, Copy In-Game.
            Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
            float x = p.X;
            if (Button("##aug_refresh", "Refresh current", new Vector2(x, p.Y), new Vector2(120, 22), Text, StartBg, border: Teal))
                SendProg("augload");
            ImGuiNET.ImGui.SetItemTooltip("Reads your augmentation levels with /aug (also done once when this tab first opens).");
            x += 124;
            if (Button("##aug_reset", "Reset targets", new Vector2(x, p.Y), new Vector2(104, 22), Text, BtnFill))
                SendProg("augreset");
            ImGuiNET.ImGui.SetItemTooltip("Sets every target back to the current level.");
            x += 108;
            if (Button("##aug_discord", "Copy Discord", new Vector2(x, p.Y), new Vector2(100, 22), Text, BtnFill, enabled: a.CopyDiscord.Length > 0))
                ImGuiNET.ImGui.SetClipboardText(a.CopyDiscord);
            ImGuiNET.ImGui.SetItemTooltip("Copies the plan as a Discord code-block table.");
            x += 104;
            if (Button("##aug_ingame", "Copy in-game", new Vector2(x, p.Y), new Vector2(100, 22), Text, BtnFill, enabled: a.CopyIngame.Length > 0))
                ImGuiNET.ImGui.SetClipboardText(a.CopyIngame);
            ImGuiNET.ImGui.SetItemTooltip("Copies your current levels as one chat line.");
            NextLine(p, 26);
            Label("Status: " + a.Status, Dim);

            if (ImGuiNET.ImGui.BeginTable("##prog_augs", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp,
                    new Vector2(w - 16, 0)))
            {
                ImGuiNET.ImGui.TableSetupColumn("Aug");
                ImGuiNET.ImGui.TableSetupColumn("Current", ImGuiTableColumnFlags.WidthFixed, 56);
                ImGuiNET.ImGui.TableSetupColumn("Target", ImGuiTableColumnFlags.WidthFixed, 124);
                ImGuiNET.ImGui.TableSetupColumn("Inc", ImGuiTableColumnFlags.WidthFixed, 44);
                ImGuiNET.ImGui.TableSetupColumn("Luminance", ImGuiTableColumnFlags.WidthFixed, 76);
                ImGuiNET.ImGui.TableSetupColumn("Coins", ImGuiTableColumnFlags.WidthFixed, 56);
                ImGuiNET.ImGui.TableHeadersRow();
                foreach (AugRow row in a.Rows)
                {
                    ImGuiNET.ImGui.TableNextRow();
                    ImGuiNET.ImGui.TableNextColumn(); ImGuiNET.ImGui.TextUnformatted(row.Label);
                    ImGuiNET.ImGui.TableNextColumn(); ImGuiNET.ImGui.TextUnformatted(row.Current.ToString("N0", CultureInfo.InvariantCulture));
                    ImGuiNET.ImGui.TableNextColumn();
                    DrawAugTarget(row);
                    int inc = row.Increase > 0 ? row.Increase : Math.Max(0, row.Target - row.Current);
                    ImGuiNET.ImGui.TableNextColumn(); Label(inc > 0 ? inc.ToString("N0", CultureInfo.InvariantCulture) : "-", inc > 0 ? Teal : Dim);
                    ImGuiNET.ImGui.TableNextColumn(); ImGuiNET.ImGui.TextUnformatted(row.Lum);
                    ImGuiNET.ImGui.TableNextColumn(); ImGuiNET.ImGui.TextUnformatted(row.Coins > 0 ? row.Coins.ToString("N0", CultureInfo.InvariantCulture) : "-");
                }
                if (a.TotalLum.Length > 0)
                {
                    ImGuiNET.ImGui.TableNextRow();
                    ImGuiNET.ImGui.TableNextColumn(); Label("TOTALS", Amber);
                    ImGuiNET.ImGui.TableNextColumn(); Label(a.TotalCurrent.ToString("N0", CultureInfo.InvariantCulture), Amber);
                    ImGuiNET.ImGui.TableNextColumn(); Label(a.TotalTarget.ToString("N0", CultureInfo.InvariantCulture), Amber);
                    ImGuiNET.ImGui.TableNextColumn(); Label(a.TotalIncrease.ToString("N0", CultureInfo.InvariantCulture), Amber);
                    ImGuiNET.ImGui.TableNextColumn(); Label(a.TotalLum, Amber);
                    ImGuiNET.ImGui.TableNextColumn(); Label(a.TotalCoins.ToString("N0", CultureInfo.InvariantCulture), Amber);
                }
                ImGuiNET.ImGui.EndTable();
            }

            // Lum per coin commits on Enter / focus loss, not per keystroke.
            string shownLpc = a.LumPerCoin.ToString(CultureInfo.InvariantCulture);
            string lpc = _augLpcEdit ?? shownLpc;
            ImGuiNET.ImGui.SetNextItemWidth(140);
            bool lpcEnter = ImGuiNET.ImGui.InputTextWithHint("Lum per e-coin##prog_lpc", "0 = don't price coins", ref lpc, 24u,
                ImGuiInputTextFlags.EnterReturnsTrue);
            bool lpcActive = ImGuiNET.ImGui.IsItemActive();
            if (lpcEnter || ImGuiNET.ImGui.IsItemDeactivatedAfterEdit())
            {
                _augLpcEdit = null;
                string clean = lpc.Replace(" ", "", StringComparison.Ordinal).Replace(",", "", StringComparison.Ordinal);
                if (!string.Equals(clean, shownLpc, StringComparison.Ordinal)) SendProg("lumpercoin " + clean);
            }
            else _augLpcEdit = lpcActive ? lpc : null;
            ImGuiNET.ImGui.SetItemTooltip("Luminance one Enlightened Coin costs you. Accepts k / m / b / t suffixes.");

            if (a.CoinsLine.Length > 0)
            {
                Label(a.CoinsLine, Text);
                Label(a.LumToBuy, Text);
            }
            else
            {
                Label(a.Total, Amber);
                if (a.Short.Length > 0) Label(a.Short, Text);
            }
            Label(a.Banked, Dim);
            if (a.Config != null) DrawAugCostSettings(a.Config, w);
        }
        finally
        {
            ImGuiNET.ImGui.Unindent(8);
        }
    }

    private static readonly string[] AugTierNames = { "Below S1", "From S1", "From S2", "From S3", "From S4" };

    // Cost-settings boxes being typed in, by box id (committed on Enter / focus loss).
    private readonly Dictionary<string, int> _augCfgIntEdit = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _augCfgDblEdit = new(StringComparer.Ordinal);

    /// <summary>
    /// Collapsible editor for this world's aug cost rules (RynthAi saves them per world). The
    /// defaults are UB's InfiniteLeaftide model; Reset puts a changed world back on them.
    /// </summary>
    private void DrawAugCostSettings(AugCostConfig cfg, float w)
    {
        ImGuiNET.ImGui.Dummy(new Vector2(0, 2));
        string head = $"Cost settings for {cfg.World}" + (cfg.Custom ? " (custom)" : " (InfiniteLeaftide defaults)");
        if (!ImGuiNET.ImGui.CollapsingHeader(head + "##aug_cfg")) return;

        ImGuiNET.ImGui.PushTextWrapPos(ImGuiNET.ImGui.GetCursorPosX() + w - 24);
        Label("Level i costs (Base + (i - 1) x Base x Growth% / 100) x the multiplier of its tier. "
              + "A tier starts at its S level (UB's InfiniteLeaftide model). Changes apply to this world only.", Dim);
        ImGuiNET.ImGui.PopTextWrapPos();

        // Tier multipliers, one box each.
        Label("Tier multipliers", Text);
        for (int t = 0; t < AugTierNames.Length && t < cfg.Multipliers.Length; t++)
        {
            if (t > 0) ImGuiNET.ImGui.SameLine();
            ImGuiNET.ImGui.BeginGroup();
            Label(AugTierNames[t], Mute);
            ImGuiNET.ImGui.SetNextItemWidth(62);
            CfgDouble($"mult{t}", cfg.Multipliers[t], v => $"augmult {t} {v.ToString("R", CultureInfo.InvariantCulture)}");
            ImGuiNET.ImGui.EndGroup();
        }

        // Per-aug rules.
        if (ImGuiNET.ImGui.BeginTable("##aug_cfg_rows", 9, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp,
                new Vector2(w - 16, 0)))
        {
            ImGuiNET.ImGui.TableSetupColumn("Aug");
            ImGuiNET.ImGui.TableSetupColumn("Base", ImGuiTableColumnFlags.WidthFixed, 74);
            ImGuiNET.ImGui.TableSetupColumn("Growth%", ImGuiTableColumnFlags.WidthFixed, 54);
            ImGuiNET.ImGui.TableSetupColumn("S1", ImGuiTableColumnFlags.WidthFixed, 46);
            ImGuiNET.ImGui.TableSetupColumn("S2", ImGuiTableColumnFlags.WidthFixed, 46);
            ImGuiNET.ImGui.TableSetupColumn("S3", ImGuiTableColumnFlags.WidthFixed, 46);
            ImGuiNET.ImGui.TableSetupColumn("S4", ImGuiTableColumnFlags.WidthFixed, 46);
            ImGuiNET.ImGui.TableSetupColumn("Cap", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGuiNET.ImGui.TableSetupColumn("Coins", ImGuiTableColumnFlags.WidthFixed, 42);
            ImGuiNET.ImGui.TableHeadersRow();
            foreach (AugCostRule r in cfg.Rows)
            {
                ImGuiNET.ImGui.TableNextRow();
                ImGuiNET.ImGui.TableNextColumn(); ImGuiNET.ImGui.TextUnformatted(r.Label);
                ImGuiNET.ImGui.TableNextColumn(); CfgInt($"{r.Key}.base", (int)Math.Min(r.Base, int.MaxValue), $"augcfg {r.Key} base");
                ImGuiNET.ImGui.TableNextColumn(); CfgDouble($"{r.Key}.pct", r.Percent, v => $"augcfg {r.Key} pct {v.ToString("R", CultureInfo.InvariantCulture)}");
                ImGuiNET.ImGui.TableNextColumn(); CfgInt($"{r.Key}.s1", r.S1, $"augcfg {r.Key} s1");
                ImGuiNET.ImGui.TableNextColumn(); CfgInt($"{r.Key}.s2", r.S2, $"augcfg {r.Key} s2");
                ImGuiNET.ImGui.TableNextColumn(); CfgInt($"{r.Key}.s3", r.S3, $"augcfg {r.Key} s3");
                ImGuiNET.ImGui.TableNextColumn(); CfgInt($"{r.Key}.s4", r.S4, $"augcfg {r.Key} s4");
                ImGuiNET.ImGui.TableNextColumn(); CfgInt($"{r.Key}.cap", r.Cap, $"augcfg {r.Key} cap");
                ImGuiNET.ImGui.SetItemTooltip("Highest target level; 0 = no limit.");
                ImGuiNET.ImGui.TableNextColumn(); CfgInt($"{r.Key}.coins", r.Coins, $"augcfg {r.Key} coins");
                ImGuiNET.ImGui.SetItemTooltip("Enlightened Coins per level.");
            }
            ImGuiNET.ImGui.EndTable();
        }

        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        if (Button("##aug_cfg_reset", "Reset to InfiniteLeaftide defaults", p, new Vector2(230, 22), Text, BtnFill, enabled: cfg.Custom))
            SendProg("augcfgreset");
        ImGuiNET.ImGui.SetItemTooltip("Drops this world's changes; it goes back to UB's InfiniteLeaftide rules.");
        NextLine(p, 26);
    }

    /// <summary>Integer box that sends "<paramref name="verb"/> value" on Enter or focus loss.</summary>
    private void CfgInt(string id, int value, string verb)
    {
        int v = _augCfgIntEdit.TryGetValue(id, out int typing) ? typing : value;
        ImGuiNET.ImGui.SetNextItemWidth(-1);
        bool enter = ImGuiNET.ImGui.InputInt("##cfg_" + id, ref v, 0, 0, ImGuiInputTextFlags.EnterReturnsTrue);
        bool active = ImGuiNET.ImGui.IsItemActive();
        if (enter || ImGuiNET.ImGui.IsItemDeactivatedAfterEdit())
        {
            _augCfgIntEdit.Remove(id);
            if (v != value) SendProg($"{verb} {Math.Max(0, v).ToString(CultureInfo.InvariantCulture)}");
        }
        else if (active) _augCfgIntEdit[id] = v;
        else _augCfgIntEdit.Remove(id);
    }

    /// <summary>Decimal box (the caller sets its width) that sends <paramref name="command"/>(value) on Enter or focus loss.</summary>
    private void CfgDouble(string id, double value, Func<double, string> command)
    {
        double v = _augCfgDblEdit.TryGetValue(id, out double typing) ? typing : value;
        if (!id.StartsWith("mult", StringComparison.Ordinal)) ImGuiNET.ImGui.SetNextItemWidth(-1);
        bool enter = ImGuiNET.ImGui.InputDouble("##cfg_" + id, ref v, 0, 0, "%.6g", ImGuiInputTextFlags.EnterReturnsTrue);
        bool active = ImGuiNET.ImGui.IsItemActive();
        if (enter || ImGuiNET.ImGui.IsItemDeactivatedAfterEdit())
        {
            _augCfgDblEdit.Remove(id);
            if (Math.Abs(v - value) > 1e-9 && double.IsFinite(v)) SendProg(command(Math.Max(0, v)));
        }
        else if (active) _augCfgDblEdit[id] = v;
        else _augCfgDblEdit.Remove(id);
    }

    private string? _augLpcEdit;                                              // Lum per coin box being typed in
    private readonly Dictionary<string, int> _augTargetEdit = new(StringComparer.Ordinal);   // target boxes being typed in

    /// <summary>
    /// Target cell: -/+ step one level (Ctrl: 100), typing commits on Enter or focus loss so a
    /// half-typed number never reaches RynthAi. RynthAi clamps to current..cap.
    /// </summary>
    private void DrawAugTarget(AugRow row)
    {
        int tgt = _augTargetEdit.TryGetValue(row.Key, out int typing) ? typing : row.Target;
        ImGuiNET.ImGui.SetNextItemWidth(-1);
        bool commit = ImGuiNET.ImGui.InputInt("##prog_tgt_" + row.Key, ref tgt, 1, 100, ImGuiInputTextFlags.EnterReturnsTrue);
        bool active = ImGuiNET.ImGui.IsItemActive();
        if (commit || ImGuiNET.ImGui.IsItemDeactivatedAfterEdit())
        {
            _augTargetEdit.Remove(row.Key);
            int clamped = Math.Clamp(tgt, row.Current, Math.Max(row.Cap, row.Current));
            if (clamped != row.Target) SendProg($"augtarget {row.Key} {clamped.ToString(CultureInfo.InvariantCulture)}");
        }
        else if (active) _augTargetEdit[row.Key] = tgt;
        else _augTargetEdit.Remove(row.Key);
        SafeImGui.SetItemTooltip($"Target {row.Label} level (up to {row.Cap.ToString("N0", CultureInfo.InvariantCulture)}).");
    }

    private void DrawEnlPlanner(EnlPlanner e, float w)
    {
        Section("Enlightenment", w);
        ImGuiNET.ImGui.Indent(8);
        float wrap = ImGuiNET.ImGui.GetCursorPosX() + w - 24;   // window-local x
        ImGuiNET.ImGui.PushTextWrapPos(wrap);
        try
        {
            if (e.Off) { Label("/enl is not available on this server.", Mute); return; }
            Label(e.Header, Text);
            Label(e.Next, Teal);

            int target = e.Target;
            ImGuiNET.ImGui.SetNextItemWidth(100);
            if (ImGuiNET.ImGui.InputInt("Plan to level##prog_enlt", ref target))
                SendProg("enltarget " + Math.Max(e.Level + 1, target).ToString(CultureInfo.InvariantCulture));
            foreach (string line in e.Plan) SafeImGui.BulletText(line);

            DrawEnlCalculator(e, w);

            int coinsPerToken = e.CoinsPerToken;
            ImGuiNET.ImGui.SetNextItemWidth(100);
            if (ImGuiNET.ImGui.InputInt("Coins per token (shop price)##prog_cpt", ref coinsPerToken))
                SendProg("coinspertoken " + Math.Max(0, coinsPerToken).ToString(CultureInfo.InvariantCulture));
            if (e.TokensShort.Length > 0) Label(e.TokensShort, Dim);
            if (!string.IsNullOrEmpty(e.Ecoins)) Label(e.Ecoins, Text);
            if (!string.IsNullOrEmpty(e.EcoinsLum)) Label(e.EcoinsLum, Dim);

            ImGuiNET.ImGui.Separator();
            if (e.Blockers.Length == 0) Label("Client-side checks pass.", Green);
            foreach (string b in e.Blockers) Label(b, Amber);
            Label(e.ServerChecks, Dim);

            Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
            if (Button("##prog_enl", "Enlighten now...", p, new Vector2(150, 24), Text, BtnFill, border: Teal))
                AskProgConfirm("Enlighten",
                    "Send /enl now?\n\nEnlightening resets your level and wipes unassigned XP.\n"
                    + "The server will show its own Yes/No dialog - you must click Yes there.",
                    "Send /enl", "enlighten");
            NextLine(p, 28);

            ImGuiNET.ImGui.Separator();
            bool auto = e.Auto;
            if (ImGuiNET.ImGui.Checkbox("Auto-enlighten when ready##prog_auto", ref auto))
            {
                if (auto)
                    AskProgConfirm("Arm auto-enlighten",
                        "Auto-enlighten will send /enl whenever the checks pass (peace mode, level, materials).\n"
                        + "You still have to click Yes in the AC dialog each time.\n\nArm it for this session?",
                        "Arm", "autoenl on");
                else
                    SendProg("autoenl off");
            }
            if (e.Auto && !e.Armed)
            {
                ImGuiNET.ImGui.SameLine();
                if (ImGuiNET.ImGui.SmallButton("Arm for this session...##prog_arm"))
                    AskProgConfirm("Arm auto-enlighten", "Arm auto-enlighten for this session?", "Arm", "autoenl arm");
            }
            bool spend = e.SpendFirst;
            if (ImGuiNET.ImGui.Checkbox("Spend unassigned XP on attributes first##prog_spend", ref spend))
                SendProg("spendfirst " + (spend ? "on" : "off"));
            int every = e.CheckEvery;
            ImGuiNET.ImGui.SetNextItemWidth(100);
            if (ImGuiNET.ImGui.InputInt("Check every (s)##prog_every", ref every))
                SendProg("checkevery " + Math.Max(10, every).ToString(CultureInfo.InvariantCulture));
            Label(e.Armed ? "Armed." : "Disarmed.", e.Armed ? Amber : Dim);
            if (e.Status.Length > 0) SafeImGui.TextWrapped(e.Status);
        }
        finally
        {
            ImGuiNET.ImGui.PopTextWrapPos();
            ImGuiNET.ImGui.Unindent(8);
        }
    }

    // ── Enl calculator ──────────────────────────────────────────────────
    //
    // RynthILT's AppendEnlCalculatorJson (Silentkelpie's rynth PR 6 drew it first): per-level cost rows, still-need /
    // have materials and the 301-325 sigil craft chain. Read-only: it only draws the Progression snapshot already
    // subscribed by ProgressionShown, so it adds no subscription and no per-frame work beyond the table rows
    // (row text is formatted once per snapshot, in the DTOs). A snapshot without the fields (older RynthILT) has
    // empty arrays and nothing here is drawn.
    //
    // Width: the three tables are w - 16 wide inside the Indent(8) of DrawEnlPlanner, with a stretch label
    // column and narrow fixed number columns scaled down below the retail width (300); text in a cell wraps at
    // the cell instead of running off the panel. Level rows show a preview (12), "Show all" lists up to 100 in a
    // table that scrolls on its own.

    private const float EnlRetailTableWidth = 284f;   // retail panel width (300) - 16
    private bool _enlShowAllLevels;
    private string _enlToggleLabel = "";
    private int _enlToggleTotal = -1;
    private bool _enlToggleAll;

    private void DrawEnlCalculator(EnlPlanner e, float w)
    {
        if (!e.HasCalculator) return;

        ImGuiNET.ImGui.Dummy(new Vector2(0, 2));
        if (!ImGuiNET.ImGui.CollapsingHeader("Enl calculator##prog_enl_calc", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        float tw = Math.Max(120f, w - 16);
        float k = Math.Min(1f, tw / EnlRetailTableWidth);   // fixed columns shrink on a narrower panel
        DrawEnlLevels(e.Levels, tw, k);
        DrawEnlMaterials(e.Materials, tw, k);
        DrawEnlChain(e, tw, k);
    }

    private void DrawEnlLevels(EnlLevelRow[] rows, float tw, float k)
    {
        if (rows.Length == 0) return;
        bool canExpand = EnlCalculatorView.CanExpand(rows.Length);
        bool all = canExpand && _enlShowAllLevels;
        int shown = EnlCalculatorView.VisibleLevels(rows.Length, all);

        ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp;
        var size = new Vector2(tw, 0);
        if (all)
        {
            // Own scroll area, a preview's worth of rows tall (+ header): the long list never pushes the panel.
            flags |= ImGuiTableFlags.ScrollY;
            size.Y = (ImGuiNET.ImGui.GetTextLineHeightWithSpacing() + 4f) * (EnlCalculatorView.PreviewLevels + 1);
        }

        ImGuiNET.ImGui.PushTextWrapPos(0f);   // wrap in the cell, not at the panel's wrap position
        try
        {
            if (ImGuiNET.ImGui.BeginTable("##enl_levels", 4, flags, size))
            {
                ImGuiNET.ImGui.TableSetupColumn("Enl", ImGuiTableColumnFlags.WidthFixed, 40 * k);
                ImGuiNET.ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthFixed, 84 * k);
                ImGuiNET.ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 44 * k);
                ImGuiNET.ImGui.TableSetupColumn("Lum");
                if (all) ImGuiNET.ImGui.TableSetupScrollFreeze(0, 1);
                ImGuiNET.ImGui.TableHeadersRow();
                for (int i = 0; i < shown; i++)
                {
                    EnlLevelRow row = rows[i];
                    uint c = row.Ok ? Green : Amber;
                    ImGuiNET.ImGui.TableNextRow();
                    ImGuiNET.ImGui.TableNextColumn(); Label(row.LevelText, c);
                    ImGuiNET.ImGui.TableNextColumn(); Label(row.Item, c);
                    ImGuiNET.ImGui.TableNextColumn(); Label(row.CountText, c);
                    ImGuiNET.ImGui.TableNextColumn(); Label(row.Lum, c);
                }
                ImGuiNET.ImGui.EndTable();
            }
        }
        finally
        {
            ImGuiNET.ImGui.PopTextWrapPos();
        }

        if (canExpand)
        {
            // The label is rebuilt only when the row count or the mode changes, not every frame.
            if (_enlToggleTotal != rows.Length || _enlToggleAll != all)
            {
                _enlToggleTotal = rows.Length;
                _enlToggleAll = all;
                _enlToggleLabel = (all ? "Show first " + EnlCalculatorView.PreviewLevels.ToString(CultureInfo.InvariantCulture)
                                       : "Show all " + Math.Min(rows.Length, EnlCalculatorView.MaxLevels).ToString(CultureInfo.InvariantCulture) + " steps")
                                  + "##enl_lv_all";
            }
            if (ImGuiNET.ImGui.SmallButton(_enlToggleLabel)) _enlShowAllLevels = !all;
        }
        Label("Green = pack items and banked luminance still cover that step (simulated in order).", Dim);
    }

    private static void DrawEnlMaterials(EnlMaterialRow[] rows, float tw, float k)
    {
        if (rows.Length == 0) return;
        ImGuiNET.ImGui.Dummy(new Vector2(0, 4));
        Label("Still need / have", Text);
        ImGuiNET.ImGui.PushTextWrapPos(0f);
        try
        {
            if (ImGuiNET.ImGui.BeginTable("##enl_mats", 3,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp, new Vector2(tw, 0)))
            {
                ImGuiNET.ImGui.TableSetupColumn("Material");
                ImGuiNET.ImGui.TableSetupColumn("Still need", ImGuiTableColumnFlags.WidthFixed, 72 * k);
                ImGuiNET.ImGui.TableSetupColumn("Have", ImGuiTableColumnFlags.WidthFixed, 72 * k);
                ImGuiNET.ImGui.TableHeadersRow();
                int n = Math.Min(rows.Length, EnlCalculatorView.MaxListRows);
                for (int i = 0; i < n; i++)
                {
                    EnlMaterialRow m = rows[i];
                    uint c = m.Ok ? Green : Amber;
                    ImGuiNET.ImGui.TableNextRow();
                    ImGuiNET.ImGui.TableNextColumn(); Label(m.Label, c);
                    ImGuiNET.ImGui.TableNextColumn(); Label(m.Need, c);
                    ImGuiNET.ImGui.TableNextColumn(); Label(m.Have, c);
                }
                ImGuiNET.ImGui.EndTable();
            }
        }
        finally
        {
            ImGuiNET.ImGui.PopTextWrapPos();
        }
    }

    private static void DrawEnlChain(EnlPlanner e, float tw, float k)
    {
        EnlChainRow[] rows = e.Chain;
        if (rows.Length == 0) return;
        ImGuiNET.ImGui.Dummy(new Vector2(0, 4));
        Label("Sigil craft chain (301-325)", Text);
        if (!string.IsNullOrEmpty(e.ChainNote)) Label(e.ChainNote, Dim);   // wraps at the panel's wrap position
        ImGuiNET.ImGui.PushTextWrapPos(0f);
        try
        {
            if (ImGuiNET.ImGui.BeginTable("##enl_chain", 3,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp, new Vector2(tw, 0)))
            {
                ImGuiNET.ImGui.TableSetupColumn("Item");
                ImGuiNET.ImGui.TableSetupColumn("Need", ImGuiTableColumnFlags.WidthFixed, 72 * k);
                ImGuiNET.ImGui.TableSetupColumn("Have", ImGuiTableColumnFlags.WidthFixed, 72 * k);
                ImGuiNET.ImGui.TableHeadersRow();
                int n = Math.Min(rows.Length, EnlCalculatorView.MaxListRows);
                for (int i = 0; i < n; i++)
                {
                    EnlChainRow r = rows[i];
                    uint c = r.Ok ? Green : Amber;
                    ImGuiNET.ImGui.TableNextRow();
                    ImGuiNET.ImGui.TableNextColumn(); Label(r.DisplayLabel, c);
                    ImGuiNET.ImGui.TableNextColumn(); Label(r.NeedText, c);
                    ImGuiNET.ImGui.TableNextColumn(); Label(r.HaveText, c);
                }
                ImGuiNET.ImGui.EndTable();
            }
        }
        finally
        {
            ImGuiNET.ImGui.PopTextWrapPos();
        }
    }

    // ── Confirm ─────────────────────────────────────────────────────────

    /// <summary>The Progression tab's Yes/No popup (outside the body child, like DrawConfirm).</summary>
    private void DrawProgressionConfirm()
    {
        if (_progConfirmOpenRequested)
        {
            _progConfirmOpenRequested = false;
            ImGuiNET.ImGui.OpenPopup(ProgConfirmId);
        }
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.PopupBg, ShellBg);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, Teal);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 4f);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10, 8));
        bool open = ImGuiNET.ImGui.BeginPopup(ProgConfirmId);
        ImGuiNET.ImGui.PopStyleVar(2);
        ImGuiNET.ImGui.PopStyleColor(2);
        if (!open) return;
        try
        {
            ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.UiBold11));
            Label(_progConfirmTitle, Amber);
            ImGuiNET.ImGui.PopFont();
            ImGuiNET.ImGui.PushTextWrapPos(ImGuiNET.ImGui.GetCursorPosX() + 320);
            Label(_progConfirmText, Text);
            ImGuiNET.ImGui.PopTextWrapPos();

            Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos() + new Vector2(0, 4);
            if (Button("##prog_yes", _progConfirmYes, p, new Vector2(120, 24), Text, StartBg))
            {
                SendProg(_progConfirmCommand);
                ImGuiNET.ImGui.CloseCurrentPopup();
            }
            if (Button("##prog_no", "Cancel", new Vector2(p.X + 126, p.Y), new Vector2(90, 24), Text, BtnFill))
                ImGuiNET.ImGui.CloseCurrentPopup();
            NextLine(p, 28);
        }
        finally
        {
            ImGuiNET.ImGui.EndPopup();
        }
    }
}
