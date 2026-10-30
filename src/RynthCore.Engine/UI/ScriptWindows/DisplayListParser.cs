// ============================================================================
//  RynthCore.Engine - UI/ScriptWindows/DisplayListParser.cs
//  Script windows (API v71): validates a UiSubmit buffer and turns each window
//  body into an immutable DisplayList the replay walks every frame.
//  Design: RynthSuite Docs/RYNTHLUA_WINDOWS_DESIGN.md §4.2 (format 1), §6.3.
//
//  Runs on the plugin pump thread inside UiSubmit. It reads the plugin's
//  buffer only during the call and copies everything it keeps: strings go
//  into one pinned, NUL-terminated UTF-8 pool per list, so the replay hands
//  cimgui byte pointers with no per-frame encoding or allocation.
//
//  Format 1 ops (Phase 1 MVP):
//    0x01 Text  0x02 TextColored  0x03 TextWrapped  0x04 TextDisabled
//    0x05 BulletText  0x06 SeparatorText  0x07 LabelText
//    0x10 Separator  0x11 SameLine  0x12 NewLine  0x13 Spacing  0x14 Dummy
//    0x15 Indent  0x16 Unindent
//    0x20 Button  0x21 Checkbox  0x22 SliderInt  0x23 SliderFloat
//    0x24 InputText  0x25 Combo  0x26 Selectable  0x27 ProgressBar
//  Op level 2 (2026-09-30, still format 1: older engines skip these ops):
//    0x28 Image  0x29 ImageButton  0x2A InputInt  0x2B InputFloat
//    0x2C DragInt  0x2D DragFloat
//    0x30 PushID  0x31 PopID  0x32 PushStyleColor  0x33 PopStyleColor
//    0x38 SetItemTooltip
//    0x40 Child (block)  0x41 CollapsingHeader (plain op)  0x42 TreeNode (block)
//    0x00 End (root body only)
//  An op this engine doesn't know is skipped by its PayloadLength (and its
//  block), so newer recorders still draw what this engine can. A known op
//  that carries a block it can't have, or a block op without one, refuses the
//  list. Every flag field is masked to a whitelist, every f32 must be finite,
//  and no script text can reach cimgui as a format string (LabelText's text
//  is stored with '%' doubled; slider, drag and InputFloat formats are
//  validated). Icons are ids the engine resolves (ScriptIcons), never pointers.
// ============================================================================

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using System.Text.Unicode;
using ImGuiNET;

namespace RynthCore.Engine.UI.ScriptWindows;

internal enum ScriptOp : byte
{
    End = 0x00,
    Text = 0x01,
    TextColored = 0x02,
    TextWrapped = 0x03,
    TextDisabled = 0x04,
    BulletText = 0x05,
    SeparatorText = 0x06,
    LabelText = 0x07,
    Separator = 0x10,
    SameLine = 0x11,
    NewLine = 0x12,
    Spacing = 0x13,
    Dummy = 0x14,
    Indent = 0x15,
    Unindent = 0x16,
    Button = 0x20,
    Checkbox = 0x21,
    SliderInt = 0x22,
    SliderFloat = 0x23,
    InputText = 0x24,
    Combo = 0x25,
    Selectable = 0x26,
    ProgressBar = 0x27,
    Image = 0x28,
    ImageButton = 0x29,
    InputInt = 0x2A,
    InputFloat = 0x2B,
    DragInt = 0x2C,
    DragFloat = 0x2D,
    PushID = 0x30,
    PopID = 0x31,
    PushStyleColor = 0x32,
    PopStyleColor = 0x33,
    SetItemTooltip = 0x38,
    Child = 0x40,
    CollapsingHeader = 0x41,
    TreeNode = 0x42,
}

/// <summary>What an Image / ImageButton op's id is (op level 2).</summary>
internal enum ScriptIconKind : byte
{
    /// <summary>An icon (0x06 texture) id; below 0x06000000, 0x06000000 is added (UtilityBelt's rule).</summary>
    Icon = 0,
    /// <summary>An object id: the item's icon with its underlay and overlay.</summary>
    Object = 1,
    /// <summary>A spell id: the spell's icon.</summary>
    Spell = 2,
    /// <summary>A kind this engine doesn't know: drawn as the missing-icon placeholder.</summary>
    Unknown = 0xFF,
}

/// <summary>
/// One validated op, ready to replay. Strings are offsets into the list's pool (-1 = none).
/// Field use per op (see <see cref="DisplayListParser"/>):
///   Str/StrLen   the text, label, PushID string, tooltip or child id.
///   Str2/Str2Len LabelText's escaped text, a slider's format, InputText's value,
///                Combo's NUL-separated items, ProgressBar's overlay.
///   Flag         Button small; Checkbox value; Selectable selected; Child border;
///                CollapsingHeader/TreeNode recordedOpen; PushID kind; Image/ImageButton
///                icon kind (ScriptIconKind).
///   Aux          PushStyleColor ImGuiCol; PopStyleColor count; InputText maxLen; Combo count.
///   Flags        the op's masked ImGui flags.
///   Color        TextColored / PushStyleColor colour (0xAABBGGRR); Image tint;
///                ImageButton background.
///   Color2       Image border; ImageButton tint.
///   I0..I2       SliderInt v/min/max; Combo index; PushID integer; InputInt v/step/stepFast;
///                DragInt v/min/max; Image/ImageButton id (I0, as u32).
///   A..F         sizes and offsets (logical, UI scale 1); SliderFloat v/min/max;
///                ProgressBar fraction (A) and size (B, C); Image size (A, B), uv0 (C, D)
///                and uv1 (E, F); InputFloat v/step/stepFast; DragFloat v/min/max (A..C);
///                DragInt/DragFloat speed (D).
///   EndIndex     the index just past the op (a block op: past its last child).
/// </summary>
internal struct ReplayOp
{
    public ScriptOp Code;
    public byte Flag;
    /// <summary>AC thread only: the open state last reported for this list (CollapsingHeader, TreeNode).</summary>
    public byte LastOpen;
    public ushort Aux;
    public int Str;
    public int StrLen;
    public int Str2;
    public int Str2Len;
    public uint Key;
    public uint Flags;
    public uint Color;
    public uint Color2;
    public int I0, I1, I2;
    public float A, B, C, D, E, F;
    public int EndIndex;
}

