// ============================================================================
//  RynthCore.Engine - ImGui/Panels/BountiesFace.cs
//  ACECustom bounties: every active bounty on the server and this character's
//  progress on each (kills, cooldown or QB requirement), from the RynthILT
//  plugin's tracker (UI/Data/BountiesData.cs). Part of the ILT section: only
//  registered when RynthILT is installed, opened from the bar's ILT button or
//  "/rc bounties" on Infinite Leaftide only. ImGui-only. Ported from
//  SilentKelpie's rynth/RynthCore PR #5.
//
//  The plugin reads /bounty list (quietly) only while this face is shown on
//  ILT: on open, a moment after each kill, after bounty chat events and every
//  few minutes, never more than once per MinRefreshSeconds. Options, Hide
//  QB-locked and Closest first live in the plugin (per character); the text
//  filter is this face's own.
// ============================================================================

using System;
using System.Globalization;
using System.Numerics;
using ImGuiNET;
using RynthCore.Engine.UI.Data;
using static RynthCore.Engine.ImGuiBackend.Panels.FaceKit;

namespace RynthCore.Engine.ImGuiBackend.Panels;

internal sealed class BountiesFace : IImGuiPanel
{
    public const string Title = "Bounties";

    public static void Register() => ImGuiPanelHost.Register(Title,
        new PanelSpec(new Vector2(560, 420), new Vector2(360, 220), EdgeToEdge: true),
        () => new BountiesFace());

    private const float RowH = 24, ProgressW = 160, FooterH = 18;
    private const int MinRefreshFloor = 5, MinRefreshCeiling = 300, PeriodicCeiling = 60;

    private static readonly uint OnBg = RynthTheme.Argb(0xFF104010), OnText = RynthTheme.Argb(0xFF33FF33);
    private static readonly uint BarFill = RynthTheme.Argb(0xFF2E8C4A), BarChanged = RynthTheme.Argb(0xFF26C8D9),
        BarTrack = RynthTheme.Argb(0xFF0E1A24);

    private readonly byte[] _filter = new byte[64];
    private bool _showOptions;

    public void OnShown()
    {
        UiSources.Bounties.Subscribe();
        UiSources.Bounties.RequestRefresh();
    }

    public void OnHidden() => UiSources.Bounties.Unsubscribe();

