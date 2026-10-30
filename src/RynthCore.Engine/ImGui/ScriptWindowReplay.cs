// ============================================================================
//  RynthCore.Engine - ImGui/ScriptWindowReplay.cs
//  Script windows (API v71): the op interpreter. Replays a window's latest
//  DisplayList with real ImGui calls every frame and turns what ImGui did
//  into events for the owner (clicks, new values, text, open states).
//  Design: RynthSuite Docs/RYNTHLUA_WINDOWS_DESIGN.md §2.5-2.7, §4.2, §6.3.
//
//  AC's thread only, inside the panel host's window and try/catch. No
//  allocation on a frame without input: ops are pre-parsed, strings are pinned
//  NUL-terminated UTF-8 handed to cimgui as byte pointers.
//
//  Safety:
//    - Script text never reaches cimgui as a format string: TextUnformatted
//      for every text op (colour / wrap / bullet done with pushes around it),
//      LabelText's text was stored with '%' doubled, SeparatorText takes a
//      label, slider formats were validated by the parser.
//    - The engine owns every Begin/End and Push/Pop. Scopes are an explicit
//      stack (no recursion): leftover PushIDs / PushStyleColors are popped at
//      the end of their scope, then TreePop / EndChild. An exception mid-list
//      unwinds every open scope in a finally before it propagates to
//      ImGuiPanelHost.DrawBody, so ImGui's stacks stay balanced.
//    - Icons (Image / ImageButton, op level 2) are ids: ScriptIcons turns them
//      into cached D3D9 textures; until one is ready, or when it is missing,
//      a dim placeholder square of the same size draws (the font atlas's white
//      pixel), so a bad id never throws.
//    - Sizes and offsets in ops are logical at UI scale 1: multiplied by
//      S = EngineFrameController.FontScale here (the sign is kept, so 0 = auto
//      and -1 = fill still work).
//
//  Measurements (Phase 0): replay cost per frame and the input -> reaction
//  latency are logged every ~10 s while a script window is open
//  ("ScriptWindows replay:" lines, UI tag).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ImGuiNET;
using RynthCore.Engine.UI;
using RynthCore.Engine.UI.ScriptWindows;

namespace RynthCore.Engine.ImGuiBackend;

internal static unsafe class ScriptWindowReplay
{
    private const byte ScopeRoot = 0, ScopeChild = 1, ScopeTree = 2;

    /// <summary>One open scope: the root body, a Child or an open TreeNode.</summary>
    private struct Scope
    {
        public byte Kind;
        /// <summary>A CollapsingHeader the player just closed: drawing ops are skipped until the next header.</summary>
        public bool Skip;
        /// <summary>The op index just past the scope's last child.</summary>
        public int End;
        public int Ids;
        public int Colors;
    }

    // AC thread only. Root + at most MaxDepth nested blocks.
    private static readonly Scope[] Scopes = new Scope[DisplayListParser.MaxDepth + 1];
    private static int _frame = int.MinValue;
    private static int _frameOps;

    private static readonly Vector4 ErrorColor = new(1f, 0.4f, 0.3f, 1f);

    /// <summary>Draws <paramref name="w"/>'s latest list into the current window. AC thread.</summary>
    public static void Draw(ScriptWindow w)
    {
        int frame = ImGuiNative.igGetFrameCount();
        if (frame != _frame)
        {
            _frame = frame;
            _frameOps = 0;
        }

        DisplayList list = w.List;
        long t0 = Stopwatch.GetTimestamp();
        DrawError(w);

        int ops = 0;
        bool over = false;
        int budget = ScriptWindowRegistry.MaxReplayOpsPerFrame - _frameOps;
        if (list.OpCount > 0 && budget <= 0)
            over = true;
        else if (list.OpCount > 0)
        {
            ops = Replay(w, list, frame, budget, out over);
            _frameOps += ops;
        }
        if (over)
        {
            // Over the per-frame budget for all script windows together.
            ReadOnlySpan<byte> msg = "Too large to draw this frame."u8;
            fixed (byte* m = msg) ImGuiNative.igTextUnformatted(m, m + msg.Length);
        }
        long elapsed = Stopwatch.GetTimestamp() - t0;

        // First frame this list is on screen: if it was recorded after the owner applied the
        // pending input, that input has had its reaction.
        if (!ReferenceEquals(list, w.LastReplayed))
        {
            w.LastReplayed = list;
            w.Locals.PruneIfLarge(frame);
            if (w.PendingInputSeq != 0 && list.AckSeq >= w.PendingInputSeq)
            {
                ScriptWindowStats.Reaction(w, frame - w.PendingInputFrame, Stopwatch.GetTimestamp() - w.PendingInputTicks);
                w.PendingInputSeq = 0;
            }
        }
        ScriptWindowStats.Replay(frame, elapsed, ops);
    }