/// <summary>An immutable parsed window body. Published to AC's thread with Volatile.Write.</summary>
internal sealed class DisplayList
{
    public static readonly DisplayList Empty = new(Array.Empty<ReplayOp>(), 0, GC.AllocateArray<byte>(1, pinned: true), 0, 0);

    public readonly ReplayOp[] Ops;
    public readonly int OpCount;
    /// <summary>Pinned (pinned-object heap), NUL-terminated UTF-8 strings.</summary>
    public readonly byte[] Pool;
    public readonly uint ListSeq;
    /// <summary>The last event seq the plugin had applied when it recorded this list.</summary>
    public readonly uint AckSeq;

    public DisplayList(ReplayOp[] ops, int opCount, byte[] pool, uint listSeq, uint ackSeq)
    {
        Ops = ops;
        OpCount = opCount;
        Pool = pool;
        ListSeq = listSeq;
        AckSeq = ackSeq;
    }
}

/// <summary>One window of a submit, validated.</summary>
internal sealed class ParsedWindow
{
    public required string Key;
    public required uint Hash;
    public required string Title;
    public required uint Flags;
    /// <summary>Panel logical units. RequestSize/RequestPos: NaN = none.</summary>
    public Vector2 DefaultSize, MinSize, RequestSize, RequestPos;
    /// <summary>Flags bit10: RequestPos is a FirstUseEver default position, not a request.</summary>
    public bool PosIsDefault;
    /// <summary>The script's error (Flags bit9 HasError), control characters made spaces; null = none.</summary>
    public string? ErrorText;
    public uint ListSeq;
    /// <summary>Null: unchanged since the last submit (no body sent).</summary>
    public DisplayList? List;
}

internal static class WindowFlags
{
    public const uint Visible = 1u << 0;
    public const uint ShowInBar = 1u << 1;
    public const uint ChromeNone = 1u << 2;
    public const uint ClickThrough = 1u << 3;
    public const uint NoBackground = 1u << 4;
    public const uint NoScrollbar = 1u << 5;
    public const uint NoScrollWithMouse = 1u << 6;
    /// <summary>Phase 2: parsed, ignored.</summary>
    public const uint AutoSize = 1u << 7;
    /// <summary>Phase 2: parsed, ignored.</summary>
    public const uint NoPopOut = 1u << 8;
    /// <summary>An ErrorText field follows RequestPos; the window draws it above the list.</summary>
    public const uint HasError = 1u << 9;
    /// <summary>RequestPos is a FirstUseEver default position (used only when nothing is saved).</summary>
    public const uint PosIsDefault = 1u << 10;
    /// <summary>
    /// With ShowInBar: the window gets a button of its own on the bar (icon, tooltip = title)
    /// instead of a line in the Scripts menu. For plugin windows; engines before 2026-10-11
    /// mask it off and list the window in the Scripts menu as before.
    /// </summary>
    public const uint BarButton = 1u << 11;
    public const uint Known = (1u << 12) - 1;
}

internal static class DisplayListParser
{
    public const uint Magic = 0x31575352;          // 'RSW1'
    public const ushort FormatVersion = 1;
    /// <summary>
    /// The ops this engine replays within format 1 (UiGetInfo OpLevel): 1 = Phase 1; 2 = + Image,
    /// ImageButton, InputInt, InputFloat, DragInt, DragFloat. Older engines report 0 (the field
    /// was reserved) and skip level-2 ops; RynthLua refuses to record them there.
    /// </summary>
    public const ushort OpLevel = 2;
    public const int HeaderSize = 16;
    public const int MaxString = 4096;
    public const int MaxStr8 = 128;
    public const int MaxErrorText = 1024;
    /// <summary>Blocks plus the PushID depth (§6.1).</summary>
    public const int MaxDepth = 32;
    public const int MaxComboItems = 512;
    private const uint BodyUnchanged = 0xFFFFFFFF;
    private const byte OpHasBlock = 1;

    // ── Whitelists (ImGui.NET 1.91.6.1) ─────────────────────────────────

    private const uint InputTextMask = (uint)(ImGuiInputTextFlags.CharsDecimal | ImGuiInputTextFlags.CharsHexadecimal
        | ImGuiInputTextFlags.CharsUppercase | ImGuiInputTextFlags.CharsNoBlank | ImGuiInputTextFlags.EnterReturnsTrue
        | ImGuiInputTextFlags.ReadOnly | ImGuiInputTextFlags.Password | ImGuiInputTextFlags.AutoSelectAll);

    private const uint SelectableMask = (uint)(ImGuiSelectableFlags.AllowDoubleClick | ImGuiSelectableFlags.Disabled
        | ImGuiSelectableFlags.AllowOverlap);

    /// <summary>TreeNode / CollapsingHeader. NoTreePushOnOpen is never allowed (the engine owns TreePop).</summary>
    private const uint TreeNodeMask = (uint)(ImGuiTreeNodeFlags.Selected | ImGuiTreeNodeFlags.Framed
        | ImGuiTreeNodeFlags.AllowOverlap | ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.OpenOnDoubleClick
        | ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.Bullet
        | ImGuiTreeNodeFlags.FramePadding | ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.SpanFullWidth
        | ImGuiTreeNodeFlags.SpanTextWidth) & ~(uint)ImGuiTreeNodeFlags.NoTreePushOnOpen;

    /// <summary>InputInt / InputFloat (ImGui's InputScalar picks its own character filter).</summary>
    private const uint NumberInputMask = (uint)(ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.ReadOnly
        | ImGuiInputTextFlags.AutoSelectAll | ImGuiInputTextFlags.ParseEmptyRefVal | ImGuiInputTextFlags.DisplayEmptyRefVal);

