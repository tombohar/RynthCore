// ============================================================================
//  RynthCore.Engine - ImGui/Panels/SenseFace.cs
//  The Sense panel, our take on Virindi Sense: type a name, and every object
//  the client knows about anywhere (not just in radar range) whose name
//  matches shows up here. Engine only, ImGui only.
//
//    add row   a text box (Enter adds), Add, the settings gear, the help mark
//    terms     one chip per term: click it to switch it on/off, X removes it
//    status    how many matches among how many known objects (or why none)
//    rows      direction arrow (relative to where you face), name, kind,
//              distance, compass point, map coordinates; nearest first; a new
//              match flashes. Click selects it in game, hover for details.
//    gear      a chat line and/or the system beep when a new match appears
//              (both off by default; they work with the panel closed).
//
//  Data: SenseStore (the list, sense.txt) and UiSources.Sense (the 2 Hz scan on
//  the pump thread, snapshots only; UI/Data/SenseData.cs). This face only reads
//  the published view and the player pose snapshot (for the arrows, per frame)
//  and parks a click's select with AcMainThreadQueue.EnqueueOverlaySelect, the
//  path the Radar and the nameplates use. AC's render thread; no AC calls, no
//  file I/O, no per-frame allocation except a hovered row's tooltip.
// ============================================================================

using System;
using System.Numerics;
using ImGuiNET;
using RynthCore.Engine.Compatibility;
using RynthCore.Engine.UI.Data;
using static RynthCore.Engine.ImGuiBackend.Panels.FaceKit;

namespace RynthCore.Engine.ImGuiBackend.Panels;

internal sealed class SenseFace : IImGuiPanel
{
    public const string Title = "Sense";

    /// <summary>Registers the face (engine init) and queues the list's load so alerts work before it opens.</summary>
    public static void Register()
    {
        ImGuiPanelHost.Register(Title,
            new PanelSpec(new Vector2(470, 360), new Vector2(300, 220), EdgeToEdge: true, GripInBody: true),
            () => new SenseFace());
        SenseStore.EnsureLoadQueued();
    }

    /// <summary>Arrows turn with you: 10 Hz idle popped out, 30 Hz while a new match flashes.</summary>
    public int PopOutIdleHz => _flashing ? 30 : 10;

    // ── Layout (px) and palette ──────────────────────────────────────────
    private const float RowH = 20f, ArrowW = 20f, KindW = 58f, DistW = 64f, DirW = 34f, LocW = 116f, ChipH = 20f;
    private const long FlashMs = 2500;

    private static readonly uint Purple = RynthTheme.Argb(0xFFBF4DFF), ChipOff = RynthTheme.Argb(0xFF0E161E);

    private const string HelpText =
        "Sense lists every object the client knows about whose name matches a term on your list, however far away it is.\n\n" +
        "How far: the client only knows what the server has sent it. That is everything in the landblocks it has loaded " +
        "around you (outdoors: your landblock and the ones around it, a few hundred yards, far past the radar), or the whole " +
        "dungeon you are in. Nothing beyond that reaches the client, so no plugin can see it.\n\n" +
        "Terms ignore case. Without * or ? a name only has to contain the term (drake finds Dragon's Isle Drake). " +
        "With them the whole name must match: Drake* starts with Drake, ? is any one letter.\n\n" +
        "The distance shows when you and the object are both outdoors (or in surface buildings), or in the same dungeon. " +
        "Otherwise the row says elsewhere and gives the landblock.\n\n" +
        "Click a row to select it in game. Gear: a chat line or a beep when a new match appears.\n" +
        "/rc sense opens and closes this panel.";

    // ── State ───────────────────────────────────────────────────────────
    private readonly byte[] _input = new byte[SenseStore.MaxTermLength + 1];
    private string _message = "";
    private uint _messageColor;
    private long _messageUntilMs;
    private bool _openSettings;
    private bool _flashing;

    public void OnShown()
    {
        SenseStore.EnsureLoadQueued();
        UiSources.Sense.Subscribe();
        UiSources.Sense.RequestRefresh();
    }

    public void OnHidden() => UiSources.Sense.Unsubscribe();