    /// <summary>The script's error (HasError) in red, wrapped at the window edge, above the list.</summary>
    private static void DrawError(ScriptWindow w)
    {
        if ((w.Flags & WindowFlags.HasError) == 0) return;
        byte[]? err = w.ErrorUtf8;
        if (err == null || err.Length < 1) return;
        ImGuiNative.igPushStyleColor_Vec4(ImGuiCol.Text, ErrorColor);
        ImGuiNative.igPushTextWrapPos(0f);
        try
        {
            fixed (byte* e = err) ImGuiNative.igTextUnformatted(e, e + err.Length - 1);
        }
        finally
        {
            ImGuiNative.igPopTextWrapPos();
            ImGuiNative.igPopStyleColor(1);
        }
        ImGuiNative.igSeparator();
    }

    /// <summary>
    /// Walks the ops with an explicit scope stack. Returns the ops visited; <paramref name="over"/>
    /// is set when the per-frame budget ran out before the list ended (the rest isn't drawn).
    /// </summary>
    private static int Replay(ScriptWindow w, DisplayList list, int frame, int budget, out bool over)
    {
        over = false;
        int n = list.OpCount;
        ReplayOp[] ops = list.Ops;
        float s = EngineFrameController.FontScale;
        if (!(s > 0f) || !float.IsFinite(s)) s = 1f;
        uint ack = list.AckSeq;
        ScriptLocals locals = w.Locals;
        ScriptEventQueue events = w.Owner.Events;
        int visited = 0;
        int open = 0;   // scopes whose closing calls are still owed

        fixed (byte* pool = list.Pool)
        {
            try
            {
                Scopes[0] = new Scope { Kind = ScopeRoot, End = n };
                open = 1;
                int i = 0;
                while (open > 0)
                {
                    ref Scope sc = ref Scopes[open - 1];
                    if (i >= sc.End)
                    {
                        open--;
                        CloseScope(ref sc);
                        continue;
                    }
                    if (visited >= budget) { over = true; break; }
                    visited++;

                    ref ReplayOp op = ref ops[i];
                    if (sc.Skip)
                    {
                        if (op.Code == ScriptOp.CollapsingHeader) sc.Skip = false;
                        else if (op.Code is not (ScriptOp.PushID or ScriptOp.PopID or ScriptOp.PushStyleColor or ScriptOp.PopStyleColor))
                        {
                            // IDs and colours still run, so the ID stack after the next header is right.
                            i = op.EndIndex;
                            continue;
                        }
                    }

                    int next = i + 1;
                    byte* str = op.Str >= 0 ? pool + op.Str : null;
                    switch (op.Code)
                    {
                        // ── Text ──────────────────────────────────────────
                        case ScriptOp.Text:
                            ImGuiNative.igTextUnformatted(str, str + op.StrLen);
                            break;
                        case ScriptOp.TextColored:
                            ImGuiNative.igPushStyleColor_U32(ImGuiCol.Text, op.Color);
                            try { ImGuiNative.igTextUnformatted(str, str + op.StrLen); }
                            finally { ImGuiNative.igPopStyleColor(1); }
                            break;
                        case ScriptOp.TextWrapped:
                            ImGuiNative.igPushTextWrapPos(0f);
                            try { ImGuiNative.igTextUnformatted(str, str + op.StrLen); }
                            finally { ImGuiNative.igPopTextWrapPos(); }
                            break;
                        case ScriptOp.TextDisabled:
                        {
                            Vector4 disabled = *ImGuiNative.igGetStyleColorVec4(ImGuiCol.TextDisabled);
                            ImGuiNative.igPushStyleColor_Vec4(ImGuiCol.Text, disabled);
                            try { ImGuiNative.igTextUnformatted(str, str + op.StrLen); }
                            finally { ImGuiNative.igPopStyleColor(1); }
                            break;
                        }
                        case ScriptOp.BulletText:
                            ImGuiNative.igBullet();
                            ImGuiNative.igTextUnformatted(str, str + op.StrLen);
                            break;
                        case ScriptOp.SeparatorText:
                            ImGuiNative.igSeparatorText(str);   // a label, not a format
                            break;
                        case ScriptOp.LabelText:
                            ImGuiNative.igLabelText(str, pool + op.Str2);   // text stored with '%' doubled
                            break;

                        // ── Layout ────────────────────────────────────────
                        case ScriptOp.Separator:
                            ImGuiNative.igSeparator();
                            break;
                        case ScriptOp.SameLine:
                            ImGuiNative.igSameLine(op.A * s, op.B < 0f ? -1f : op.B * s);
                            break;
                        case ScriptOp.NewLine:
                            ImGuiNative.igNewLine();
                            break;
                        case ScriptOp.Spacing:
                            ImGuiNative.igSpacing();
                            break;
                        case ScriptOp.Dummy:
                            ImGuiNative.igDummy(new Vector2(op.A * s, op.B * s));
                            break;
                        case ScriptOp.Indent:
                            ImGuiNative.igIndent(op.A * s);
                            break;
                        case ScriptOp.Unindent:
                            ImGuiNative.igUnindent(op.A * s);
                            break;

                        // ── Widgets ───────────────────────────────────────
                        case ScriptOp.Button:
                        {
                            byte clicked = op.Flag != 0
                                ? ImGuiNative.igSmallButton(str)
                                : ImGuiNative.igButton(str, new Vector2(op.A * s, op.B * s));
                            if (clicked != 0)
                                w.NoteInput(events.Click(w.Hash, op.Key), frame);
                            break;
                        }
                        case ScriptOp.Checkbox:
                        {
                            bool had = locals.Bools.TryGet(op.Key, ack, out bool local);
                            byte v = had ? (local ? (byte)1 : (byte)0) : op.Flag;
                            bool changed = ImGuiNative.igCheckbox(str, &v) != 0;
                            bool active = ImGuiNative.igIsItemActive() != 0;
                            uint seq = 0;
                            if (changed)
                            {
                                seq = events.Bool(w.Hash, op.Key, v != 0);
                                w.NoteInput(seq, frame);
                            }
                            locals.Bools.After(op.Key, had, changed, v != 0, seq, active, frame);
                            break;
                        }
                        case ScriptOp.SliderInt:
                        {
                            bool had = locals.Ints.TryGet(op.Key, ack, out int local);
                            int v = had ? local : op.I0;
                            bool changed = ImGuiNative.igSliderInt(str, &v, op.I1, op.I2, pool + op.Str2, ImGuiSliderFlags.AlwaysClamp) != 0;
                            bool active = ImGuiNative.igIsItemActive() != 0;
                            uint seq = 0;
                            if (changed)
                            {
                                seq = events.Int(w.Hash, op.Key, v);
                                w.NoteInput(seq, frame);
                            }
                            locals.Ints.After(op.Key, had, changed, v, seq, active, frame);
                            break;
                        }
                        case ScriptOp.SliderFloat:
                        {
                            bool had = locals.Floats.TryGet(op.Key, ack, out float local);
                            float v = had ? local : op.A;
                            bool changed = ImGuiNative.igSliderFloat(str, &v, op.B, op.C, pool + op.Str2, ImGuiSliderFlags.AlwaysClamp) != 0;
                            bool active = ImGuiNative.igIsItemActive() != 0;
                            uint seq = 0;
                            if (changed && float.IsFinite(v))
                            {
                                seq = events.Float(w.Hash, op.Key, v);
                                w.NoteInput(seq, frame);
                            }
                            else changed = false;
                            locals.Floats.After(op.Key, had, changed, v, seq, active, frame);
                            break;
                        }
                        case ScriptOp.Combo:
                        {
                            bool had = locals.Ints.TryGet(op.Key, ack, out int local);
                            int v = had ? local : op.I0;
                            bool changed = ImGuiNative.igCombo_Str(str, &v, pool + op.Str2, -1) != 0;
                            bool active = ImGuiNative.igIsItemActive() != 0;
                            uint seq = 0;
                            if (changed)
                            {
                                seq = events.Int(w.Hash, op.Key, v);
                                w.NoteInput(seq, frame);
                            }
                            locals.Ints.After(op.Key, had, changed, v, seq, active, frame);
                            break;
                        }
                        case ScriptOp.InputText:
                            ReplayInputText(w, ref op, str, pool, ack, frame);
                            break;
                        case ScriptOp.Selectable:
                            if (ImGuiNative.igSelectable_Bool(str, op.Flag, (ImGuiSelectableFlags)op.Flags, new Vector2(op.A * s, op.B * s)) != 0)
                                w.NoteInput(events.Click(w.Hash, op.Key), frame);
                            break;
                        case ScriptOp.ProgressBar:
                            ImGuiNative.igProgressBar(op.A, new Vector2(op.B * s, op.C * s), op.Str2 >= 0 ? pool + op.Str2 : null);
                            break;

                        // ── Icons and number inputs (op level 2) ──────────
                        case ScriptOp.Image:
                        {
                            var size = new Vector2(op.A * s, op.B * s);
                            if (ScriptIcons.TryGet((ScriptIconKind)op.Flag, unchecked((uint)op.I0), out IntPtr tex))
                                ImGuiNative.igImage(tex, size, new Vector2(op.C, op.D), new Vector2(op.E, op.F), Rgba(op.Color), Rgba(op.Color2));
                            else
                                Placeholder(size, Rgba(op.Color2));
                            break;
                        }
                        case ScriptOp.ImageButton:
                        {
                            var size = new Vector2(op.A * s, op.B * s);
                            byte clicked;
                            if (ScriptIcons.TryGet((ScriptIconKind)op.Flag, unchecked((uint)op.I0), out IntPtr tex))
                                clicked = ImGuiNative.igImageButton(str, tex, size, new Vector2(op.C, op.D), new Vector2(op.E, op.F), Rgba(op.Color), Rgba(op.Color2));
                            else
                            {
                                // The same button (same ID, size and frame) with the placeholder inside.
                                WhitePixel(out IntPtr white, out Vector2 uv);
                                clicked = ImGuiNative.igImageButton(str, white, size, uv, uv, Rgba(op.Color), PlaceholderFill);
                            }
                            if (clicked != 0)
                                w.NoteInput(events.Click(w.Hash, op.Key), frame);
                            break;
                        }
                        case ScriptOp.InputInt:
                        {
                            bool had = locals.Ints.TryGet(op.Key, ack, out int local);
                            int v = had ? local : op.I0;
                            bool changed = ImGuiNative.igInputInt(str, &v, op.I1, op.I2, (ImGuiInputTextFlags)op.Flags) != 0;
                            bool active = ImGuiNative.igIsItemActive() != 0;
                            if (active) ImGuiTextFocus.NoteActive("number box");
                            uint seq = 0;
                            if (changed)
                            {
                                seq = events.Int(w.Hash, op.Key, v);
                                w.NoteInput(seq, frame);
                            }
                            locals.Ints.After(op.Key, had, changed, v, seq, active, frame);
                            break;
                        }
                        case ScriptOp.InputFloat:
                        {
                            bool had = locals.Floats.TryGet(op.Key, ack, out float local);
                            float v = had ? local : op.A;
                            bool changed = ImGuiNative.igInputFloat(str, &v, op.B, op.C, pool + op.Str2, (ImGuiInputTextFlags)op.Flags) != 0;
                            bool active = ImGuiNative.igIsItemActive() != 0;
                            if (active) ImGuiTextFocus.NoteActive("number box");
                            uint seq = 0;
                            if (changed && float.IsFinite(v))
                            {
                                seq = events.Float(w.Hash, op.Key, v);
                                w.NoteInput(seq, frame);
                            }
                            else changed = false;
                            locals.Floats.After(op.Key, had, changed, v, seq, active, frame);
                            break;
                        }
                        case ScriptOp.DragInt:
                        {
                            bool had = locals.Ints.TryGet(op.Key, ack, out int local);
                            int v = had ? local : op.I0;
                            bool changed = ImGuiNative.igDragInt(str, &v, op.D, op.I1, op.I2, pool + op.Str2, (ImGuiSliderFlags)op.Flags) != 0;
                            bool active = ImGuiNative.igIsItemActive() != 0;
                            uint seq = 0;
                            if (changed)
                            {
                                seq = events.Int(w.Hash, op.Key, v);
                                w.NoteInput(seq, frame);
                            }
                            locals.Ints.After(op.Key, had, changed, v, seq, active, frame);
                            break;
                        }
                        case ScriptOp.DragFloat:
                        {
                            bool had = locals.Floats.TryGet(op.Key, ack, out float local);
                            float v = had ? local : op.A;
                            bool changed = ImGuiNative.igDragFloat(str, &v, op.D, op.B, op.C, pool + op.Str2, (ImGuiSliderFlags)op.Flags) != 0;
                            bool active = ImGuiNative.igIsItemActive() != 0;
                            uint seq = 0;
                            if (changed && float.IsFinite(v))
                            {
                                seq = events.Float(w.Hash, op.Key, v);
                                w.NoteInput(seq, frame);
                            }
                            else changed = false;
                            locals.Floats.After(op.Key, had, changed, v, seq, active, frame);
                            break;
                        }

                        // ── IDs, style, tooltips ──────────────────────────
                        case ScriptOp.PushID:
                            if (op.Flag == 0) ImGuiNative.igPushID_Str(str);
                            else ImGuiNative.igPushID_Int(op.I0);
                            sc.Ids++;
                            break;
                        case ScriptOp.PopID:
                            if (sc.Ids > 0)
                            {
                                sc.Ids--;
                                ImGuiNative.igPopID();
                            }
                            break;
                        case ScriptOp.PushStyleColor:
                            ImGuiNative.igPushStyleColor_U32((ImGuiCol)op.Aux, op.Color);
                            sc.Colors++;
                            break;
                        case ScriptOp.PopStyleColor:
                        {
                            int count = Math.Min((int)op.Aux, sc.Colors);
                            if (count > 0)
                            {
                                sc.Colors -= count;
                                ImGuiNative.igPopStyleColor(count);
                            }
                            break;
                        }
                        case ScriptOp.SetItemTooltip:
                            if (ImGuiNative.igIsItemHovered(ImGuiHoveredFlags.ForTooltip) != 0 && ImGuiNative.igBeginTooltip() != 0)
                            {
                                try { ImGuiNative.igTextUnformatted(str, str + op.StrLen); }
                                finally { ImGuiNative.igEndTooltip(); }
                            }
                            break;

                        // ── Containers ────────────────────────────────────
                        case ScriptOp.Child:
                        {
                            ImGuiChildFlags childFlags = op.Flag != 0 ? ImGuiChildFlags.Borders : ImGuiChildFlags.None;
                            bool visible = ImGuiNative.igBeginChild_Str(str, new Vector2(op.A * s, op.B * s), childFlags, (ImGuiWindowFlags)op.Flags) != 0;
                            if (visible && open < Scopes.Length)
                                Scopes[open++] = new Scope { Kind = ScopeChild, End = op.EndIndex };
                            else
                            {
                                ImGuiNative.igEndChild();   // always paired, drawn or not
                                next = op.EndIndex;
                            }
                            break;
                        }
                        case ScriptOp.TreeNode:
                        {
                            byte live = ImGuiNative.igTreeNodeEx_Str(str, (ImGuiTreeNodeFlags)op.Flags);
                            ReportOpen(w, ref op, live, frame);
                            if (live != 0 && op.Flag != 0 && open < Scopes.Length)
                                Scopes[open++] = new Scope { Kind = ScopeTree, End = op.EndIndex };
                            else
                            {
                                // Closed, or just opened by the player (recorded closed: no children yet).
                                if (live != 0) ImGuiNative.igTreePop();
                                next = op.EndIndex;
                            }
                            break;
                        }
                        case ScriptOp.CollapsingHeader:
                        {
                            byte live = ImGuiNative.igCollapsingHeader_TreeNodeFlags(str, (ImGuiTreeNodeFlags)op.Flags);
                            ReportOpen(w, ref op, live, frame);
                            // Just closed by the player: the recorded contents follow; hide them until the
                            // next header or the scope's end (one tick, until the next list arrives).
                            if (live == 0 && op.Flag != 0) sc.Skip = true;
                            break;
                        }
                    }
                    i = next;
                }
            }
            finally
            {
                // Normal end: nothing is open. Over budget or an exception: close every open scope
                // (colours, IDs, TreePop, EndChild) so ImGui's stacks stay balanced.
                while (open > 0)
                {
                    open--;
                    CloseScope(ref Scopes[open]);
                }
            }
        }
        return visited;
    }

