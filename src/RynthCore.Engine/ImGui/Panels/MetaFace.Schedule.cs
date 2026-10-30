// ============================================================================
//  RynthCore.Engine - ImGui/Panels/MetaFace.Schedule.cs
//  The Meta panel's Schedule view: RynthAi's Meta Manager, which loads a meta
//  when a timer runs out (RynthSuite Docs/META_MANAGER.md).
//
//    [x Manager] Poll [5] min [poll now]        Wait [60] s  Gap [30] s  [x While stopped]
//    34 quests, polled 2m ago . last: farm.af (quest x ready)
//    #  [on] [up][dn] [edit][del]  when quest x ready -> farm.af     ready | 12m 30s
//    ...
//    [+ New Rule]                                          first match: amber edge
//
//  Data: the "schedule" object of the Meta snapshot (UiSources.Meta). Every
//  change is an mm_* MetaCommand, applied by RynthAi on its tick; nothing here
//  loads a meta or sends chat. Times are absolute (unix ms) in the snapshot,
//  so the countdowns tick here between snapshots.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using ImGuiNET;
using RynthCore.Engine.UI.Data;

namespace RynthCore.Engine.ImGuiBackend.Panels;

internal sealed partial class MetaFace
{
    private const string ScheduleLabel = PhosphorIcons.HourglassMedium + " Schedule##mode_sch";

    private static readonly string[] TriggerNames =
    {
        "Quest ready (/myquests timer)", "After N minutes on the meta", "Every N minutes", "Countdown (you start it)",
    };

    // Rule editor: -2 closed, -1 new rule, else the rule's index.
    private int _schedEdit = -2;
    private ScheduleRuleDto _schedRule = new();
    private readonly byte[] _schedQuest = new byte[128];
    private readonly byte[] _schedMinutes = new byte[16];
    private readonly byte[] _schedPoll = new byte[8], _schedWait = new byte[8], _schedGap = new byte[8];
    private long _schedFieldsVersion = -1;   // snapshot the number boxes were last filled from
    private string[] _schedMetaPicks = Array.Empty<string>(), _schedOnlyPicks = Array.Empty<string>();

    private void ShowSchedule()
    {
        _view = View.Schedule;
        _schedEdit = -2;
    }