    /// <summary>DragInt / DragFloat (ImGuiSliderFlags; AlwaysClamp = ClampOnInput | ClampZeroRange).</summary>
    private const uint DragMask = (uint)(ImGuiSliderFlags.Logarithmic | ImGuiSliderFlags.NoRoundToFormat
        | ImGuiSliderFlags.NoInput | ImGuiSliderFlags.WrapAround | ImGuiSliderFlags.AlwaysClamp);

    /// <summary>Image uv coordinates are clamped to this (the texture repeats or clamps beyond 0..1).</summary>
    private const float UvLimit = 16f;
    /// <summary>Drag speed per pixel of mouse movement, clamped to this either way.</summary>
    private const float DragSpeedLimit = 1_000_000f;

    private const uint ChildWindowMask = (uint)(ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
        | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.HorizontalScrollbar
        | ImGuiWindowFlags.AlwaysVerticalScrollbar | ImGuiWindowFlags.AlwaysHorizontalScrollbar);

    /// <summary>The style colours a script may push; any other id drops the push (and its pop).</summary>
    public static bool IsAllowedColor(int col) => (ImGuiCol)col switch
    {
        ImGuiCol.Text or ImGuiCol.TextDisabled or ImGuiCol.ChildBg or ImGuiCol.Border or ImGuiCol.FrameBg
            or ImGuiCol.CheckMark or ImGuiCol.SliderGrab or ImGuiCol.Button or ImGuiCol.ButtonHovered
            or ImGuiCol.ButtonActive or ImGuiCol.Header or ImGuiCol.HeaderHovered or ImGuiCol.PlotHistogram => true,
        _ => false,
    };

    // Slider range limits ImGui asserts on (SliderBehavior).
    private const int SliderIntLimit = int.MaxValue / 2;
    private const float SliderFloatLimit = float.MaxValue / 2;

    /// <summary>FNV-1a, the hash both sides use for window keys (and the recorder for widget keys).</summary>
    public static uint Fnv1a(ReadOnlySpan<byte> bytes, uint hash = 2166136261)
    {
        foreach (byte b in bytes)
        {
            hash ^= b;
            hash *= 16777619;
        }
        return hash;
    }

    /// <summary>
    /// Validates a whole submit. Returns 0 with <paramref name="windows"/> set, or
    /// -1 malformed / -3 over a limit / -4 unsupported version, with <paramref name="error"/>.
    /// Nothing is applied on failure.
    /// </summary>
    public static unsafe int Parse(byte* data, int length, float displayMax,
        out uint ackSeq, out List<ParsedWindow> windows, out string error)
    {
        ackSeq = 0;
        windows = new List<ParsedWindow>();
        error = string.Empty;
        if (data == null || length < HeaderSize) { error = "submit shorter than its header"; return -1; }
        if (length > ScriptWindowRegistry.MaxBytesPerSubmit)
        {
            error = $"submit of {length} bytes is over the {ScriptWindowRegistry.MaxBytesPerSubmit}-byte limit";
            return -3;
        }

        var span = new ReadOnlySpan<byte>(data, length);
        if (BinaryPrimitives.ReadUInt32LittleEndian(span) != Magic) { error = "bad magic"; return -1; }
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(span[4..]);
        if (version == 0 || version > FormatVersion) { error = $"format {version} isn't supported (max {FormatVersion})"; return -4; }
        int windowCount = BinaryPrimitives.ReadUInt16LittleEndian(span[6..]);
        ackSeq = BinaryPrimitives.ReadUInt32LittleEndian(span[8..]);
        uint total = BinaryPrimitives.ReadUInt32LittleEndian(span[12..]);
        if (total != (uint)length) { error = "TotalLength doesn't match the length"; return -1; }
        if (windowCount > ScriptWindowRegistry.MaxWindowsPerOwner)
        {
            error = $"{windowCount} windows is over the {ScriptWindowRegistry.MaxWindowsPerOwner}-window limit";
            return -3;
        }

        var r = new Reader(span, HeaderSize);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var ctx = new BodyContext(displayMax);
        for (int w = 0; w < windowCount; w++)
        {
            if (!r.Str8(out string key, out ReadOnlySpan<byte> keyBytes) || key.Length == 0)
            { error = $"window {w}: bad key"; return -1; }
            if (!keys.Add(key)) { error = $"window '{key}' appears twice"; return -1; }
            if (!r.Str8(out string title, out _)) { error = $"window '{key}': bad title"; return -1; }
            if (!r.U32(out uint flags)) { error = $"window '{key}': truncated"; return -1; }
            flags &= WindowFlags.Known;
            var pw = new ParsedWindow
            {
                Key = key,
                Hash = Fnv1a(keyBytes),
                Title = title.Length > 0 ? title : key,
                Flags = flags,
                PosIsDefault = (flags & WindowFlags.PosIsDefault) != 0,
            };
            if (!r.Vec2(out pw.DefaultSize, allowNaN: false) || !r.Vec2(out pw.MinSize, allowNaN: false)
                || !r.Vec2(out pw.RequestSize, allowNaN: true) || !r.Vec2(out pw.RequestPos, allowNaN: true))
            { error = $"window '{key}': bad sizes (not finite)"; return -1; }
            pw.DefaultSize = ClampSize(pw.DefaultSize, displayMax);
            pw.MinSize = ClampSize(pw.MinSize, displayMax);
            if (!float.IsNaN(pw.RequestSize.X)) pw.RequestSize = ClampSize(pw.RequestSize, displayMax);
            if (!float.IsNaN(pw.RequestPos.X))
                pw.RequestPos = new Vector2(Math.Clamp(pw.RequestPos.X, -displayMax, displayMax), Math.Clamp(pw.RequestPos.Y, -displayMax, displayMax));
            if ((flags & WindowFlags.HasError) != 0)
            {
                if (!r.U16(out ushort errLen) || errLen > MaxErrorText || !r.Take(errLen, out ReadOnlySpan<byte> errBytes))
                { error = $"window '{key}': bad error text"; return -1; }
                pw.ErrorText = Sanitize(Encoding.UTF8.GetString(errBytes));
            }
            if (!r.U32(out pw.ListSeq) || !r.U32(out uint bodyLength)) { error = $"window '{key}': truncated"; return -1; }
            if (bodyLength != BodyUnchanged)
            {
                if (bodyLength > ScriptWindowRegistry.MaxBytesPerWindow)
                { error = $"window '{key}' is {bodyLength} bytes, over the {ScriptWindowRegistry.MaxBytesPerWindow}-byte limit"; return -3; }
                if (!r.Take((int)bodyLength, out ReadOnlySpan<byte> body)) { error = $"window '{key}': body runs past the end"; return -1; }
                ctx.Reset(key);
                int rc = ParseRoot(body, ctx);
                if (rc != 0) { error = ctx.Error; return rc; }
                pw.List = new DisplayList(ctx.Ops.ToArray(), ctx.Ops.Count, ctx.Pool.ToPinned(), pw.ListSeq, ackSeq);
            }
            windows.Add(pw);
        }
        if (r.Pos != span.Length) { error = "bytes after the last window"; return -1; }
        return 0;
    }