    /// <summary>The missing-icon placeholder's fill (a dim grey square).</summary>
    private static readonly Vector4 PlaceholderFill = new(0.5f, 0.5f, 0.5f, 0.35f);

    /// <summary>0xAABBGGRR as ImGui's Vector4 colour.</summary>
    private static Vector4 Rgba(uint c) =>
        new((c & 0xFF) / 255f, ((c >> 8) & 0xFF) / 255f, ((c >> 16) & 0xFF) / 255f, (c >> 24) / 255f);

    /// <summary>The font atlas's white pixel: a plain-colour "image" that needs no texture of its own.</summary>
    private static void WhitePixel(out IntPtr texture, out Vector2 uv)
    {
        ImFontAtlas* atlas = ImGuiNative.igGetIO()->Fonts;
        texture = atlas->TexID;
        uv = atlas->TexUvWhitePixel;
    }

    /// <summary>An icon that isn't ready, is missing or didn't decode: a dim square of the same size (and border).</summary>
    private static void Placeholder(Vector2 size, Vector4 border)
    {
        WhitePixel(out IntPtr white, out Vector2 uv);
        ImGuiNative.igImage(white, size, uv, uv, PlaceholderFill, border);
    }

    private static void CloseScope(ref Scope sc)
    {
        if (sc.Colors > 0)
        {
            int c = sc.Colors;
            sc.Colors = 0;
            ImGuiNative.igPopStyleColor(c);
        }
        while (sc.Ids > 0)
        {
            sc.Ids--;
            ImGuiNative.igPopID();
        }
        byte kind = sc.Kind;
        sc.Kind = ScopeRoot;
        if (kind == ScopeTree) ImGuiNative.igTreePop();
        else if (kind == ScopeChild) ImGuiNative.igEndChild();
    }

