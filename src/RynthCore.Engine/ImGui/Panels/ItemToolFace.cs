// ============================================================================
//  RynthCore.Engine - ImGui/Panels/ItemToolFace.cs
//  The Item Tool panel: two small tools of RynthAi's, each picked, started and
//  stopped by the player. Engine only, ImGui only; the work is RynthAi's
//  (RynthAiPlugin.ItemTool.cs), this is its buttons and status lines.
//
//    Chest looter  "Pick a chest. Pick a key. It just loots the chest based on the
//                  current loot profile until it's out of keys."
//                  Select the chest in the world, press Use selected as chest;
//                  select the key in your pack, press Use selected as key; Start.
//                  Chest re-lock wait (s): how long after the open the next key
//                  goes on (RynthAi's setting, "/ra itemtool relock N").
//    Quick give    Select an item in your pack (Use selected as item) and a player
//                  or NPC in the world (Use selected as target), press Give: every
//                  such item in your packs is handed over, one give at a time.
//                  The target's name is shown (PLAYER for a player).
//
//  Data: ItemToolSource (RynthPluginGetItemToolJson, 250 ms while open). Buttons
//  post words to RynthPluginItemToolCommand (ItemToolCommands.Send), the same
//  words as "/ra itemtool ...". Draw reads a snapshot and posts; no AC calls.
// ============================================================================

using System;
using System.Numerics;
using ImGuiNET;
using RynthCore.Engine.UI.Data;
using static RynthCore.Engine.ImGuiBackend.Panels.FaceKit;

namespace RynthCore.Engine.ImGuiBackend.Panels;

internal sealed class ItemToolFace : IImGuiPanel
{
    public const string Title = "Item Tool";

    public static void Register() => ImGuiPanelHost.Register(Title,
        new PanelSpec(new Vector2(320, 400), new Vector2(240, 260), EdgeToEdge: true, GripInBody: true),
        () => new ItemToolFace());

    public void OnShown()
    {
        UiSources.ItemTool.Subscribe();
        UiSources.ItemTool.RequestRefresh();
    }

    public void OnHidden() => UiSources.ItemTool.Unsubscribe();

    public void Draw()
    {
        float w = Begin(out Vector2 origin, out Vector2 size);
        ItemToolSnapshot? snap = UiSources.ItemTool.Current?.Value;
        float bodyH = Math.Max(30, Remaining(origin, size) - GripOverlap());
        ImGuiNET.ImGui.BeginChild("##itemtool_body", new Vector2(w, bodyH));
        if (snap == null)
            Wrapped("Waiting for RynthAi (log in, and RynthAi must be loaded).", Mute, w);
        else
        {
            DrawChest(snap.Chest, w);
            ImGuiNET.ImGui.Dummy(new Vector2(0, 4));
            Separator(w);
            DrawGive(snap.Give, w);
        }
        ImGuiNET.ImGui.EndChild();
        End(origin, size);
    }

    // ── Chest looter ────────────────────────────────────────────────────

    private static void DrawChest(ChestToolState c, float w)
    {
        Label("Chest looter", Teal);
        Wrapped("Loots the chest with the current loot profile, then waits for it to re-lock and goes again, until the key is used up.", Mute, w);
        Pick("Chest:", c.Name, "(none: select the chest in the world)", w);
        Pick("Key:", c.Key, "(none: select the key in your pack)", w);

        bool ready = c.Name.Length > 0 && c.Key.Length > 0;
        int hit = WrapButtons("##chest_pick", new[] { "Use selected as chest", "Use selected as key" }, w, 22,
            new[] { !c.Running, !c.Running });
        if (hit == 0) ItemToolCommands.Send("chest set");
        else if (hit == 1) ItemToolCommands.Send("chest key");

        DrawRelockWait(c, w);

        hit = StartStop("##chest_run", "Start", "Stop", ready && !c.Running, c.Running, w);
        if (hit == 0) ItemToolCommands.Send("chest start");
        else if (hit == 1) ItemToolCommands.Send("chest stop");

        Wrapped(c.Status, c.Running ? Amber : Text, w);
    }

    // The slider's value while it is dragged, and after the release until RynthAi reports it back
    // (the snapshot is 250 ms behind; without this the slider jumps back for a moment).
    private static int _relockEdit;
    private static bool _relockPending;
    private static long _relockSentAt;
    private const long RelockPendingMs = 2000;