    private static Vector2 ClampSize(Vector2 v, float max) =>
        new(Math.Clamp(v.X, 0, max), Math.Clamp(v.Y, 0, max));

    /// <summary>Per-body parse state, reused across the windows of one submit.</summary>
    private sealed class BodyContext
    {
        public readonly float DisplayMax;
        public readonly List<ReplayOp> Ops = new();
        public readonly PoolBuilder Pool = new();
        /// <summary>PushStyleColor stack across scopes: true = a real push, false = a dropped one.</summary>
        public readonly List<bool> Colors = new();
        public string Key = string.Empty;
        public string Error = string.Empty;
        /// <summary>Every op read (known, unknown or dropped), children included.</summary>
        public int OpsRead;
        public int DefaultIntFmt = -1, DefaultIntFmtLen, DefaultFloatFmt = -1, DefaultFloatFmtLen;

        public BodyContext(float displayMax) => DisplayMax = displayMax;

        public void Reset(string key)
        {
            Ops.Clear();
            Pool.Clear();
            Colors.Clear();
            Key = key;
            Error = string.Empty;
            OpsRead = 0;
            DefaultIntFmt = DefaultFloatFmt = -1;
        }

        public int Fail(int rc, string message)
        {
            Error = $"window '{Key}': {message}";
            return rc;
        }
    }

    private static int ParseRoot(ReadOnlySpan<byte> body, BodyContext ctx)
    {
        if (body.Length == 0) return 0;   // empty list
        var r = new Reader(body, 0);
        int rc = ParseScope(ref r, ctx, depth: 0, root: true);
        if (rc != 0) return rc;
        if (r.Pos != body.Length) return ctx.Fail(-1, "bytes after End");
        return 0;
    }