    /// <summary>
    /// A tree node / header whose live state differs from what was last reported for this list
    /// (at first, the recorded state) queues an Open event (coalesced per widget).
    /// </summary>
    private static void ReportOpen(ScriptWindow w, ref ReplayOp op, byte live, int frame)
    {
        byte v = live != 0 ? (byte)1 : (byte)0;
        if (v == op.LastOpen) return;
        op.LastOpen = v;
        w.NoteInput(w.Owner.Events.Open(w.Hash, op.Key, v != 0), frame);
    }

    private static void ReplayInputText(ScriptWindow w, ref ReplayOp op, byte* label, byte* pool, uint ack, int frame)
    {
        int maxLen = op.Aux;
        ScriptLocals.TextLocal t = w.Locals.Text(op.Key, maxLen, frame);
        byte[] buf = t.Buf;
        // No newer local text and not being typed in: show the list's value (copied only when it differs).
        if (!(t.Seq > ack || t.Active))
        {
            t.Seq = 0;
            var value = new ReadOnlySpan<byte>(pool + op.Str2, op.Str2Len);
            int cur = Array.IndexOf(buf, (byte)0);
            if (cur < 0) cur = buf.Length - 1;
            if (!value.SequenceEqual(buf.AsSpan(0, cur)))
            {
                value.CopyTo(buf);
                buf[value.Length] = 0;
            }
        }

        var flags = (ImGuiInputTextFlags)op.Flags;
        byte ret;
        bool edited;
        fixed (byte* b = buf)
        {
            ret = ImGuiNative.igInputText(label, b, (uint)(maxLen + 1), flags, null, null);
            edited = ImGuiNative.igIsItemEdited() != 0;
        }
        t.Active = ImGuiNative.igIsItemActive() != 0;
        if (t.Active) ImGuiTextFocus.NoteActive("text box");   // keys stay out of AC from this frame on

        // EnterReturnsTrue: true = Enter pressed (submitted); edits still send the text, unsubmitted.
        // Otherwise true = edited.
        bool enterMode = (flags & ImGuiInputTextFlags.EnterReturnsTrue) != 0;
        bool submitted = enterMode && ret != 0;
        if (submitted || edited || (!enterMode && ret != 0))
        {
            buf[maxLen] = 0;
            int len = Array.IndexOf(buf, (byte)0);
            uint seq = w.Owner.Events.Text(w.Hash, op.Key, buf.AsSpan(0, len), submitted);
            t.Seq = seq;
            w.NoteInput(seq, frame);
        }
    }
}

