// ============================================================================
//  RynthCore.Engine - ImGui/Panels/ReportFace.cs
//  The "Report a problem" panel (Report/ProblemReport.cs). ImGui only.
//
//    What went wrong?          required, 2,500 bytes of UTF-8 at most
//    What were you doing?      optional, 1,500 bytes
//    [x] Attach my recent RynthCore log      ticked each time the panel opens
//        its size, View (opens exactly what will be sent) and Refresh
//    what else is sent, the Send button and the result
//
//  Opened from the bar's grip menu, Settings' sidebar and /rc report. Draw reads
//  ProblemReport's state and calls Send / RefreshTail / ViewTail, which do their
//  work off AC's thread. The log tail is read when the panel opens (and on
//  Refresh); Send sends that same tail, so what View showed is what goes.
// ============================================================================

using System;
using System.Globalization;
using System.Numerics;
using System.Text;
using ImGuiNET;
using RynthCore.Engine.Report;
using static RynthCore.Engine.ImGuiBackend.Panels.FaceKit;

namespace RynthCore.Engine.ImGuiBackend.Panels;

internal sealed class ReportFace : IImGuiPanel
{
    public const string Title = ProblemReport.PanelTitle;

    public static void Register() => ImGuiPanelHost.Register(Title,
        new PanelSpec(new Vector2(390, 490), new Vector2(300, 440), EdgeToEdge: true, GripInBody: true),
        () => new ReportFace());

    private const string DescId = "##report_desc", DoingId = "##report_doing";
    private static readonly byte[] DescLabel = Encoding.UTF8.GetBytes(DescId + "\0");
    private static readonly byte[] DoingLabel = Encoding.UTF8.GetBytes(DoingId + "\0");

    private readonly byte[] _desc = new byte[ProblemReportCore.MaxDescriptionBytes + 1];
    private readonly byte[] _doing = new byte[ProblemReportCore.MaxDoingBytes + 1];
    private bool _attachLog = true;
    private long _messageSeqAtOpen;
    private long _resultSeqAtSend = -1;   // >= 0 while a send this panel started is out
    private string _localError = "";
    private LogTail? _describedTail;      // the tail _tailText describes (built when the tail changes, not per frame)
    private string _tailText = "";

    private static readonly string LogToggleLabel =
        $"Attach my recent RynthCore log (last {ProblemReportCore.TailLines:N0} lines)";

    public void OnShown()
    {
        _attachLog = true;                     // ticked by default, every time
        _localError = "";
        _messageSeqAtOpen = ProblemReport.MessageSeq;
        ProblemReport.RefreshTail();
    }

    public void Draw()
    {
        // A send this panel started finished: clear the form when it was taken.
        if (_resultSeqAtSend >= 0 && ProblemReport.ResultSeq != _resultSeqAtSend)
        {
            if (ProblemReport.State == ReportState.Sent)
            {
                WriteUtf8(_desc, "");
                WriteUtf8(_doing, "");
            }
            _resultSeqAtSend = -1;
        }

        float w = Begin(out Vector2 origin, out Vector2 size);

        Label("What went wrong? (required)", Text);
        MultiLine(DescId, DescLabel, _desc, w, 92, "What happened, and what did you expect?");
        Label("What were you doing? (optional)", Mute);
        MultiLine(DoingId, DoingLabel, _doing, w, 56, "e.g. hunting in a dungeon with the bot on");
        ImGuiNET.ImGui.Dummy(new Vector2(0, 2));
        Separator(w);

        LogRow(w);
        Separator(w);

        Note("Also sent: your character, server, RynthCore / RynthAi / plugin versions, where you are (landblock and cell) " +
             "and the client's process id. Reports go to the staff only.", w);
        ImGuiNET.ImGui.Dummy(new Vector2(0, 2));
        SendRow(w);

        End(origin, size);
    }

    // ── The log ─────────────────────────────────────────────────────────

    private void LogRow(float w)
    {
        // Square toggle + label; the whole row toggles (Settings' look).
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        float labelW = ImGuiNET.ImGui.CalcTextSize(LogToggleLabel).X;
        if (ImGuiNET.ImGui.InvisibleButton("##report_attach", new Vector2(Math.Min(w, 18 + labelW), 18)))
            _attachLog = !_attachLog;
        bool hot = ImGuiNET.ImGui.IsItemHovered();
        if (hot) ImGuiNET.ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        ImGuiNET.ImGui.SetItemTooltip("Your RynthCore log helps us see what happened. Untick it to send only what you wrote.");
        dl.AddRectFilled(p + new Vector2(0, 3), p + new Vector2(12, 15), _attachLog ? Green : C(0xFF334455), 2);
        dl.AddText(p + new Vector2(18, (18 - ImGuiNET.ImGui.GetFontSize()) * 0.5f), hot ? Teal : Text, LogToggleLabel);

        LogTail? tail = ProblemReport.Tail;
        if (!ReferenceEquals(tail, _describedTail))
        {
            _describedTail = tail;
            _tailText = tail != null ? ProblemReportCore.DescribeTail(tail) : "";
        }
        string info = ProblemReport.TailLoading ? "Reading the log..."
            : tail != null ? _tailText
            : ProblemReport.TailError.Length > 0 ? ProblemReport.TailError : "Reading the log...";
        Wrapped(info, _attachLog ? Mute : Faded(Mute), w);

        Vector2 row = ImGuiNET.ImGui.GetCursorScreenPos();
        string view = PhosphorIcons.Eye + " View what will be sent";
        string refresh = PhosphorIcons.ArrowsClockwise + " Refresh";
        float vw = ButtonWidth(view), rw = ButtonWidth(refresh);
        bool canView = tail != null && !ProblemReport.TailLoading;
        if (Button("##report_view", view, row, new Vector2(vw, 22), Text, BtnFill, enabled: canView))
            ProblemReport.ViewTail(tail!);
        ImGuiNET.ImGui.SetItemTooltip("Opens the exact log text that will be sent, in your text editor.");
        if (Button("##report_refresh", refresh, new Vector2(row.X + vw + 4, row.Y), new Vector2(rw, 22), Text, BtnFill,
                enabled: !ProblemReport.TailLoading))
            ProblemReport.RefreshTail();
        ImGuiNET.ImGui.SetItemTooltip("Take the log again, up to now.");
        NextLine(row, 26);

        Note("The log has your character names and some chat in it (your account, Windows user and PC names are hidden). View shows exactly what will be sent.", w);
    }