    /// <summary>
    /// Parses one scope: the root body (ends with End) or a block's children (end with the block).
    /// <paramref name="depth"/> is the enclosing blocks plus PushIDs still open outside this scope.
    /// </summary>
    private static int ParseScope(ref Reader r, BodyContext ctx, int depth, bool root)
    {
        int ids = 0;                        // PushIDs open in this scope
        int colorBase = ctx.Colors.Count;   // this scope's part of the colour stack starts here
        try
        {
            while (true)
            {
                if (!root && r.AtEnd) return 0;
                if (!r.U8(out byte code) || !r.U8(out byte opFlags) || !r.U16(out ushort payloadLength))
                    return ctx.Fail(-1, "op header runs past the end");
                if (code == (byte)ScriptOp.End)
                {
                    if (!root) return ctx.Fail(-1, "End inside a block");
                    return 0;
                }
                if (++ctx.OpsRead > ScriptWindowRegistry.MaxOpsPerWindow)
                    return ctx.Fail(-3, $"more than {ScriptWindowRegistry.MaxOpsPerWindow} items");
                if (!r.Take(payloadLength, out ReadOnlySpan<byte> payload))
                    return ctx.Fail(-1, $"op 0x{code:X2} runs past the end");
                bool hasBlock = (opFlags & OpHasBlock) != 0;
                ReadOnlySpan<byte> block = default;
                if (hasBlock)
                {
                    if (!r.U32(out uint blockLength) || blockLength > int.MaxValue || !r.Take((int)blockLength, out block))
                        return ctx.Fail(-1, $"op 0x{code:X2} block runs past the end");
                }

                var op = new ReplayOp { Code = (ScriptOp)code, Str = -1, Str2 = -1 };
                var p = new Reader(payload, 0);
                bool ok;
                bool keep = true;
                bool isBlockOp = false;
                float max = ctx.DisplayMax;
                switch ((ScriptOp)code)
                {
                    case ScriptOp.Text:
                    case ScriptOp.TextWrapped:
                    case ScriptOp.TextDisabled:
                    case ScriptOp.BulletText:
                    case ScriptOp.SeparatorText:
                    case ScriptOp.SetItemTooltip:
                        ok = p.Str(ctx.Pool, out op.Str, out op.StrLen);
                        break;
                    case ScriptOp.TextColored:
                        ok = p.U32(out op.Color) && p.Str(ctx.Pool, out op.Str, out op.StrLen);
                        break;
                    case ScriptOp.LabelText:
                        ok = p.Str(ctx.Pool, out op.Str, out op.StrLen) && p.StrEscaped(ctx.Pool, out op.Str2, out op.Str2Len);
                        break;
                    case ScriptOp.Separator:
                    case ScriptOp.NewLine:
                    case ScriptOp.Spacing:
                        ok = true;
                        break;
                    case ScriptOp.SameLine:
                        ok = p.F32(out op.A) && p.F32(out op.B);
                        op.A = Math.Clamp(op.A, 0f, max);
                        op.B = op.B < 0f ? -1f : Math.Min(op.B, max);
                        break;
                    case ScriptOp.Dummy:
                        ok = p.F32(out op.A) && p.F32(out op.B);
                        op.A = Math.Clamp(op.A, 0f, max);
                        op.B = Math.Clamp(op.B, 0f, max);
                        break;
                    case ScriptOp.Indent:
                    case ScriptOp.Unindent:
                        ok = p.F32(out op.A);
                        op.A = Math.Clamp(op.A, 0f, max);
                        break;
                    case ScriptOp.Button:
                        ok = p.U32(out op.Key) && p.Str(ctx.Pool, out op.Str, out op.StrLen)
                             && p.F32(out op.A) && p.F32(out op.B) && p.U8(out op.Flag);
                        op.A = Math.Clamp(op.A, -max, max);
                        op.B = Math.Clamp(op.B, -max, max);
                        op.Flag = op.Flag != 0 ? (byte)1 : (byte)0;
                        break;
                    case ScriptOp.Checkbox:
                        ok = p.U32(out op.Key) && p.Str(ctx.Pool, out op.Str, out op.StrLen) && p.U8(out op.Flag);
                        op.Flag = op.Flag != 0 ? (byte)1 : (byte)0;
                        break;
                    case ScriptOp.SliderInt:
                        ok = p.U32(out op.Key) && p.Str(ctx.Pool, out op.Str, out op.StrLen)
                             && p.I32(out op.I0) && p.I32(out op.I1) && p.I32(out op.I2)
                             && p.SliderFormat(ctx, isInt: true, out op.Str2, out op.Str2Len);
                        if (ok)
                        {
                            op.I1 = Math.Clamp(op.I1, -SliderIntLimit, SliderIntLimit);
                            op.I2 = Math.Clamp(op.I2, -SliderIntLimit, SliderIntLimit);
                            if (op.I1 > op.I2) (op.I1, op.I2) = (op.I2, op.I1);
                            op.I0 = Math.Clamp(op.I0, op.I1, op.I2);
                        }
                        break;
                    case ScriptOp.SliderFloat:
                        ok = p.U32(out op.Key) && p.Str(ctx.Pool, out op.Str, out op.StrLen)
                             && p.F32(out op.A) && p.F32(out op.B) && p.F32(out op.C)
                             && p.SliderFormat(ctx, isInt: false, out op.Str2, out op.Str2Len);
                        if (ok)
                        {
                            op.B = Math.Clamp(op.B, -SliderFloatLimit, SliderFloatLimit);
                            op.C = Math.Clamp(op.C, -SliderFloatLimit, SliderFloatLimit);
                            if (op.B > op.C) (op.B, op.C) = (op.C, op.B);
                            op.A = Math.Clamp(op.A, op.B, op.C);
                        }
                        break;
                    case ScriptOp.InputText:
                    {
                        ReadOnlySpan<byte> value = default;
                        ok = p.U32(out op.Key) && p.Str(ctx.Pool, out op.Str, out op.StrLen)
                             && p.StrRaw(out value) && p.U16(out op.Aux) && p.U32(out op.Flags);
                        if (ok)
                        {
                            op.Aux = (ushort)Math.Clamp((int)op.Aux, 1, MaxString);
                            op.Flags &= InputTextMask;   // no Callback* flag ever: no callback is passed
                            (op.Str2, op.Str2Len) = ctx.Pool.Add(value, op.Aux);
                        }
                        break;
                    }
                    case ScriptOp.Combo:
                    {
                        ok = p.U32(out op.Key) && p.Str(ctx.Pool, out op.Str, out op.StrLen)
                             && p.I32(out op.I0) && p.U16(out op.Aux) && op.Aux <= MaxComboItems;
                        if (ok)
                        {
                            op.Str2 = ctx.Pool.BeginItems();
                            for (int k = 0; k < op.Aux && ok; k++)
                            {
                                ok = p.StrRaw(out ReadOnlySpan<byte> item);
                                if (ok) ctx.Pool.AddItem(item);
                            }
                            op.Str2Len = ctx.Pool.EndItems(op.Str2);
                            op.I0 = Math.Clamp(op.I0, -1, op.Aux - 1);
                        }
                        break;
                    }
                    case ScriptOp.Selectable:
                        ok = p.U32(out op.Key) && p.Str(ctx.Pool, out op.Str, out op.StrLen) && p.U8(out op.Flag)
                             && p.U32(out op.Flags) && p.F32(out op.A) && p.F32(out op.B);
                        op.Flag = op.Flag != 0 ? (byte)1 : (byte)0;
                        op.Flags &= SelectableMask;
                        op.A = Math.Clamp(op.A, -max, max);
                        op.B = Math.Clamp(op.B, -max, max);
                        break;
                    case ScriptOp.ProgressBar:
                    {
                        ReadOnlySpan<byte> overlay = default;
                        ok = p.F32(out op.A) && p.F32(out op.B) && p.F32(out op.C) && p.StrRaw(out overlay);
                        if (ok)
                        {
                            op.A = Math.Clamp(op.A, 0f, 1f);
                            op.B = Math.Clamp(op.B, -max, max);
                            op.C = Math.Clamp(op.C, -max, max);
                            if (overlay.Length > 0) (op.Str2, op.Str2Len) = ctx.Pool.Add(overlay);   // empty: ImGui's percentage
                        }
                        break;
                    }
                    case ScriptOp.Image:
                        ok = IconRef(ref p, ref op) && p.F32(out op.A) && p.F32(out op.B)
                             && p.F32(out op.C) && p.F32(out op.D) && p.F32(out op.E) && p.F32(out op.F)
                             && p.U32(out op.Color) && p.U32(out op.Color2);
                        if (ok) ClampImage(ref op, max);
                        break;
                    case ScriptOp.ImageButton:
                        ok = p.U32(out op.Key) && p.Str(ctx.Pool, out op.Str, out op.StrLen) && IconRef(ref p, ref op)
                             && p.F32(out op.A) && p.F32(out op.B)
                             && p.F32(out op.C) && p.F32(out op.D) && p.F32(out op.E) && p.F32(out op.F)
                             && p.U32(out op.Color) && p.U32(out op.Color2);
                        if (ok) ClampImage(ref op, max);
                        break;
                    case ScriptOp.InputInt:
                        ok = p.U32(out op.Key) && p.Str(ctx.Pool, out op.Str, out op.StrLen)
                             && p.I32(out op.I0) && p.I32(out op.I1) && p.I32(out op.I2) && p.U32(out op.Flags);
                        op.Flags &= NumberInputMask;
                        break;
                    case ScriptOp.InputFloat:
                        ok = p.U32(out op.Key) && p.Str(ctx.Pool, out op.Str, out op.StrLen)
                             && p.F32(out op.A) && p.F32(out op.B) && p.F32(out op.C)
                             && p.SliderFormat(ctx, isInt: false, out op.Str2, out op.Str2Len) && p.U32(out op.Flags);
                        if (ok)
                        {
                            op.B = Math.Clamp(op.B, -SliderFloatLimit, SliderFloatLimit);
                            op.C = Math.Clamp(op.C, -SliderFloatLimit, SliderFloatLimit);
                        }
                        op.Flags &= NumberInputMask;
                        break;
                    case ScriptOp.DragInt:
                        ok = p.U32(out op.Key) && p.Str(ctx.Pool, out op.Str, out op.StrLen)
                             && p.I32(out op.I0) && p.F32(out op.D) && p.I32(out op.I1) && p.I32(out op.I2)
                             && p.SliderFormat(ctx, isInt: true, out op.Str2, out op.Str2Len) && p.U32(out op.Flags);
                        if (ok)
                        {
                            // min == max (0, 0 by default) is ImGui's "no bounds".
                            op.D = Math.Clamp(op.D, -DragSpeedLimit, DragSpeedLimit);
                            op.I1 = Math.Clamp(op.I1, -SliderIntLimit, SliderIntLimit);
                            op.I2 = Math.Clamp(op.I2, -SliderIntLimit, SliderIntLimit);
                            if (op.I1 > op.I2) (op.I1, op.I2) = (op.I2, op.I1);
                        }
                        op.Flags &= DragMask;
                        break;
                    case ScriptOp.DragFloat:
                        ok = p.U32(out op.Key) && p.Str(ctx.Pool, out op.Str, out op.StrLen)
                             && p.F32(out op.A) && p.F32(out op.D) && p.F32(out op.B) && p.F32(out op.C)
                             && p.SliderFormat(ctx, isInt: false, out op.Str2, out op.Str2Len) && p.U32(out op.Flags);
                        if (ok)
                        {
                            op.D = Math.Clamp(op.D, -DragSpeedLimit, DragSpeedLimit);
                            op.B = Math.Clamp(op.B, -SliderFloatLimit, SliderFloatLimit);
                            op.C = Math.Clamp(op.C, -SliderFloatLimit, SliderFloatLimit);
                            if (op.B > op.C) (op.B, op.C) = (op.C, op.B);
                        }
                        op.Flags &= DragMask;
                        break;
                    case ScriptOp.PushID:
                        ok = p.U8(out op.Flag);
                        if (ok)
                        {
                            if (op.Flag == 0) ok = p.Str(ctx.Pool, out op.Str, out op.StrLen);
                            else if (op.Flag == 1) ok = p.I32(out op.I0);
                            else ok = false;
                        }
                        if (ok)
                        {
                            if (depth + ids + 1 > MaxDepth) return ctx.Fail(-3, $"nesting deeper than {MaxDepth}");
                            ids++;
                        }
                        break;
                    case ScriptOp.PopID:
                        ok = true;
                        if (ids > 0) ids--;
                        else keep = false;   // unmatched in its scope: dropped
                        break;
                    case ScriptOp.PushStyleColor:
                        ok = p.U16(out op.Aux) && p.U32(out op.Color);
                        if (ok)
                        {
                            bool allowed = IsAllowedColor(op.Aux);
                            ctx.Colors.Add(allowed);
                            keep = allowed;   // not whitelisted: dropped, and its pop won't pop
                        }
                        break;
                    case ScriptOp.PopStyleColor:
                    {
                        ok = p.U8(out byte count);
                        if (ok)
                        {
                            // Pop up to count entries of this scope's stack; only real pushes pop.
                            int real = 0;
                            for (int k = 0; k < count && ctx.Colors.Count > colorBase; k++)
                            {
                                if (ctx.Colors[^1]) real++;
                                ctx.Colors.RemoveAt(ctx.Colors.Count - 1);
                            }
                            op.Aux = (ushort)real;
                            keep = real > 0;
                        }
                        break;
                    }
                    case ScriptOp.Child:
                        isBlockOp = true;
                        ok = p.U32(out op.Key) && p.Str(ctx.Pool, out op.Str, out op.StrLen)
                             && p.F32(out op.A) && p.F32(out op.B) && p.U8(out op.Flag) && p.U32(out op.Flags);
                        op.A = Math.Clamp(op.A, -max, max);
                        op.B = Math.Clamp(op.B, -max, max);
                        op.Flag = op.Flag != 0 ? (byte)1 : (byte)0;
                        op.Flags &= ChildWindowMask;
                        break;
                    case ScriptOp.CollapsingHeader:
                    case ScriptOp.TreeNode:
                        isBlockOp = (ScriptOp)code == ScriptOp.TreeNode;
                        ok = p.U32(out op.Key) && p.Str(ctx.Pool, out op.Str, out op.StrLen)
                             && p.U32(out op.Flags) && p.U8(out op.Flag);
                        op.Flags &= TreeNodeMask;
                        op.Flag = op.Flag != 0 ? (byte)1 : (byte)0;
                        op.LastOpen = op.Flag;
                        break;
                    default:
                        continue;   // unknown op (a newer recorder): skipped with its block
                }
                if (!ok) return ctx.Fail(-1, $"op 0x{code:X2} payload is short or has a bad value");
                if (isBlockOp != hasBlock)
                    return ctx.Fail(-1, isBlockOp ? $"op 0x{code:X2} needs a block" : $"op 0x{code:X2} can't have a block");
                if (!keep) continue;

                int index = ctx.Ops.Count;
                op.EndIndex = index + 1;
                ctx.Ops.Add(op);
                if (isBlockOp)
                {
                    if (depth + ids + 1 > MaxDepth) return ctx.Fail(-3, $"nesting deeper than {MaxDepth}");
                    var br = new Reader(block, 0);
                    int rc = ParseScope(ref br, ctx, depth + ids + 1, root: false);
                    if (rc != 0) return rc;
                    ReplayOp done = ctx.Ops[index];
                    done.EndIndex = ctx.Ops.Count;
                    ctx.Ops[index] = done;
                }
            }
        }
        finally
        {
            // Pushes left in this scope are the replay's to pop; the enclosing scope never sees them.
            if (ctx.Colors.Count > colorBase) ctx.Colors.RemoveRange(colorBase, ctx.Colors.Count - colorBase);
        }
    }