/// <summary>
/// A window's local widget values (no snap-back, §2.5). AC thread only. A local value shows
/// while its event seq is newer than the list's AckSeq, or while ImGui reported the item
/// active last frame; otherwise it is dropped and the list's value shows.
/// </summary>
internal sealed class ScriptLocals
{
    private const int PruneAbove = 64;
    private const int StaleFrames = 600;

    public struct Local<T> where T : struct
    {
        public T Value;
        public uint Seq;
        public bool Active;
        public int Frame;
    }

    public sealed class LocalMap<T> where T : struct
    {
        private readonly Dictionary<uint, Local<T>> _map = new();

        public int Count => _map.Count;

        /// <summary>The local value to show instead of the list's, if any (a stale one is dropped).</summary>
        public bool TryGet(uint key, uint ack, out T value)
        {
            value = default;
            if (_map.Count == 0) return false;
            ref Local<T> l = ref CollectionsMarshal.GetValueRefOrNullRef(_map, key);
            if (Unsafe.IsNullRef(ref l)) return false;
            if (l.Seq > ack || l.Active)
            {
                value = l.Value;
                return true;
            }
            _map.Remove(key);
            return false;
        }

        /// <summary>After the widget call: record a change, or the item's active state.</summary>
        public void After(uint key, bool had, bool changed, T value, uint seq, bool active, int frame)
        {
            if (changed)
            {
                _map[key] = new Local<T> { Value = value, Seq = seq, Active = active, Frame = frame };
                return;
            }
            if (had)
            {
                ref Local<T> l = ref CollectionsMarshal.GetValueRefOrNullRef(_map, key);
                if (!Unsafe.IsNullRef(ref l))
                {
                    l.Active = active;
                    l.Frame = frame;
                }
            }
            else if (active)
            {
                // Grabbed without a change yet: hold the shown value while it is held.
                _map[key] = new Local<T> { Value = value, Seq = 0, Active = true, Frame = frame };
            }
        }