    public void Draw()
    {
        float w = Begin(out Vector2 origin, out Vector2 size);
        DrawAddRow(w);
        DrawTerms(w);

        SenseView view = UiSources.Sense.Current?.Value ?? SenseView.Empty;
        DrawStatus(view);
        bool showLoc = w - (ArrowW + KindW + DistW + DirW + LocW) >= 110;
        DrawColumnHeader(w, showLoc);

        float bodyH = Math.Max(40, Remaining(origin, size) - GripOverlap());
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ChildBg, PanelBg);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, 0));
        ImGuiNET.ImGui.BeginChild("##sense_rows", new Vector2(w, bodyH));
        DrawRows(view, showLoc);
        ImGuiNET.ImGui.EndChild();
        ImGuiNET.ImGui.PopStyleVar();
        ImGuiNET.ImGui.PopStyleColor();
        End(origin, size);

        DrawSettingsPopup();
    }

    // ── Add row ─────────────────────────────────────────────────────────

    private void DrawAddRow(float w)
    {
        Vector2 start = ImGuiNET.ImGui.GetCursorScreenPos();
        const float h = 22, iconW = 22;
        string addLabel = PhosphorIcons.Plus + " Add";
        float addW = ButtonWidth(addLabel);
        float boxW = Math.Max(60, w - addW - iconW * 2 - 12);

        bool enter = TextBox("##sense_input", _input, start, boxW,
            PhosphorIcons.MagnifyingGlass + " Name to watch for (* and ? wildcards)", out _,
            ImGuiInputTextFlags.EnterReturnsTrue, h);
        bool add = Button("##sense_add", addLabel, new Vector2(start.X + boxW + 4, start.Y), new Vector2(addW, h), Text, BtnFill,
            enabled: SenseStore.Loaded);
        ImGuiNET.ImGui.SetItemTooltip("Add the name to the watch list (Enter in the box does the same)");
        if (enter || add) AddTerm();

        float gx = start.X + boxW + 4 + addW + 4;
        if (IconButton("##sense_gear", PhosphorIcons.Gear, new Vector2(gx, start.Y), new Vector2(iconW, h), Text, BtnFill))
            _openSettings = true;
        ImGuiNET.ImGui.SetItemTooltip("Alerts");
        IconButton("##sense_help", PhosphorIcons.Info, new Vector2(gx + iconW + 4, start.Y), new Vector2(iconW, h), Mute, BtnFill);
        if (ImGuiNET.ImGui.IsItemHovered())
        {
            ImGuiNET.ImGui.BeginTooltip();
            ImGuiNET.ImGui.PushTextWrapPos(ImGuiNET.ImGui.GetFontSize() * 30);
            ImGuiNET.ImGui.TextUnformatted(HelpText);
            ImGuiNET.ImGui.PopTextWrapPos();
            ImGuiNET.ImGui.EndTooltip();
        }
        NextLine(start, h + 4);
    }

    private void AddTerm()
    {
        string text = Utf8(_input);
        bool ok = SenseStore.Add(text, out string message);
        if (ok) WriteUtf8(_input, "");
        ShowMessage(message, ok ? Mute : Amber);
    }

    private void ShowMessage(string text, uint color)
    {
        _message = text;
        _messageColor = color;
        _messageUntilMs = Environment.TickCount64 + 3000;
    }

    // ── Terms ───────────────────────────────────────────────────────────

    private void DrawTerms(float w)
    {
        if (!SenseStore.Loaded)
        {
            Label("Loading the watch list...", Mute);
            return;
        }
        SensePattern[] terms = SenseStore.Terms;
        if (terms.Length == 0)
        {
            Label("No names yet: type one above and press Enter.", Mute);
            return;
        }

        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        ImFontPtr iconFont = ImGuiFonts.Get(UiFont.Ui11);
        Vector2 start = ImGuiNET.ImGui.GetCursorScreenPos();
        float x = start.X, y = start.Y;
        const float iconW = 18, xW = 16;
        int toggle = -1, remove = -1;
        for (int i = 0; i < terms.Length; i++)
        {
            SensePattern t = terms[i];
            float textW = ImGuiNET.ImGui.CalcTextSize(t.Text).X;
            float chipW = Math.Min(w, iconW + textW + 6 + xW);
            if (x > start.X && x + chipW > start.X + w) { x = start.X; y += ChipH + 4; }
            var p = new Vector2(x, y);
            uint fg = t.Enabled ? Text : Faded(Mute);

            ImGuiNET.ImGui.PushID(i);
            ImGuiNET.ImGui.SetCursorScreenPos(p);
            if (ImGuiNET.ImGui.InvisibleButton("##t", new Vector2(chipW - xW, ChipH))) toggle = i;
            bool hotT = ImGuiNET.ImGui.IsItemHovered();
            if (hotT)
                SafeImGui.SetItemTooltip(t.Enabled
                    ? (t.IsGlob ? "On: the whole name must match (wildcards). Click to switch off." : "On: names containing this match. Click to switch off.")
                    : "Off. Click to switch on.");
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(x + chipW - xW, y));
            if (ImGuiNET.ImGui.InvisibleButton("##x", new Vector2(xW, ChipH))) remove = i;
            bool hotX = ImGuiNET.ImGui.IsItemHovered();
            if (hotX) ImGuiNET.ImGui.SetItemTooltip("Remove");
            ImGuiNET.ImGui.PopID();

            uint bg = t.Enabled ? BtnFill : ChipOff;
            if (hotT) bg = Lighten(bg);
            dl.AddRectFilled(p, p + new Vector2(chipW, ChipH), bg, 3);
            dl.AddRect(p, p + new Vector2(chipW, ChipH), t.Enabled ? Teal : BtnBord, 3);
            PhosphorIcons.DrawCentered(dl, iconFont, t.Enabled ? PhosphorIcons.CheckSquare : PhosphorIcons.Square,
                p, new Vector2(iconW, ChipH), t.Enabled ? Teal : fg);
            dl.PushClipRect(p, p + new Vector2(chipW - xW, ChipH), true);
            dl.AddText(new Vector2(x + iconW, y + (ChipH - ImGuiNET.ImGui.GetFontSize()) * 0.5f), fg, t.Text);
            dl.PopClipRect();
            PhosphorIcons.DrawCentered(dl, iconFont, PhosphorIcons.X, new Vector2(x + chipW - xW, y), new Vector2(xW, ChipH),
                hotX ? Red : Faded(Mute));
            x += chipW + 4;
        }
        NextLine(start, y - start.Y + ChipH + 4);

        if (toggle >= 0) SenseStore.SetEnabled(toggle, !terms[toggle].Enabled);
        if (remove >= 0)
        {
            ShowMessage($"Stopped watching for \"{terms[remove].Text}\".", Mute);
            SenseStore.Remove(remove);
        }
    }

    // ── Status and header ───────────────────────────────────────────────

    private void DrawStatus(SenseView view)
    {
        bool msg = _message.Length > 0 && Environment.TickCount64 < _messageUntilMs;
        Label(msg ? _message : view.Status.Length > 0 ? view.Status : "Scanning...", msg ? _messageColor : Mute);
    }

    private static void DrawColumnHeader(float w, bool showLoc)
    {
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        float ty = p.Y + 1;
        float nameW = NameWidth(w, showLoc);
        float x = p.X + ArrowW;
        dl.AddText(new Vector2(x, ty), Mute, "Name");
        x += nameW;
        dl.AddText(new Vector2(x, ty), Mute, "Kind");
        x += KindW;
        dl.AddText(new Vector2(x, ty), Mute, "Dist");
        x += DistW;
        dl.AddText(new Vector2(x, ty), Mute, "Dir");
        x += DirW;
        if (showLoc) dl.AddText(new Vector2(x, ty), Mute, "Location");
        ImGuiNET.ImGui.Dummy(new Vector2(w, ImGuiNET.ImGui.GetFontSize() + 2));
        Separator(w);
    }

    private static float NameWidth(float w, bool showLoc) =>
        Math.Max(40, w - ArrowW - KindW - DistW - DirW - (showLoc ? LocW : 0));

    // ── Rows ────────────────────────────────────────────────────────────

    private void DrawRows(SenseView view, bool showLoc)
    {
        float bw = ImGuiNET.ImGui.GetContentRegionAvail().X;
        SenseRow[] rows = view.Rows;
        _flashing = false;
        if (rows.Length == 0)
        {
            Vector2 at = ImGuiNET.ImGui.GetCursorScreenPos() + new Vector2(6, 6);
            ImGuiNET.ImGui.GetWindowDrawList().AddText(at, Faded(Mute),
                SenseStore.AnyEnabled ? "Nothing matching is in the client's range right now." : "Switch a term on to start watching.");
            return;
        }

        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        long now = Environment.TickCount64;
        uint selected = ClientHelperHooks.GetSelectedItemId();
        float heading = PlayerHeading(out bool haveHeading);
        float nameW = NameWidth(bw, showLoc);
        float fontH = ImGuiNET.ImGui.GetFontSize();

        Vector2 start = ImGuiNET.ImGui.GetCursorScreenPos();
        float scroll = ImGuiNET.ImGui.GetScrollY();
        float visible = ImGuiNET.ImGui.GetWindowHeight();
        int first = Math.Max(0, (int)(scroll / RowH) - 1);
        int last = Math.Min(rows.Length, (int)((scroll + visible) / RowH) + 2);
        int clicked = -1, hovered = -1;

        for (int i = first; i < last; i++)
        {
            SenseRow r = rows[i];
            var p = new Vector2(start.X, start.Y + i * RowH);
            ImGuiNET.ImGui.SetCursorScreenPos(p);
            ImGuiNET.ImGui.PushID(i);
            if (ImGuiNET.ImGui.InvisibleButton("##row", new Vector2(bw, RowH))) clicked = i;
            bool hot = ImGuiNET.ImGui.IsItemHovered();
            ImGuiNET.ImGui.PopID();
            if (hot) hovered = i;

            bool isSel = r.Id == selected;
            uint bg = isSel ? Selected : hot ? Lighten(PanelBg) : (i & 1) == 1 ? RowAlt : 0;
            if (bg != 0) dl.AddRectFilled(p, p + new Vector2(bw, RowH), bg);
            if (r.FirstSeenMs != 0)
            {
                long age = now - r.FirstSeenMs;
                if (age >= 0 && age < FlashMs)
                {
                    _flashing = true;
                    float pulse = 0.5f + 0.5f * MathF.Cos(age * 0.0126f);
                    float a = (1f - age / (float)FlashMs) * (0.2f + 0.35f * pulse);
                    dl.AddRectFilled(p, p + new Vector2(bw, RowH), WithAlpha(Amber, a));
                }
            }
            if (isSel) dl.AddRect(p, p + new Vector2(bw, RowH), Teal);

            Vector2 mid = p + new Vector2(ArrowW * 0.5f, RowH * 0.5f);
            if (r.Measured && haveHeading && !float.IsNaN(r.Bearing))
                DrawArrow(dl, mid, r.Bearing - heading, KindColor(r.Kind));
            else if (r.Measured)
                dl.AddCircleFilled(mid, 2.5f, KindColor(r.Kind));
            else
                dl.AddCircle(mid, 3.5f, Faded(Mute), 12, 1.2f);

            float ty = p.Y + (RowH - fontH) * 0.5f;
            uint nameCol = r.Measured ? Text : Mute;
            float x = p.X + ArrowW;
            Cell(dl, r.Name, x, ty, nameW - 4, nameCol);
            x += nameW;
            Cell(dl, SenseSource.KindName(r.Kind), x, ty, KindW - 4, KindColor(r.Kind));
            x += KindW;
            Cell(dl, r.DistanceText, x, ty, DistW - 4, r.Measured ? Text : Faded(Mute));
            x += DistW;
            Cell(dl, r.CompassText, x, ty, DirW - 4, Mute);
            x += DirW;
            if (showLoc) Cell(dl, r.LocationText, x, ty, bw - (x - p.X) - 4, Mute);
        }

        ImGuiNET.ImGui.SetCursorScreenPos(start);
        ImGuiNET.ImGui.Dummy(new Vector2(bw, rows.Length * RowH));

        if (clicked >= 0)
            AcMainThreadQueue.EnqueueOverlaySelect(rows[clicked].Id);
        if (hovered >= 0)
        {
            ImGuiNET.ImGui.BeginTooltip();
            ImGuiNET.ImGui.TextUnformatted(rows[hovered].Tooltip);
            ImGuiNET.ImGui.EndTooltip();
        }
    }

    private static void Cell(ImDrawListPtr dl, string text, float x, float y, float width, uint color)
    {
        if (text.Length == 0 || width <= 0) return;
        dl.PushClipRect(new Vector2(x, y - 2), new Vector2(x + width, y + 40), true);
        dl.AddText(new Vector2(x, y), color, text);
        dl.PopClipRect();
    }

    /// <summary>
    /// An arrow pointing <paramref name="degrees"/> clockwise from straight up
    /// (up = the way you face).
    /// </summary>
    private static void DrawArrow(ImDrawListPtr dl, Vector2 c, float degrees, uint color)
    {
        float rad = degrees * (MathF.PI / 180f);
        float s = MathF.Sin(rad), co = MathF.Cos(rad);
        Vector2 tip = Rot(c, 0, -6.5f, s, co), left = Rot(c, -4.5f, 5f, s, co),
            notch = Rot(c, 0, 2.5f, s, co), right = Rot(c, 4.5f, 5f, s, co);
        dl.AddTriangleFilled(tip, left, notch, color);
        dl.AddTriangleFilled(tip, notch, right, color);
    }

    private static Vector2 Rot(Vector2 c, float x, float y, float s, float co) =>
        new(c.X + x * co - y * s, c.Y + x * s + y * co);

    /// <summary>Your compass heading this frame from the pose snapshot's quaternion; no AC call.</summary>
    private static float PlayerHeading(out bool ok)
    {
        ok = PlayerPhysicsHooks.TryGetPlayerCompassSnapshot(out float heading);
        return heading;
    }

    private static uint KindColor(SenseKind kind) => kind switch
    {
        SenseKind.Monster => Red,
        SenseKind.Npc => Amber,
        SenseKind.Player => Green,
        SenseKind.Portal => Purple,
        SenseKind.Corpse => Mute,
        _ => Teal,
    };

    private static uint WithAlpha(uint abgr, float a) =>
        (abgr & 0x00FFFFFF) | ((uint)Math.Clamp((int)(a * 255f), 0, 255) << 24);

    // ── Settings (gear) ─────────────────────────────────────────────────

    private void DrawSettingsPopup()
    {
        if (_openSettings)
        {
            _openSettings = false;
            ImGuiNET.ImGui.OpenPopup("##sense_settings");
        }
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.PopupBg, ShellBg);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, Teal);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, Text);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBg, BtnFill);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.CheckMark, Teal);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10, 8));
        bool open = ImGuiNET.ImGui.BeginPopup("##sense_settings");
        ImGuiNET.ImGui.PopStyleVar();
        ImGuiNET.ImGui.PopStyleColor(5);
        if (!open) return;

        Label("When a new match appears:", Mute);
        bool chat = SenseStore.AlertChat, beep = SenseStore.AlertBeep;
        if (ImGuiNET.ImGui.Checkbox("Chat line", ref chat))
            SenseStore.SetAlerts(chat, beep);
        ImGuiNET.ImGui.SetItemTooltip("A line in chat such as: Sense: Dragon's Isle Drake 140 yd NE\n" +
            "(at most three at once; the same object again only after two minutes).");
        if (ImGuiNET.ImGui.Checkbox("System beep", ref beep))
            SenseStore.SetAlerts(chat, beep);
        ImGuiNET.ImGui.SetItemTooltip("Windows' default sound, at most every 3 seconds.");
        Label("Alerts keep working with the panel closed.", Faded(Mute));
        Label("Saved in %LocalAppData%\\RynthCore\\sense.txt", Faded(Mute));
        ImGuiNET.ImGui.EndPopup();
    }
}