    /// <summary>An icon reference: u8 kind, u32 id. A kind this engine doesn't know becomes Unknown (a placeholder).</summary>
    private static bool IconRef(ref Reader p, ref ReplayOp op)
    {
        if (!p.U8(out byte kind) || !p.U32(out uint id)) return false;
        op.Flag = kind <= (byte)ScriptIconKind.Spell ? kind : (byte)ScriptIconKind.Unknown;
        op.I0 = unchecked((int)id);
        return true;
    }

    /// <summary>Image sizes 0..max (no auto or fill for images); uvs within UvLimit.</summary>
    private static void ClampImage(ref ReplayOp op, float max)
    {
        op.A = Math.Clamp(op.A, 0f, max);
        op.B = Math.Clamp(op.B, 0f, max);
        op.C = Math.Clamp(op.C, -UvLimit, UvLimit);
        op.D = Math.Clamp(op.D, -UvLimit, UvLimit);
        op.E = Math.Clamp(op.E, -UvLimit, UvLimit);
        op.F = Math.Clamp(op.F, -UvLimit, UvLimit);
    }

    /// <summary>
    /// A slider format: exactly one conversion %[-+ #0]*[0-9]{0,2}(\.[0-9]{1,2})? ending in d/i
    /// (int) or f/F/g/G/e/E (float); "%%" anywhere; nothing else.
    /// </summary>
    internal static bool IsValidSliderFormat(ReadOnlySpan<byte> f, bool isInt)
    {
        int conversions = 0;
        int i = 0;
        while (i < f.Length)
        {
            if (f[i] != (byte)'%') { i++; continue; }
            i++;
            if (i < f.Length && f[i] == (byte)'%') { i++; continue; }
            if (conversions == 1) return false;
            while (i < f.Length && f[i] is (byte)'-' or (byte)'+' or (byte)' ' or (byte)'#' or (byte)'0') i++;
            int digits = 0;
            while (i < f.Length && f[i] >= (byte)'0' && f[i] <= (byte)'9') { i++; digits++; }
            if (digits > 2) return false;
            if (i < f.Length && f[i] == (byte)'.')
            {
                i++;
                digits = 0;
                while (i < f.Length && f[i] >= (byte)'0' && f[i] <= (byte)'9') { i++; digits++; }
                if (digits is 0 or > 2) return false;
            }
            if (i >= f.Length) return false;
            byte c = f[i++];
            bool okConv = isInt ? c is (byte)'d' or (byte)'i'
                                : c is (byte)'f' or (byte)'F' or (byte)'g' or (byte)'G' or (byte)'e' or (byte)'E';
            if (!okConv) return false;
            conversions++;
        }
        return conversions == 1;
    }