        public void Prune(int frame)
        {
            foreach (var kv in _map)
                if (!kv.Value.Active && frame - kv.Value.Frame > StaleFrames)
                    _map.Remove(kv.Key);
        }
    }

    /// <summary>One InputText's pinned buffer (maxLen + 1 bytes) and its local state.</summary>
    public sealed class TextLocal
    {
        public byte[] Buf;
        public int MaxLen;
        public uint Seq;
        public bool Active;
        public int Frame;

        public TextLocal(int maxLen)
        {
            MaxLen = maxLen;
            Buf = GC.AllocateArray<byte>(maxLen + 1, pinned: true);
        }
    }

    public readonly LocalMap<bool> Bools = new();
    public readonly LocalMap<int> Ints = new();
    public readonly LocalMap<float> Floats = new();
    private readonly Dictionary<uint, TextLocal> _texts = new();

    /// <summary>
    /// The InputText buffer for a widget, holding at most <paramref name="maxLen"/> bytes of text
    /// (NUL-terminated within maxLen + 1). Allocated when first seen and only ever grown: two
    /// InputTexts sharing a key (a duplicate ID the recorder only warns about) with different
    /// maxLens would otherwise swap a new pinned buffer in for each of them on every frame.
    /// A smaller maxLen cuts the text in place instead.
    /// </summary>
    public TextLocal Text(uint key, int maxLen, int frame)
    {
        if (!_texts.TryGetValue(key, out TextLocal? t))
        {
            t = new TextLocal(maxLen);
            _texts[key] = t;
        }
        else if (t.MaxLen < maxLen)
        {
            var bigger = new TextLocal(maxLen) { Seq = t.Seq, Active = t.Active };
            int len = Array.IndexOf(t.Buf, (byte)0);
            if (len < 0) len = t.MaxLen;
            t.Buf.AsSpan(0, len).CopyTo(bigger.Buf);
            t = bigger;
            _texts[key] = t;
        }
        if (t.MaxLen > maxLen && t.Buf[maxLen] != 0)
        {
            // ImGui copies strlen(buf) bytes into a buffer of the size it's given: the text must fit.
            int len = Array.IndexOf(t.Buf, (byte)0);
            if (len < 0 || len > maxLen)
            {
                len = maxLen;
                while (len > 0 && (t.Buf[len] & 0xC0) == 0x80) len--;   // don't split a character
                t.Buf[len] = 0;
            }
        }
        t.Frame = frame;
        return t;
    }