    // ── Send ────────────────────────────────────────────────────────────

    private void SendRow(float w)
    {
        ReportState state = ProblemReport.State;
        long cooldown = ProblemReport.CooldownLeftMs;
        bool sending = state == ReportState.Sending;
        bool hasText = _desc[0] != 0;

        Vector2 row = ImGuiNET.ImGui.GetCursorScreenPos();
        string label = sending ? "Sending..." : "Send report";
        float bw = Math.Max(110, ButtonWidth(label));
        bool enabled = hasText && !sending && cooldown == 0 && !(_attachLog && ProblemReport.TailLoading);
        if (Button("##report_send", label, row, new Vector2(bw, 24), Text, StartBg, enabled: enabled))
            StartSend();
        if (ImGuiNET.ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled | ImGuiHoveredFlags.ForTooltip))
            SafeImGui.SetTooltip(!hasText ? "Write what went wrong first."
                : cooldown > 0 ? "One report a minute: " + CooldownText(cooldown)
                : "Send this report to the staff.");
        NextLine(row, 28);

        // The result (only ones from since the panel opened), then the cooldown.
        if (_localError.Length > 0)
            Wrapped(_localError, Amber, w);
        else if (sending || ProblemReport.MessageSeq != _messageSeqAtOpen)
            Wrapped(ProblemReport.Message, state switch
            {
                ReportState.Sent => Green,
                ReportState.Failed => Red,
                _ => Mute,
            }, w);
        if (!sending && cooldown > 0)
            Wrapped(CooldownText(cooldown), Faded(Mute), w);
    }

    // "You can send another report in 42 s.", rebuilt once a second rather than every frame.
    private long _cooldownShownS = -1;
    private string _cooldownText = "";

    private string CooldownText(long ms)
    {
        long sec = (ms + 999) / 1000;
        if (sec != _cooldownShownS)
        {
            _cooldownShownS = sec;
            _cooldownText = $"You can send another report in {ProblemReportCore.FormatWait(ms)}.";
        }
        return _cooldownText;
    }

    private void StartSend()
    {
        string description = Utf8(_desc), doing = Utf8(_doing);
        LogTail? tail = _attachLog ? ProblemReport.Tail : null;
        long seq = ProblemReport.ResultSeq;
        if (ProblemReport.Send(description, doing, tail, readTailIfNull: false, announceInChat: false, out string error))
        {
            _localError = "";
            _resultSeqAtSend = seq;
        }
        else
        {
            _localError = error;
        }
    }

    // ── Widgets ─────────────────────────────────────────────────────────

    /// <summary>A bordered multi-line text box (FaceKit.TextBox's look) with a grey hint while empty.</summary>
    private unsafe void MultiLine(string id, byte[] label, byte[] buffer, float width, float height, string hint)
    {
        Vector2 pos = ImGuiNET.ImGui.GetCursorScreenPos();
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBg, BtnFill);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, Text);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, BtnBord);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(4, 3));
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 3f);
        fixed (byte* l = label)
        fixed (byte* b = buffer)
        {
            if (ImGuiNative.igInputTextMultiline(l, b, (uint)buffer.Length, new Vector2(width, height),
                    ImGuiInputTextFlags.None, null, null) != 0)
                _localError = "";
        }
        bool active = ImGuiNET.ImGui.IsItemActive();
        if (active) ImGuiTextFocus.NoteActive(id);   // keys stay out of AC
        ImGuiNET.ImGui.PopStyleVar(3);
        ImGuiNET.ImGui.PopStyleColor(3);
        if (buffer[0] == 0 && !active)
            ImGuiNET.ImGui.GetWindowDrawList().AddText(pos + new Vector2(5, 3), Faded(Mute), hint);
    }

    private static void Wrapped(string text, uint color, float w)
    {
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGuiNET.ImGui.PushTextWrapPos(ImGuiNET.ImGui.GetCursorPosX() + w);
        ImGuiNET.ImGui.TextUnformatted(text);
        ImGuiNET.ImGui.PopTextWrapPos();
        ImGuiNET.ImGui.PopStyleColor();
    }

    private static void Note(string text, float w) => Wrapped(text, Faded(Mute), w);

    private static uint C(uint argb) => RynthTheme.Argb(argb);
}
