// ============================================================================
//  RynthCore.Engine - ImGui/Panels/Classic/ClassicSettings.cs
//  The Classic tabs' copy of RynthAi's settings, kept and saved the way the
//  Settings face and the Ranges drawer do it: a private clone of
//  UiSources.Settings, a schema row's Set on the clone, SettingsCommands.Save
//  of the whole copy (RynthAi saves it to the profile). A newer fetch replaces
//  the copy unless a control is being typed in or dragged.
//
//  Also the number-box rows (label | box) and gem rows the Main tab draws for
//  schema rows. AC's render thread only.
// ============================================================================

using System;
using System.Globalization;
using System.Numerics;
using System.Text;
using RynthCore.Engine.UI;
using RynthCore.Engine.UI.Data;
using static RynthCore.Engine.ImGuiBackend.Panels.Classic.ClassicKit;

namespace RynthCore.Engine.ImGuiBackend.Panels.Classic;

internal sealed class ClassicSettings
{
    private RynthAiSettings? _data;
    private long _seenVersion = -1;
    private bool _subscribed;

    /// <summary>A control was typed in or dragged this frame: hold the copy.</summary>
    public bool Busy;

    public RynthAiSettings? Data => _data;

    public void Subscribe()
    {
        if (_subscribed) return;
        _subscribed = true;
        UiSources.Settings.Subscribe();
        UiSources.Settings.RequestRefresh();
    }

    public void Unsubscribe()
    {
        if (!_subscribed) return;
        _subscribed = false;
        UiSources.Settings.Unsubscribe();
    }

    /// <summary>Start of a frame: take a newer fetch unless a control is in use.</summary>
    public void Sync(bool force = false)
    {
        var snap = UiSources.Settings.Current;
        if (snap == null || snap.Version == _seenVersion || (Busy && !force)) return;
        _seenVersion = snap.Version;
        _data = snap.Value.Clone();
    }

    /// <summary>Sets <paramref name="row"/> to <paramref name="value"/> and saves the whole copy.</summary>
    public void Set(SettingRow row, double value)
    {
        Sync(force: true);
        if (_data == null || row.Set == null) return;
        row.Set(_data, value);
        SettingsCommands.Save(_data.Clone());
    }

    public double Get(SettingRow row) => _data != null && row.Get != null ? row.Get(_data) : 0;

    /// <summary>A schema row by its Settings label; null when the schema doesn't have it (left out of the tab).</summary>
    public static SettingRow? Row(string label)
    {
        SettingRow? r = SettingsSchema.Find(label);
        return r?.Get != null && r.Set != null ? r : null;
    }

    // ── A number row: label | retail number box ─────────────────────────

    internal sealed class NumberRow
    {
        public readonly SettingRow? Setting;
        public readonly string Label;
        public readonly byte[] Buffer = new byte[24];
        public double Shown = double.NaN;
        public bool Typing;

        public NumberRow(string schemaLabel, string label)
        {
            Setting = Row(schemaLabel);
            Label = label;
        }

        public bool IsInt => Setting?.Kind == SettingKind.Int;
    }

    /// <summary>Draws a number row (h retail px high); true while it's typed in.</summary>
    public bool DrawNumber(NumberRow r, Vector2 pos, float w, float boxW = 40, float h = 20)
    {
        if (r.Setting == null || _data == null) return false;
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        SettingRow s = r.Setting;
        float rh = Px(h);
        if (!s.IsVisible(_data))
        {
            // A setting this RynthAi doesn't send yet (older plugin): shown, not editable.
            TextClip(dl, pos.X + Px(3), pos.Y, rh, pos.X + w, r.Label, Faint);
            ImGuiNET.ImGui.SetCursorScreenPos(pos);
            ImGuiNET.ImGui.InvisibleButton("##na_" + r.Label, new Vector2(Math.Max(1, w), rh));
            SafeImGui.SetItemTooltip(NeedsNewer);
            return false;
        }
        double value = Math.Clamp(Get(s), s.Min, s.Max);
        TextClip(dl, pos.X + Px(3), pos.Y, rh, pos.X + w - Px(boxW) - Px(6), r.Label, Text);
        if (!r.Typing && r.Shown != value) Write(r, value);
        ImGuiNET.ImGui.PushID(r.Label);
        var boxPos = new Vector2(pos.X + w - Px(boxW), pos.Y + (rh - Px(17)) * 0.5f);
        bool done = NumberBox("##n", r.Buffer, boxPos, Px(boxW), out bool active);
        r.Typing = active;
        SafeImGui.SetItemTooltip(s.Tooltip ?? r.Label);
        ImGuiNET.ImGui.PopID();
        if (done)
        {
            if (TryParse(r.Buffer, out double typed))
            {
                double v = Math.Clamp(r.IsInt ? Math.Round(typed) : typed, s.Min, s.Max);
                Set(s, v);
                Write(r, v);
            }
            else Write(r, value);   // bad input reverts
        }
        return r.Typing;
    }

    /// <summary>Draws a schema bool row as a gem (h retail px high).</summary>
    public void DrawGem(SettingRow? row, string label, Vector2 pos, float w, float h = 18)
    {
        if (row == null || _data == null) return;
        if (!row.IsVisible(_data))
        {
            // A setting this RynthAi doesn't send yet (older plugin): shown dimmed, not clickable.
            var dl = ImGuiNET.ImGui.GetWindowDrawList();
            float rh = Px(h);
            GemPicture(dl, new Vector2(pos.X + Px(3), pos.Y + (rh - Px(13)) * 0.5f), false);
            TextClip(dl, pos.X + Px(22), pos.Y, rh, pos.X + w, label, Faint);
            ImGuiNET.ImGui.SetCursorScreenPos(pos);
            ImGuiNET.ImGui.InvisibleButton("##na_" + label, new Vector2(Math.Max(1, w), rh));
            SafeImGui.SetItemTooltip(NeedsNewer);
            return;
        }
        bool on = Get(row) != 0;
        if (Gem("##g_" + label, label, pos, w, on, h))
            Set(row, on ? 0 : 1);
        SafeImGui.SetItemTooltip(row.Tooltip ?? label);
    }

    private const string NeedsNewer = "This setting needs a newer RynthAi (from 2026-10-19).";

    public static void Write(NumberRow r, double value)
    {
        string text = r.IsInt ? ((long)Math.Round(value)).ToString(CultureInfo.InvariantCulture)
                              : value.ToString("G4", CultureInfo.InvariantCulture);
        int n = Encoding.UTF8.GetBytes(text, 0, text.Length, r.Buffer, 0);
        r.Buffer[Math.Min(n, r.Buffer.Length - 1)] = 0;
        r.Shown = value;
    }

    public static bool TryParse(byte[] buffer, out double value)
    {
        int len = Array.IndexOf(buffer, (byte)0);
        if (len < 0) len = buffer.Length;
        return double.TryParse(Encoding.UTF8.GetString(buffer, 0, len), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