    /// <summary>On a new list: drop entries for widgets not drawn for a while, once there are many.</summary>
    public void PruneIfLarge(int frame)
    {
        if (Bools.Count > PruneAbove) Bools.Prune(frame);
        if (Ints.Count > PruneAbove) Ints.Prune(frame);
        if (Floats.Count > PruneAbove) Floats.Prune(frame);
        if (_texts.Count > PruneAbove)
            foreach (var kv in _texts)
                if (!kv.Value.Active && frame - kv.Value.Frame > StaleFrames)
                    _texts.Remove(kv.Key);
    }
}

/// <summary>
/// Phase 0 measurements, AC thread: replay cost per frame (all script windows
/// together) and input -> reaction latency, logged about every 10 s while a
/// script window draws. Lines are written off AC's thread (UiBackgroundWriter).
/// </summary>
internal static class ScriptWindowStats
{
    private const double LogEverySeconds = 10.0;

    // The frame being summed.
    private static int _curFrame = int.MinValue;
    private static long _curTicks;
    private static int _curOps, _curWindows;

    // The current ~10 s period.
    private static long _periodStart;
    private static int _frames, _maxWindows, _maxOps;
    private static long _sumTicks, _maxTicks, _sumOps;
    private static int _reactions, _reactionFramesSum, _reactionFramesMax;
    private static long _reactionTicksSum, _reactionTicksMax;