    private void ScheduleView(Vector2 origin, Vector2 size)
    {
        float w = size.X;
        SchedulePayload? sc = _data.Schedule;
        ModeRow(w, source: false, schedule: true);
        Separator(w);
        if (sc == null)
        {
            ImGuiNET.ImGui.TextColored(RynthTheme.VecOf(RynthTheme.MuteAlt), "  This RynthAi has no Meta Manager (update RynthAi).");
            return;
        }

        RefreshScheduleFields(sc);
        if (_schedEdit != -2)
        {
            ScheduleEditor(sc, size.Y - (ImGuiNET.ImGui.GetCursorScreenPos().Y - origin.Y) - FaceKit.GripOverlap());
            return;
        }

        ScheduleSettings(sc, w);
        ScheduleStatusLine(sc, w);
        Separator(w);

        const float bottomH = 24;
        float listH = Math.Max(40, origin.Y + size.Y - ImGuiNET.ImGui.GetCursorScreenPos().Y - bottomH - 9);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ChildBg, 0);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, 0));
        ImGuiNET.ImGui.BeginChild("##sched_rules", new Vector2(w, listH));
        ScheduleRules(sc, ImGuiNET.ImGui.GetContentRegionAvail().X);
        ImGuiNET.ImGui.EndChild();
        ImGuiNET.ImGui.PopStyleVar();
        ImGuiNET.ImGui.PopStyleColor();

        Separator(w);
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        float newW = BtnW(PhosphorIcons.Plus + " New Rule", 80);
        if (Button(PhosphorIcons.Plus + " New Rule##sched_new", new Vector2(p.X, p.Y), new Vector2(newW, 24), TextDim, NewFill, Green))
            OpenScheduleEditor(-1, new ScheduleRuleDto { Meta = FirstMetaFile() });
        if (sc.Rules.Count > 0)
        {
            if (Button(PhosphorIcons.ArrowCounterClockwise + " Re-arm all##sched_reset", new Vector2(p.X + newW + 4, p.Y), new Vector2(BtnW(PhosphorIcons.ArrowCounterClockwise + " Re-arm all", 86), 24),
                    Mute, BtnFill, BtnBord))
                MetaCommands.Simple("mm_reset", -1);
            ImGuiNET.ImGui.SetItemTooltip("Clear every rule's fired / error / holding state");
        }
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(p.X, p.Y + 24));
        ImGuiNET.ImGui.Dummy(new Vector2(0, 0));
    }

    /// <summary>Refill the number boxes from a new snapshot (not while one is being typed in).</summary>
    private void RefreshScheduleFields(SchedulePayload sc)
    {
        long v = UiSources.Meta.Current?.Version ?? 0;
        if (v == _schedFieldsVersion || ImGuiNET.ImGui.IsAnyItemActive()) return;
        _schedFieldsVersion = v;
        WriteUtf8(_schedPoll, sc.PollMinutes.ToString(CultureInfo.InvariantCulture));
        WriteUtf8(_schedWait, sc.MaxWaitSeconds.ToString(CultureInfo.InvariantCulture));
        WriteUtf8(_schedGap, sc.MinGapSeconds.ToString(CultureInfo.InvariantCulture));
        var metas = new List<string>();
        foreach (MetaFile f in _data.Files)
            if (!string.IsNullOrEmpty(f.Path)) metas.Add(System.IO.Path.GetFileName(f.Path));
        _schedMetaPicks = metas.ToArray();
        var only = new List<string> { "(any meta)" };
        only.AddRange(metas);
        _schedOnlyPicks = only.ToArray();
    }

    private string FirstMetaFile() => _schedMetaPicks.Length > 0 ? _schedMetaPicks[0] : string.Empty;

    // [x Manager] Poll [5] min [poll now] | Wait [60] s Gap [30] s [x While stopped]
    private void ScheduleSettings(SchedulePayload sc, float w)
    {
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        float x = p.X;
        if (Toggle((sc.Enabled ? PhosphorIcons.CheckSquare : PhosphorIcons.Square) + " Manager##sched_on", ref x, p.Y, sc.Enabled))
        {
            sc.Enabled = !sc.Enabled;   // shows at once; the next snapshot confirms
            MetaCommands.Simple("mm_enabled", value: sc.Enabled ? "true" : "false");
        }
        ImGuiNET.ImGui.SetItemTooltip("Off by default. While on, it loads the first due rule's meta (also /ra metamgr on|off).");
        x += 4;
        NumberField("Poll", "##sched_poll", _schedPoll, "min", ref x, p.Y, 30, "How often it sends /myquests (minimum 1). Only while on and with a quest rule.",
            n => { if (n >= 1) MetaCommands.Simple("mm_poll_minutes", value: n.ToString(CultureInfo.InvariantCulture)); });
        if (IconButton("##sched_pollnow", PhosphorIcons.ArrowsClockwise, new Vector2(x, p.Y), new Vector2(24, 22), TextDim, BtnFill, BtnBord,
                enabled: sc.Enabled))
            MetaCommands.Simple("mm_poll_now");
        ImGuiNET.ImGui.SetItemTooltip("Check /myquests now");
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(p.X, p.Y + 22));
        ImGuiNET.ImGui.Dummy(new Vector2(0, 0));

        Vector2 q = ImGuiNET.ImGui.GetCursorScreenPos();
        x = q.X;
        NumberField("Wait up to", "##sched_wait", _schedWait, "s", ref x, q.Y, 34,
            "A switch waits until the bot is not fighting, looting, buffing or in a portal - at most this long, then it switches anyway.",
            n => MetaCommands.Simple("mm_max_wait", value: Math.Max(0, n).ToString(CultureInfo.InvariantCulture)));
        NumberField("Gap", "##sched_gap", _schedGap, "s", ref x, q.Y, 34, "At least this long between two switches.",
            n => MetaCommands.Simple("mm_min_gap", value: Math.Max(0, n).ToString(CultureInfo.InvariantCulture)));
        if (Toggle((sc.AllowWhileStopped ? PhosphorIcons.CheckSquare : PhosphorIcons.Square) + " While stopped##sched_stopped", ref x, q.Y,
                sc.AllowWhileStopped))
        {
            sc.AllowWhileStopped = !sc.AllowWhileStopped;
            MetaCommands.Simple("mm_allow_stopped", value: sc.AllowWhileStopped ? "true" : "false");
        }
        ImGuiNET.ImGui.SetItemTooltip("Also switch metas while the macro is stopped (off: it waits for the macro to run).");
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(q.X, q.Y + 24));
        ImGuiNET.ImGui.Dummy(new Vector2(0, 0));
    }

    /// <summary>"Label [box] unit" at (x, y); commits a whole number when the box loses focus after an edit.</summary>
    private static void NumberField(string label, string id, byte[] buf, string unit, ref float x, float y, float boxW, string tip, Action<int> commit)
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        float fy = y + (22 - ImGuiNET.ImGui.GetFontSize()) * 0.5f;
        dl.AddText(new Vector2(x, fy), Mute, label);
        x += ImGuiNET.ImGui.CalcTextSize(label).X + 4;
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(x, y));
        TextBox(id, buf, boxW, "", ImGuiInputTextFlags.CharsDecimal, out _);
        bool done = ImGuiNET.ImGui.IsItemDeactivatedAfterEdit();
        SafeImGui.SetItemTooltip(tip);
        if (done && int.TryParse(Utf8(buf).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
            commit(n);
        x += boxW + 3;
        dl.AddText(new Vector2(x, fy), Mute, unit);
        x += ImGuiNET.ImGui.CalcTextSize(unit).X + 8;
    }

    private void ScheduleStatusLine(SchedulePayload sc, float w)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string poll = !sc.Enabled ? "Off - nothing is loaded or polled."
            : sc.ServerDisabled ? "The server has /myquests turned off: quest rules can't fire."
            : !sc.Polling ? "No quest rules: /myquests is not polled."
            : sc.LastPollMs == 0 ? "Waiting for the first /myquests."
            : $"{sc.QuestCount} quest timer(s), /myquests {Span(now - sc.LastPollMs)} ago.";
        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui9));
        SafeImGui.TextColored(RynthTheme.VecOf(sc.ServerDisabled ? RynthTheme.Highlight : RynthTheme.MuteAlt), Fit(poll, w));
        if (sc.LastSwitch.Length > 0)
            SafeImGui.TextColored(RynthTheme.VecOf(RynthTheme.Accent),
                Fit($"Last: {sc.LastSwitch}{(sc.LastSwitchMs > 0 ? $", {Span(now - sc.LastSwitchMs)} ago" : "")}", w));
        ImGuiNET.ImGui.PopFont();
    }

    private void ScheduleRules(SchedulePayload sc, float w)
    {
        if (sc.Rules.Count == 0)
        {
            ImGuiNET.ImGui.Dummy(new Vector2(0, 4));
            ImGuiNET.ImGui.TextColored(RynthTheme.VecOf(RynthTheme.MuteAlt), "  No rules. New Rule: when a timer runs out, load a meta.");
            ImGuiNET.ImGui.TextColored(RynthTheme.VecOf(RynthTheme.MuteAlt), "  The first due rule (top to bottom) wins.");
            return;
        }
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        const float h = 20;
        for (int i = 0; i < sc.Rules.Count; i++)
        {
            ScheduleRuleDto r = sc.Rules[i];
            bool first = i == sc.FirstMatch && sc.Enabled;
            Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
            dl.AddRectFilled(p, p + new Vector2(w, h), i % 2 == 0 ? StripeA : StripeB);
            ImGuiNET.ImGui.PushID(1000 + i);
            bool changed = false;
            float x = p.X + 1;

            dl.AddText(new Vector2(x + 2, p.Y + (h - ImGuiNET.ImGui.GetFontSize()) * 0.5f), Mute, (i + 1).ToString(CultureInfo.InvariantCulture));
            x += 16;
            if (IconButton("##en", r.Enabled ? PhosphorIcons.CheckSquare : PhosphorIcons.Square, new Vector2(x, p.Y + 2), new Vector2(14, 16),
                    r.Enabled ? Green : Mute, BtnFill, BtnBord))
            {
                r.Enabled = !r.Enabled;
                MetaCommands.Simple("mm_rule_enabled", i, r.Enabled ? "true" : "false");
            }
            SafeImGui.SetItemTooltip(r.Enabled ? "On - click to turn this rule off" : "Off - click to turn it on");
            x += 16;
            if (IconButton("##up", PhosphorIcons.ArrowUp, new Vector2(x + 1, p.Y + 2), new Vector2(16, 16), Mute, BtnFill, BtnBord, enabled: i > 0))
            {
                MetaCommands.Simple("mm_move", i, "-1");
                (sc.Rules[i], sc.Rules[i - 1]) = (sc.Rules[i - 1], sc.Rules[i]);
                changed = true;
            }
            ImGuiNET.ImGui.SetItemTooltip("Higher priority");
            x += 18;
            if (IconButton("##dn", PhosphorIcons.ArrowDown, new Vector2(x + 1, p.Y + 2), new Vector2(16, 16), Mute, BtnFill, BtnBord,
                    enabled: i < sc.Rules.Count - 1))
            {
                MetaCommands.Simple("mm_move", i, "1");
                (sc.Rules[i], sc.Rules[i + 1]) = (sc.Rules[i + 1], sc.Rules[i]);
                changed = true;
            }
            ImGuiNET.ImGui.SetItemTooltip("Lower priority");
            x += 18;
            if (r.Trigger == 3)   // countdown: start / stop
            {
                bool running = r.DueAtMs > 0;
                if (IconButton("##cd", running ? PhosphorIcons.Stop : PhosphorIcons.Play, new Vector2(x + 1, p.Y + 2), new Vector2(16, 16),
                        running ? Amber : Green, BtnFill, BtnBord))
                    MetaCommands.Simple(running ? "mm_countdown_stop" : "mm_countdown_start", i);
                SafeImGui.SetItemTooltip(running ? "Stop the countdown" : $"Start the countdown ({Num(r.Minutes)} min)");
            }
            else if (r.Fired || (r.State ?? "").StartsWith("error", StringComparison.Ordinal) || (r.State ?? "").Contains("holding"))
            {
                if (IconButton("##rearm", PhosphorIcons.ArrowCounterClockwise, new Vector2(x + 1, p.Y + 2), new Vector2(16, 16), Teal, BtnFill, BtnBord))
                    MetaCommands.Simple("mm_reset", i);
                ImGuiNET.ImGui.SetItemTooltip("Re-arm this rule");
            }
            x += 18;
            if (IconButton("##del", PhosphorIcons.Trash, new Vector2(x + 1, p.Y + 2), new Vector2(18, 16), TextDim, DelFill, BtnBord))
            {
                MetaCommands.Simple("mm_delete", i);
                sc.Rules.RemoveAt(i);
                changed = true;
            }
            ImGuiNET.ImGui.SetItemTooltip("Delete rule");
            x += 22;

            // Text: "when TRIGGER, load META" | time left / state; click to edit.
            float textW = p.X + w - x;
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(x, p.Y));
            bool open = ImGuiNET.ImGui.InvisibleButton("##edit", new Vector2(Math.Max(1, textW), h));
            if (ImGuiNET.ImGui.IsItemHovered())
            {
                ImGuiNET.ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                SafeImGui.SetTooltip(RuleTooltip(r, i, first));
            }
            ImGuiNET.ImGui.PopID();
            if (changed) return;   // the list changed: draw it again next frame
            if (open)
            {
                OpenScheduleEditor(i, r);
                return;
            }

            (string when, uint whenCol) = WhenText(r, now);
            float whenW = ImGuiNET.ImGui.CalcTextSize(when).X;
            float ty = p.Y + (h - ImGuiNET.ImGui.GetFontSize()) * 0.5f;
            uint col = r.Enabled ? TextDim : Faded(TextDim);
            dl.AddText(new Vector2(x + 3, ty), col, Fit(RuleText(r), Math.Max(10, textW - whenW - 14)));
            dl.AddText(new Vector2(p.X + w - whenW - 4, ty), r.Enabled ? whenCol : Faded(whenCol), when);
            if (first) dl.AddRect(p, p + new Vector2(w, h), Amber, 2);

            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(p.X, p.Y + h));
            ImGuiNET.ImGui.Dummy(new Vector2(0, 0));
        }
    }

    private static string RuleText(ScheduleRuleDto r)
    {
        string trig = r.Trigger switch
        {
            0 => $"quest {(r.Quest.Length > 0 ? r.Quest : "?")} ready",
            1 => $"{Num(r.Minutes)} min on the meta",
            2 => $"every {Num(r.Minutes)} min",
            _ => $"{Num(r.Minutes)} min countdown",
        };
        string on = r.OnlyOnMeta.Length > 0 ? $" (on {r.OnlyOnMeta})" : "";
        return $"{trig}{on} {Arrow} {(r.Meta.Length > 0 ? r.Meta : "?")}{(r.Repeat ? "" : "  [once]")}";
    }

    private const string Arrow = "->";

    /// <summary>The right-hand column: the state words, else the time left, "ready" or "never".</summary>
    private static (string Text, uint Color) WhenText(ScheduleRuleDto r, long nowMs)
    {
        string state = r.State ?? string.Empty;
        if (state.StartsWith("error", StringComparison.Ordinal)) return ("error", Red);
        if (state.StartsWith("waiting", StringComparison.Ordinal)) return (state, Amber);
        if (state == "switching") return (state, Amber);
        if (r.Fired) return ("fired", Mute);
        if (state.Contains("holding")) return ("holding", Teal);
        if (state.Length > 0 && r.DueAtMs == 0) return (Short(state), Mute);
        if (r.DueAtMs == -1) return ("never", Mute);
        if (r.DueAtMs == 0) return ("-", Mute);
        long left = r.DueAtMs - nowMs;
        return left <= 0 ? ("ready", Green) : (Span(left), TextDim);
    }

    private static string Short(string state) => state switch
    {
        "no /myquests yet" => "no data yet",
        "/myquests is off on this server" => "no /myquests",
        "not started" => "not started",
        _ => state.Length > 18 ? state.Substring(0, 18) + "…" : state,
    };

    private static string RuleTooltip(ScheduleRuleDto r, int i, bool first)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("Rule ").Append(i + 1).Append(": ").Append(TriggerNames[Math.Clamp(r.Trigger, 0, 3)]).Append('\n');
        if (r.Trigger == 0) sb.Append("Quest: ").Append(r.Quest.Length > 0 ? r.Quest : "(none)").Append('\n');
        else sb.Append("Minutes: ").Append(Num(r.Minutes)).Append('\n');
        sb.Append("Loads: ").Append(r.Meta.Length > 0 ? r.Meta : "(none)").Append('\n');
        if (r.OnlyOnMeta.Length > 0) sb.Append("Only while on: ").Append(r.OnlyOnMeta).Append('\n');
        sb.Append(r.Repeat ? "Repeats" : "Once (one-shot)");
        if (r.FireCount > 0) sb.Append(", fired ").Append(r.FireCount).Append("x this session");
        if (!string.IsNullOrEmpty(r.State)) sb.Append('\n').Append(r.State);
        if (first) sb.Append("\nFirst match: this rule fires next.");
        sb.Append("\nClick to edit.");
        return sb.ToString();
    }

    // ── Rule editor ─────────────────────────────────────────────────────

    private void OpenScheduleEditor(int index, ScheduleRuleDto r)
    {
        _schedEdit = index;
        _schedRule = r.CloneSettings();
        WriteUtf8(_schedQuest, _schedRule.Quest);
        WriteUtf8(_schedMinutes, Num(_schedRule.Minutes));
    }

    private void ScheduleEditor(SchedulePayload sc, float height)
    {
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ChildBg, 0);
        ImGuiNET.ImGui.BeginChild("##sched_editor", new Vector2(0, Math.Max(60, height)));
        ImGuiNET.ImGui.PopStyleColor();
        float w = ImGuiNET.ImGui.GetContentRegionAvail().X;
        ScheduleRuleDto r = _schedRule;

        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.UiBold11));
        SafeImGui.TextColored(RynthTheme.VecOf(RynthTheme.Accent), _schedEdit == -1 ? "New rule" : $"Edit rule {_schedEdit + 1}");
        ImGuiNET.ImGui.PopFont();

        FieldLabel("When:");
        int picked = FlowPicker("##sched_trig", TriggerNames[Math.Clamp(r.Trigger, 0, 3)], TriggerNames, r.Trigger, w);
        if (picked >= 0) r.Trigger = picked;

        if (r.Trigger == 0)
        {
            FieldLabel("Quest (as /myquests shows it; case doesn't matter):");
            if (TextBox("##sched_quest", _schedQuest, w, "e.g. blightlordlairwait1008", ImGuiInputTextFlags.None, out _))
                r.Quest = Utf8(_schedQuest).Trim();
        }
        else
        {
            FieldLabel(r.Trigger switch
            {
                1 => "Minutes on the current meta:",
                2 => "Every how many minutes:",
                _ => "Countdown minutes (start it with the play button or /ra metamgr start):",
            });
            if (TextBox("##sched_min", _schedMinutes, 80, "30", ImGuiInputTextFlags.CharsDecimal, out _)
                && double.TryParse(Utf8(_schedMinutes).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double m) && m >= 0)
                r.Minutes = m;
        }

        FieldLabel("Load meta:");
        picked = FlowPicker("##sched_meta", r.Meta.Length > 0 ? r.Meta : "Select a meta...", _schedMetaPicks,
            Array.FindIndex(_schedMetaPicks, s => string.Equals(s, r.Meta, StringComparison.OrdinalIgnoreCase)), w);
        if (picked >= 0) r.Meta = _schedMetaPicks[picked];

        FieldLabel("Only while this meta is loaded:");
        int onlyIdx = r.OnlyOnMeta.Length == 0 ? 0
            : Array.FindIndex(_schedOnlyPicks, s => string.Equals(s, r.OnlyOnMeta, StringComparison.OrdinalIgnoreCase));
        picked = FlowPicker("##sched_only", r.OnlyOnMeta.Length > 0 ? r.OnlyOnMeta : _schedOnlyPicks.Length > 0 ? _schedOnlyPicks[0] : "(any meta)",
            _schedOnlyPicks, onlyIdx, w);
        if (picked >= 0) r.OnlyOnMeta = picked == 0 ? string.Empty : _schedOnlyPicks[picked];

        ImGuiNET.ImGui.Dummy(new Vector2(0, 2));
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        float x = p.X;
        if (Toggle("Repeat##sched_rep", ref x, p.Y, r.Repeat)) r.Repeat = true;
        ImGuiNET.ImGui.SetItemTooltip("Fires every time its timer runs out");
        if (Toggle("Once##sched_once", ref x, p.Y, !r.Repeat)) r.Repeat = false;
        ImGuiNET.ImGui.SetItemTooltip("Fires once, then waits until you re-arm it");
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(p.X, p.Y + 22));
        ImGuiNET.ImGui.Dummy(new Vector2(0, 0));

        ImGuiNET.ImGui.Dummy(new Vector2(0, 2));
        Separator(w);
        bool valid = r.Meta.Length > 0 && (r.Trigger != 0 || r.Quest.Length > 0);
        Vector2 b = ImGuiNET.ImGui.GetCursorScreenPos();
        string saveLabel = (_schedEdit == -1 ? "Add Rule" : "Save Rule") + "##sched_save";
        float sw = ImGuiNET.ImGui.CalcTextSize(saveLabel.Substring(0, saveLabel.IndexOf("##", StringComparison.Ordinal))).X + 20;
        if (Button(saveLabel, b, new Vector2(sw, 22), TextDim, NewFill, Green, enabled: valid))
        {
            var send = r.CloneSettings();
            if (_schedEdit == -1) MetaCommands.Send(new MetaCmd { Op = "mm_add", ScheduleRule = send });
            else MetaCommands.Send(new MetaCmd { Op = "mm_update", Index = _schedEdit, ScheduleRule = send });
            _schedEdit = -2;
        }
        if (!valid) SafeImGui.SetItemTooltip(r.Meta.Length == 0 ? "Pick a meta to load" : "Name the quest");
        if (Button("Cancel##sched_cancel", new Vector2(b.X + sw + 6, b.Y), new Vector2(60, 22), Mute, BtnFill, BtnBord))
            _schedEdit = -2;
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(b.X, b.Y + 22));
        ImGuiNET.ImGui.Dummy(new Vector2(0, 0));
        if (_schedEdit >= 0)
        {
            ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui9));
            ImGuiNET.ImGui.TextColored(RynthTheme.VecOf(RynthTheme.MuteAlt), "Saving re-arms the rule (clears fired / error).");
            ImGuiNET.ImGui.PopFont();
        }
        ImGuiNET.ImGui.EndChild();
    }

    private static string Num(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>12s, 4m 05s, 3h 20m, 2d 4h.</summary>
    private static string Span(long ms)
    {
        if (ms < 0) ms = 0;
        var t = TimeSpan.FromMilliseconds(ms);
        if (t.TotalHours >= 24) return $"{(int)t.TotalDays}d {t.Hours}h";
        if (t.TotalMinutes >= 60) return $"{(int)t.TotalHours}h {t.Minutes:00}m";
        if (t.TotalSeconds >= 60) return $"{(int)t.TotalMinutes}m {t.Seconds:00}s";
        return $"{(int)t.TotalSeconds}s";
    }
}