    /// <summary>Little-endian reader with bounds checks; every read returns false instead of throwing.</summary>
    private ref struct Reader
    {
        private readonly ReadOnlySpan<byte> _s;
        public int Pos;

        public Reader(ReadOnlySpan<byte> s, int pos) { _s = s; Pos = pos; }

        public readonly bool AtEnd => Pos >= _s.Length;

        public bool Take(int n, out ReadOnlySpan<byte> bytes)
        {
            if (n < 0 || n > _s.Length - Pos) { bytes = default; return false; }
            bytes = _s.Slice(Pos, n);
            Pos += n;
            return true;
        }

        public bool U8(out byte v)
        {
            if (Pos >= _s.Length) { v = 0; return false; }
            v = _s[Pos++];
            return true;
        }

        public bool U16(out ushort v)
        {
            if (!Take(2, out var b)) { v = 0; return false; }
            v = BinaryPrimitives.ReadUInt16LittleEndian(b);
            return true;
        }

        public bool U32(out uint v)
        {
            if (!Take(4, out var b)) { v = 0; return false; }
            v = BinaryPrimitives.ReadUInt32LittleEndian(b);
            return true;
        }

        public bool I32(out int v)
        {
            if (!Take(4, out var b)) { v = 0; return false; }
            v = BinaryPrimitives.ReadInt32LittleEndian(b);
            return true;
        }

        /// <summary>A finite f32 (anything else is refused).</summary>
        public bool F32(out float v)
        {
            if (!Take(4, out var b)) { v = 0; return false; }
            v = BinaryPrimitives.ReadSingleLittleEndian(b);
            return float.IsFinite(v);
        }

        public bool Vec2(out Vector2 v, bool allowNaN)
        {
            v = default;
            if (!Take(8, out var b)) return false;
            float x = BinaryPrimitives.ReadSingleLittleEndian(b);
            float y = BinaryPrimitives.ReadSingleLittleEndian(b[4..]);
            v = new Vector2(x, y);
            if (allowNaN && float.IsNaN(x) && float.IsNaN(y)) return true;   // NaN = none
            return float.IsFinite(x) && float.IsFinite(y);
        }

        /// <summary>str8: u8 length + UTF-8, decoded to a managed string (keys, titles).</summary>
        public bool Str8(out string s, out ReadOnlySpan<byte> raw)
        {
            s = string.Empty;
            raw = default;
            if (!U8(out byte n) || !Take(n, out raw)) return false;
            s = Sanitize(Encoding.UTF8.GetString(raw));
            return true;
        }

        /// <summary>str: u16 length (≤ 4096) + UTF-8, not copied.</summary>
        public bool StrRaw(out ReadOnlySpan<byte> bytes)
        {
            bytes = default;
            return U16(out ushort n) && n <= MaxString && Take(n, out bytes);
        }

        /// <summary>str, copied into the pool.</summary>
        public bool Str(PoolBuilder pool, out int offset, out int length)
        {
            offset = -1;
            length = 0;
            if (!StrRaw(out var bytes)) return false;
            (offset, length) = pool.Add(bytes);
            return true;
        }

        /// <summary>str, copied into the pool with every '%' doubled (a cimgui format argument).</summary>
        public bool StrEscaped(PoolBuilder pool, out int offset, out int length)
        {
            offset = -1;
            length = 0;
            if (!StrRaw(out var bytes)) return false;
            (offset, length) = pool.AddEscaped(bytes);
            return true;
        }

        /// <summary>A slider's format: empty or invalid becomes the default ("%d" / "%.3f").</summary>
        public bool SliderFormat(BodyContext ctx, bool isInt, out int offset, out int length)
        {
            offset = -1;
            length = 0;
            if (!StrRaw(out var bytes)) return false;
            if (bytes.Length > 0 && IsValidSliderFormat(bytes, isInt))
            {
                (offset, length) = ctx.Pool.Add(bytes);
                return true;
            }
            if (isInt)
            {
                if (ctx.DefaultIntFmt < 0) (ctx.DefaultIntFmt, ctx.DefaultIntFmtLen) = ctx.Pool.Add("%d"u8);
                (offset, length) = (ctx.DefaultIntFmt, ctx.DefaultIntFmtLen);
            }
            else
            {
                if (ctx.DefaultFloatFmt < 0) (ctx.DefaultFloatFmt, ctx.DefaultFloatFmtLen) = ctx.Pool.Add("%.3f"u8);
                (offset, length) = (ctx.DefaultFloatFmt, ctx.DefaultFloatFmtLen);
            }
            return true;
        }
    }