    public static void Replay(int frame, long ticks, int ops)
    {
        if (frame != _curFrame)
        {
            FlushFrame();
            _curFrame = frame;
        }
        _curTicks += ticks;
        _curOps += ops;
        _curWindows++;

        long now = Stopwatch.GetTimestamp();
        if (_periodStart == 0) _periodStart = now;
        if (now - _periodStart >= (long)(Stopwatch.Frequency * LogEverySeconds))
        {
            LogPeriod(now);
            _periodStart = now;
        }
    }

    public static void Reaction(ScriptWindow w, int frames, long ticks)
    {
        _reactions++;
        _reactionFramesSum += frames;
        _reactionFramesMax = Math.Max(_reactionFramesMax, frames);
        _reactionTicksSum += ticks;
        _reactionTicksMax = Math.Max(_reactionTicksMax, ticks);
        string line = $"ScriptWindows input->reaction: {w.PanelKey} {frames} frame(s), {Ms(ticks):0.0} ms";
        UiBackgroundWriter.Enqueue("script window latency", () => RynthLog.UI(line));
    }

    private static void FlushFrame()
    {
        if (_curWindows == 0) return;
        _frames++;
        _sumTicks += _curTicks;
        _maxTicks = Math.Max(_maxTicks, _curTicks);
        _sumOps += _curOps;
        _maxOps = Math.Max(_maxOps, _curOps);
        _maxWindows = Math.Max(_maxWindows, _curWindows);
        _curTicks = 0;
        _curOps = 0;
        _curWindows = 0;
    }

    private static void LogPeriod(long now)
    {
        FlushFrame();
        if (_frames > 0)
        {
            double seconds = (now - _periodStart) / (double)Stopwatch.Frequency;
            double avgUs = Ms(_sumTicks) * 1000.0 / _frames;
            double maxUs = Ms(_maxTicks) * 1000.0;
            double avgOps = _sumOps / (double)_frames;
            double usPerOp = _sumOps > 0 ? Ms(_sumTicks) * 1000.0 / _sumOps : 0;
            string reaction = _reactions == 0 ? "none"
                : $"n={_reactions} avg={_reactionFramesSum / (double)_reactions:0.0} frames/{Ms(_reactionTicksSum) / _reactions:0.0} ms max={_reactionFramesMax} frames/{Ms(_reactionTicksMax):0.0} ms";
            string line = $"ScriptWindows replay: {seconds:0.0}s frames={_frames} windows<={_maxWindows} ops/frame avg={avgOps:0} max={_maxOps} "
                + $"us/frame avg={avgUs:0.0} max={maxUs:0.0} us/op={usPerOp:0.000} | input->reaction {reaction}";
            UiBackgroundWriter.Enqueue("script window stats", () => RynthLog.UI(line));
        }
        _frames = _maxWindows = _maxOps = 0;
        _sumTicks = _maxTicks = _sumOps = 0;
        _reactions = _reactionFramesSum = _reactionFramesMax = 0;
        _reactionTicksSum = _reactionTicksMax = 0;
    }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}