    /// <summary>
    /// "Chest re-lock wait (s)" (2026-11-05): how long after the chest was opened the next key goes on
    /// it. Sent on release as "relock N" (RynthAi clamps, saves it in the profile and answers in chat).
    /// Hidden for a RynthAi that predates it (no relockSec); greyed until the profile has loaded.
    /// </summary>
    private static void DrawRelockWait(ChestToolState c, float w)
    {
        if (c.RelockSec <= 0) return;
        int min = Math.Max(1, c.RelockMin), max = Math.Max(min, c.RelockMax);
        long now = Environment.TickCount64;
        if (_relockPending && (c.RelockSec == _relockEdit || now - _relockSentAt >= RelockPendingMs)) _relockPending = false;

        Label("Chest re-lock wait (s)", Mute);
        int v = _relockPending ? _relockEdit : c.RelockSec;
        ImGuiNET.ImGui.BeginDisabled(!c.RelockReady);
        ImGuiNET.ImGui.SetNextItemWidth(Math.Max(80, w - 4));
        ImGuiNET.ImGui.SliderInt("##chest_relock", ref v, min, max, "%d s", ImGuiSliderFlags.AlwaysClamp);
        if (ImGuiNET.ImGui.IsItemActive()) { _relockEdit = v; _relockPending = true; _relockSentAt = now; }
        if (ImGuiNET.ImGui.IsItemDeactivatedAfterEdit())
        {
            _relockEdit = v;
            _relockPending = true;
            _relockSentAt = now;
            ItemToolCommands.Send("relock " + v.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        SafeImGui.SetItemTooltip(c.RelockReady
            ? "How long after the chest was opened the next key goes on it (sooner when it reads locked again).\n125 s fits the usual 2-minute chest reset; a chest that resets sooner can use less.\nToo short costs nothing: the key is only tried again, never spent. Ctrl+click to type a value."
            : "Waiting for this character's settings profile to load.");
        ImGuiNET.ImGui.EndDisabled();
    }

    // ── Quick give ──────────────────────────────────────────────────────

    private static void DrawGive(GiveToolState g, float w)
    {
        Label("Quick give", Teal);
        Wrapped("Hands every matching item in your packs to one player or NPC, one give at a time, until none is left or you press Stop. Never worn gear.", Mute, w);
        Pick("Item:", g.Item, "(none: select the item in your pack)", w);
        Pick("Target:", g.Target, "(none: select the player or NPC in the world)", w);

        bool ready = g.Item.Length > 0 && g.Target.Length > 0;
        int hit = WrapButtons("##give_pick", new[] { "Use selected as item", "Use selected as target" }, w, 22,
            new[] { !g.Running, !g.Running });
        if (hit == 0) ItemToolCommands.Send("giveitem");
        else if (hit == 1) ItemToolCommands.Send("givetarget");

        hit = StartStop("##give_run", "Give", "Stop", ready && !g.Running, g.Running, w);
        if (hit == 0) ItemToolCommands.Send("give");
        else if (hit == 1) ItemToolCommands.Send("givestop");

        Wrapped(g.Status, g.Running ? Amber : Text, w);
    }

    // ── Pieces ──────────────────────────────────────────────────────────

    /// <summary>"Label  value" (amber while nothing is picked, the hint then).</summary>
    private static void Pick(string label, string value, string hint, float w)
    {
        Label(label, Mute);
        ImGuiNET.ImGui.SameLine(0, 6);
        bool none = value.Length == 0;
        Wrapped(none ? hint : value, none ? Mute : Text, Math.Max(40, w - ImGuiNET.ImGui.CalcTextSize(label).X - 10));
    }

    /// <summary>A Start and a Stop button side by side; returns 0 (start), 1 (stop) or -1.</summary>
    private static int StartStop(string id, string startLabel, string stopLabel, bool canStart, bool canStop, float w)
    {
        Vector2 start = ImGuiNET.ImGui.GetCursorScreenPos();
        const float h = 24;
        float bw = Math.Max(ButtonWidth(startLabel), ButtonWidth(stopLabel)) + 20;
        int hit = -1;
        ImGuiNET.ImGui.PushID(id);
        if (Button("##start", startLabel, start, new Vector2(bw, h), Text, StartBg, canStart)) hit = 0;
        if (Button("##stop", stopLabel, new Vector2(start.X + bw + 6, start.Y), new Vector2(bw, h), Text, StopBg, canStop)) hit = 1;
        ImGuiNET.ImGui.PopID();
        NextLine(start, h + 4);
        return hit;
    }

    private static void Wrapped(string text, uint color, float w)
    {
        if (text.Length == 0) return;
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGuiNET.ImGui.PushTextWrapPos(ImGuiNET.ImGui.GetCursorPosX() + w);
        ImGuiNET.ImGui.TextUnformatted(text);
        ImGuiNET.ImGui.PopTextWrapPos();
        ImGuiNET.ImGui.PopStyleColor();
    }
}