    /// <summary>Control characters (NUL included) become spaces; keys, titles and error text.</summary>
    private static string Sanitize(string s)
    {
        foreach (char c in s)
        {
            if (char.IsControl(c))
            {
                var sb = new StringBuilder(s.Length);
                foreach (char d in s) sb.Append(char.IsControl(d) ? ' ' : d);
                return sb.ToString();
            }
        }
        return s;
    }

    /// <summary>
    /// Collects one list's strings; valid UTF-8 is copied as is, anything else re-encoded.
    /// Every string ends with a NUL; an embedded NUL becomes '?' (it would cut a label short).
    /// </summary>
    private sealed class PoolBuilder
    {
        private byte[] _buf = new byte[1024];
        private int _len;

        private static ReadOnlySpan<byte> Valid(ReadOnlySpan<byte> bytes) =>
            Utf8.IsValid(bytes) ? bytes : Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes));   // invalid → U+FFFD

        /// <summary>Adds a string, cut to at most <paramref name="maxBytes"/> without splitting a character.</summary>
        public (int Offset, int Length) Add(ReadOnlySpan<byte> bytes, int maxBytes = int.MaxValue)
        {
            bytes = Valid(bytes);
            if (bytes.Length > maxBytes)
            {
                int n = maxBytes;
                while (n > 0 && (bytes[n] & 0xC0) == 0x80) n--;
                bytes = bytes[..n];
            }
            Ensure(bytes.Length + 1);
            int offset = _len;
            AppendNoNul(bytes);
            _buf[_len++] = 0;
            return (offset, bytes.Length);
        }

        /// <summary>Adds a string with every '%' doubled.</summary>
        public (int Offset, int Length) AddEscaped(ReadOnlySpan<byte> bytes)
        {
            bytes = Valid(bytes);
            Ensure(bytes.Length * 2 + 1);
            int offset = _len;
            foreach (byte b in bytes)
            {
                if (b == (byte)'%') { _buf[_len++] = (byte)'%'; _buf[_len++] = (byte)'%'; }
                else _buf[_len++] = b == 0 ? (byte)'?' : b;
            }
            int length = _len - offset;
            _buf[_len++] = 0;
            return (offset, length);
        }

        /// <summary>Combo items: "a\0b\0c\0\0". Returns the offset.</summary>
        public int BeginItems() => _len;

        /// <summary>One item; an empty one becomes " " so it doesn't end the list early.</summary>
        public void AddItem(ReadOnlySpan<byte> bytes)
        {
            bytes = Valid(bytes);
            if (bytes.Length == 0) bytes = " "u8;
            Ensure(bytes.Length + 1);
            AppendNoNul(bytes);
            _buf[_len++] = 0;
        }

        /// <summary>Ends the items with the extra NUL; returns their length (without the final NUL).</summary>
        public int EndItems(int offset)
        {
            Ensure(1);
            int length = _len - offset;
            _buf[_len++] = 0;
            return length;
        }

        private void AppendNoNul(ReadOnlySpan<byte> bytes)
        {
            Span<byte> dst = _buf.AsSpan(_len, bytes.Length);
            bytes.CopyTo(dst);
            for (int i = 0; i < dst.Length; i++)
                if (dst[i] == 0) dst[i] = (byte)'?';
            _len += bytes.Length;
        }

        private void Ensure(int extra)
        {
            if (_len + extra <= _buf.Length) return;
            int size = _buf.Length;
            while (size < _len + extra) size *= 2;
            Array.Resize(ref _buf, size);
        }

        public byte[] ToPinned()
        {
            byte[] pinned = GC.AllocateArray<byte>(Math.Max(1, _len), pinned: true);
            _buf.AsSpan(0, _len).CopyTo(pinned);
            return pinned;
        }

        public void Clear() => _len = 0;
    }
}