    public void Draw()
    {
        // Off Infinite Leaftide the source stops polling; never show its last list there.
        BountiesSnapshot? data = UiSources.Bounties.NotIlt ? null : UiSources.Bounties.Current?.Value;
        float w = Begin(out Vector2 origin, out Vector2 size);

        DrawToolbar(data, w);
        if (data != null)
        {
            if (_showOptions) DrawOptions(data, w);
            DrawFilterRow(data, w);
            if (data.LastEvent.Length > 0)
            {
                Label(data.LastEvent + (data.LastEventAgoSec >= 0 ? "  (" + Ago(data.LastEventAgoSec) + " ago)" : ""), Amber);
            }
        }

        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ChildBg, PanelBg);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, 0));
        ImGuiNET.ImGui.BeginChild("##bounties_body", new Vector2(w, Math.Max(60, Remaining(origin, size) - FooterH - GripOverlap())));
        float bw = ImGuiNET.ImGui.GetContentRegionAvail().X;
        DrawBody(data, bw);
        ImGuiNET.ImGui.EndChild();
        ImGuiNET.ImGui.PopStyleVar();
        ImGuiNET.ImGui.PopStyleColor();

        ImFontPtr f9 = ImGuiFonts.Get(UiFont.Ui9);
        Vector2 fp = ImGuiNET.ImGui.GetCursorScreenPos();
        ImGuiNET.ImGui.GetWindowDrawList().AddText(f9, f9.FontSize, fp + new Vector2(2, 3), Mute,
            "Kills count in the bounty's own area only (XP/lum mobs you did the most damage to).");
        End(origin, size);
    }

    // ── Header ───────────────────────────────────────────────────────────

    private void DrawToolbar(BountiesSnapshot? data, float w)
    {
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        bool ready = data != null;
        if (Button("##bounty_refresh", PhosphorIcons.ArrowsClockwise + " Refresh", p, new Vector2(90, 22), Teal, BtnFill, ready))
            BountiesCommands.Refresh();

        string state = !ready
            ? (UiSources.Bounties.NotIlt ? "not Infinite Leaftide"
               : UiSources.Bounties.ExportMissing ? "this RynthILT build has no bounty tracker" : "waiting for RynthILT (log in)")
            : data!.Reading ? "reading /bounty list..."
            : data.Status.Length > 0 ? data.Status
            : data.ListedAgoSec < 0 ? "not read yet"
            : "updated " + Ago(data.ListedAgoSec) + " ago";
        ImGuiNET.ImGui.GetWindowDrawList().AddText(new Vector2(p.X + 98, p.Y + (22 - ImGuiNET.ImGui.GetFontSize()) * 0.5f), Mute, state);

        Vector2 op = new(p.X + w - 26, p.Y);
        if (IconButton("##bounty_opts", PhosphorIcons.Gear, op, new Vector2(24, 22), _showOptions ? Teal : Mute, _showOptions ? Selected : BtnFill, ready))
            _showOptions = !_showOptions;
        ImGuiNET.ImGui.SetItemTooltip("Options");
        NextLine(p, 26);
    }

    private static void DrawOptions(BountiesSnapshot d, float w)
    {
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        dl.AddRectFilled(p, p + new Vector2(w, 82), PanelBg, 3);
        float ty = (22 - ImGuiNET.ImGui.GetFontSize()) * 0.5f;

        float x = p.X + 6, y = p.Y + 4;
        dl.AddText(new Vector2(x, y + ty), Text, "Update after my kills");
        float bx = x + ImGuiNET.ImGui.CalcTextSize("Update after my kills").X + 8;
        if (Button("##bounty_onkills", d.RefreshOnKills ? "ON" : "OFF", new Vector2(bx, y), new Vector2(50, 22),
                   d.RefreshOnKills ? OnText : Mute, d.RefreshOnKills ? OnBg : BtnFill))
            BountiesCommands.Set("refreshonkills", !d.RefreshOnKills);

        y += 26;
        dl.AddText(new Vector2(x, y + ty), Text, "At most every (sec)");
        int min = Spinner("##bounty_min", d.MinRefreshSeconds, MinRefreshFloor, MinRefreshCeiling, 5,
                          new Vector2(x + 150, y));
        if (min != d.MinRefreshSeconds) BountiesCommands.Set("minrefreshseconds", min.ToString(CultureInfo.InvariantCulture));

        y += 26;
        dl.AddText(new Vector2(x, y + ty), Text, "Re-read every (min, 0 = off)");
        int per = Spinner("##bounty_per", d.PeriodicRefreshMinutes, 0, PeriodicCeiling, 1,
                          new Vector2(x + 190, y));
        if (per != d.PeriodicRefreshMinutes) BountiesCommands.Set("periodicminutes", per.ToString(CultureInfo.InvariantCulture));

        ImFontPtr f9 = ImGuiFonts.Get(UiFont.Ui9);
        dl.AddText(f9, f9.FontSize, new Vector2(x + 310, p.Y + 8), Mute, "Reads run only while this\nwindow is open; the reply\nis hidden from chat.");
        NextLine(p, 86);
    }

    private void DrawFilterRow(BountiesSnapshot d, float w)
    {
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        float filterW = Math.Clamp(w - 250, 120, 260);
        TextBox("##bounty_filter", _filter, p, filterW, "filter by area or reward", out _);

        float x = p.X + filterW + 6;
        const string hideLabel = "Hide QB-locked";
        float hw = ButtonWidth(hideLabel);
        if (Button("##bounty_hideqb", hideLabel, new Vector2(x, p.Y), new Vector2(hw, 22),
                   d.HideQbLocked ? OnText : Mute, d.HideQbLocked ? OnBg : BtnFill))
            BountiesCommands.Set("hideqblocked", !d.HideQbLocked);
        if (d.HiddenCount > 0) SafeImGui.SetItemTooltip($"{d.HiddenCount} hidden");

        x += hw + 4;
        const string sortLabel = "Closest first";
        if (Button("##bounty_sort", sortLabel, new Vector2(x, p.Y), new Vector2(ButtonWidth(sortLabel), 22),
                   d.SortByProgress ? OnText : Mute, d.SortByProgress ? OnBg : BtnFill))
            BountiesCommands.Set("sortbyprogress", !d.SortByProgress);
        ImGuiNET.ImGui.SetItemTooltip("Nearest to a reward first, then cooldowns, then QB-locked");
        NextLine(p, 26);
    }

    // ── Rows ─────────────────────────────────────────────────────────────

    private void DrawBody(BountiesSnapshot? d, float w)
    {
        if (d == null)
        {
            Empty(UiSources.Bounties.NotIlt ? "Bounties are an Infinite Leaftide feature (/rc ilt for why)."
                : "Log in with RynthILT loaded to track bounties.", w);
            return;
        }
        if (d.Unsupported) { Empty("This server has no /bounty command. Press Refresh to check again.", w); return; }
        if (d.Rows.Count == 0)
        {
            Empty(d.NoneAnywhere ? "No bounties are active anywhere right now."
                : d.HiddenCount > 0 ? "Every bounty is QB-locked (Hide QB-locked is on)."
                : d.Reading ? "Reading the server's bounties..."
                : "No bounties read yet. Press Refresh.", w);
            return;
        }

        float areaW = (w - ProgressW) * 0.42f, rewardW = w - ProgressW - areaW;
        TableHeader(areaW, rewardW, w);

        string filter = Utf8(_filter).Trim();
        int shown = 0;
        for (int i = 0; i < d.Rows.Count; i++)
        {
            BountyRowJson r = d.Rows[i];
            if (filter.Length > 0 && !r.Where.Contains(filter, StringComparison.OrdinalIgnoreCase)
                && !r.Item.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            ImGuiNET.ImGui.PushID(i);
            DrawRow(r, shown++, areaW, rewardW, w);
            ImGuiNET.ImGui.PopID();
        }
        if (shown == 0) Empty("No bounty matches the filter.", w);
    }

    private static void TableHeader(float areaW, float rewardW, float w)
    {
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        dl.AddRectFilled(p, p + new Vector2(w, 22), Selected);
        ImFontPtr bold = ImGuiFonts.Get(UiFont.UiBold11);
        float ty = p.Y + (22 - bold.FontSize) * 0.5f;
        dl.AddText(bold, bold.FontSize, new Vector2(p.X + 6, ty), Text, "Area");
        dl.AddText(bold, bold.FontSize, new Vector2(p.X + areaW + 6, ty), Text, "Reward");
        dl.AddText(bold, bold.FontSize, new Vector2(p.X + areaW + rewardW + 6, ty), Text, "Progress");
        ImGuiNET.ImGui.Dummy(new Vector2(w, 22));
    }

    private static void DrawRow(BountyRowJson r, int index, float areaW, float rewardW, float w)
    {
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        dl.AddRectFilled(p, p + new Vector2(w, RowH), index % 2 == 0 ? RowAlt : PanelBg);
        float ty = p.Y + (RowH - ImGuiNET.ImGui.GetFontSize()) * 0.5f;

        dl.PushClipRect(p, p + new Vector2(areaW - 4, RowH), true);
        dl.AddText(new Vector2(p.X + 6, ty), Text, r.Where);
        dl.PopClipRect();

        float rx = p.X + areaW;
        dl.PushClipRect(new Vector2(rx, p.Y), new Vector2(rx + rewardW - 4, p.Y + RowH), true);
        dl.AddText(new Vector2(rx + 6, ty), Text, N0(r.Amount) + " " + r.Item);
        dl.PopClipRect();

        float px = rx + rewardW;
        var status = (BountyRowStatus)r.Status;
        switch (status)
        {
            case BountyRowStatus.Counting:
            {
                Vector2 b0 = new(px + 4, p.Y + 4), b1 = new(p.X + w - 6, p.Y + RowH - 4);
                float frac = r.KillsRequired > 0 ? Math.Clamp(r.Kills / (float)r.KillsRequired, 0f, 1f) : 0f;
                dl.AddRectFilled(b0, b1, BarTrack, 3);
                if (frac > 0) dl.AddRectFilled(b0, new Vector2(b0.X + (b1.X - b0.X) * frac, b1.Y), r.Changed ? BarChanged : BarFill, 3);
                dl.AddRect(b0, b1, BtnBord, 3);
                string text = N0(r.Kills) + " / " + N0(r.KillsRequired);
                Vector2 ts = ImGuiNET.ImGui.CalcTextSize(text);
                dl.AddText(new Vector2((b0.X + b1.X - ts.X) * 0.5f, ty), Text, text);
                break;
            }
            case BountyRowStatus.Cooldown:
                dl.AddText(new Vector2(px + 6, ty), Amber, "unlocks in " + Ago(r.UnlockInSec));
                break;
            default:
                dl.AddText(new Vector2(px + 6, ty), Mute, "needs " + Compact(r.QbRequired) + " QB");
                break;
        }

        ImGuiNET.ImGui.SetCursorScreenPos(p);
        ImGuiNET.ImGui.InvisibleButton("##row", new Vector2(w, RowH));
        if (ImGuiNET.ImGui.IsItemHovered())
        {
            string every = $"Every {N0(r.KillsRequired)} kill(s)"
                           + (r.CooldownSeconds > 0 ? ", at most every " + Ago(r.CooldownSeconds) : "");
            string detail = status switch
            {
                BountyRowStatus.Cooldown => $"\nKills don't count for {Ago(r.UnlockInSec)}; after that {N0(r.KillsRequired)} kill(s).",
                BountyRowStatus.NeedsQb => $"\nNeeds {N0(r.QbRequired)} QB; you have {N0(r.QbHave)}.",
                _ => string.Empty,
            };
            SafeImGui.SetTooltip($"{r.Where}: {N0(r.Amount)} {r.Item}\n{every}{detail}");
        }
        NextLine(p, RowH);
    }

    private static void Empty(string message, float w)
    {
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        ImGuiNET.ImGui.GetWindowDrawList().AddRectFilled(p, p + new Vector2(w, 22), RowAlt);
        ImGuiNET.ImGui.GetWindowDrawList().AddText(p + new Vector2(8, (22 - ImGuiNET.ImGui.GetFontSize()) * 0.5f), Mute, message);
        ImGuiNET.ImGui.Dummy(new Vector2(w, 22));
    }

    /// <summary>[-] value [+]; returns the new value (same when nothing was clicked).</summary>
    private static int Spinner(string id, int value, int min, int max, int step, Vector2 pos)
    {
        int v = value;
        ImGuiNET.ImGui.PushID(id);
        if (IconButton("##dec", PhosphorIcons.Minus, pos, new Vector2(22, 22), Teal, BtnFill)) v = Math.Max(min, v - step);
        string text = v.ToString(CultureInfo.InvariantCulture);
        float tw = ImGuiNET.ImGui.CalcTextSize(text).X;
        ImGuiNET.ImGui.GetWindowDrawList().AddText(new Vector2(pos.X + 24 + (40 - tw) * 0.5f, pos.Y + (22 - ImGuiNET.ImGui.GetFontSize()) * 0.5f), Text, text);
        if (IconButton("##inc", PhosphorIcons.Plus, new Vector2(pos.X + 66, pos.Y), new Vector2(22, 22), Teal, BtnFill)) v = Math.Min(max, v + step);
        ImGuiNET.ImGui.PopID();
        return v;
    }

    // ── Formatting ───────────────────────────────────────────────────────

    private static string N0(long v) => v.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>1.2M, 350k, 900.</summary>
    private static string Compact(long v) =>
        v >= 1_000_000 ? (v / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + "M"
        : v >= 10_000 ? (v / 1_000d).ToString("0.#", CultureInfo.InvariantCulture) + "k"
        : N0(v);

    /// <summary>"45 sec", "2 min 30 sec", "1 hr 5 min", "2 days 3 hr".</summary>
    private static string Ago(int seconds)
    {
        long s = Math.Max(0, seconds);
        if (s >= 86400) return $"{s / 86400} day{(s / 86400 == 1 ? "" : "s")}" + (s % 86400 >= 3600 ? $" {s % 86400 / 3600} hr" : "");
        if (s >= 3600) return $"{s / 3600} hr" + (s % 3600 >= 60 ? $" {s % 3600 / 60} min" : "");
        if (s >= 60) return $"{s / 60} min" + (s % 60 > 0 ? $" {s % 60} sec" : "");
        return $"{s} sec";
    }
}
