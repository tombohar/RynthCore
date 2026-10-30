// ============================================================================
//  RynthCore.Engine - ImGui/CodeEditor.cs
//  A syntax-coloured source editor for ImGui faces: ImGuiColorTextEdit
//  compiled into our cimgui.dll (native/cimgui/rynth_textedit_ext.cpp),
//  called through function pointers resolved from that module.
//
//  AC's render thread only, inside the ImGui frame. The native editor is
//  created on the first Render (it reads the ImGui style) and destroyed by
//  Dispose. Text crosses the boundary only on SetText/GetText, never per
//  frame: IsDirty compares the editor's change counter, so a 50 KB meta costs
//  nothing while it is being typed in.
// ============================================================================

using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace RynthCore.Engine.ImGuiBackend;

internal enum CodeLanguage { None = 0, Meta = 1, Lua = 2 }

internal sealed unsafe class CodeEditor : IDisposable
{
    // ── Exports ────────────────────────────────────────────────────────────
    private static bool _resolved, _available;
    private static delegate* unmanaged[Cdecl]<IntPtr> _create;
    private static delegate* unmanaged[Cdecl]<IntPtr, void> _destroy;
    private static delegate* unmanaged[Cdecl]<byte*, byte*, byte*, void> _setMetaVocabulary;
    private static delegate* unmanaged[Cdecl]<IntPtr, int, void> _setLanguage;
    private static delegate* unmanaged[Cdecl]<IntPtr, uint*, int, void> _setPalette;
    private static delegate* unmanaged[Cdecl]<IntPtr, int, int, int, int, void> _setOptions;
    private static delegate* unmanaged[Cdecl]<IntPtr, byte*, void> _setText;
    private static delegate* unmanaged[Cdecl]<IntPtr, int*, byte*> _getText;
    private static delegate* unmanaged[Cdecl]<IntPtr, uint> _getChangeCount;
    private static delegate* unmanaged[Cdecl]<IntPtr, byte*, float, float, int, int> _render;
    private static delegate* unmanaged[Cdecl]<IntPtr, int*, int*, void> _getCursor;
    private static delegate* unmanaged[Cdecl]<IntPtr, int, int, void> _setCursor;
    private static delegate* unmanaged[Cdecl]<IntPtr, float*, float*, void> _getCursorScreenPos;
    private static delegate* unmanaged[Cdecl]<IntPtr, int*, byte*> _getLinePrefix;
    private static delegate* unmanaged[Cdecl]<IntPtr, int, byte*, void> _replaceBeforeCursor;
    private static delegate* unmanaged[Cdecl]<IntPtr, int, void> _setCompletionKeysHeld;
    private static delegate* unmanaged[Cdecl]<IntPtr, void> _requestFocus;

    /// <summary>False when the loaded cimgui.dll was built without the editor.</summary>
    public static bool Available
    {
        get
        {
            if (!_resolved) Resolve();
            return _available;
        }
    }

    private static void Resolve()
    {
        _resolved = true;
        IntPtr module = EntryPoint.ImGuiNativeHandle;
        if (module == IntPtr.Zero) return;
        IntPtr Get(string name) => NativeLibrary.TryGetExport(module, name, out IntPtr p) ? p : IntPtr.Zero;

        _create = (delegate* unmanaged[Cdecl]<IntPtr>)Get("RynthTE_Create");
        _destroy = (delegate* unmanaged[Cdecl]<IntPtr, void>)Get("RynthTE_Destroy");
        _setMetaVocabulary = (delegate* unmanaged[Cdecl]<byte*, byte*, byte*, void>)Get("RynthTE_SetMetaVocabulary");
        _setLanguage = (delegate* unmanaged[Cdecl]<IntPtr, int, void>)Get("RynthTE_SetLanguage");
        _setPalette = (delegate* unmanaged[Cdecl]<IntPtr, uint*, int, void>)Get("RynthTE_SetPalette");
        _setOptions = (delegate* unmanaged[Cdecl]<IntPtr, int, int, int, int, void>)Get("RynthTE_SetOptions");
        _setText = (delegate* unmanaged[Cdecl]<IntPtr, byte*, void>)Get("RynthTE_SetText");
        _getText = (delegate* unmanaged[Cdecl]<IntPtr, int*, byte*>)Get("RynthTE_GetText");
        _getChangeCount = (delegate* unmanaged[Cdecl]<IntPtr, uint>)Get("RynthTE_GetChangeCount");
        _render = (delegate* unmanaged[Cdecl]<IntPtr, byte*, float, float, int, int>)Get("RynthTE_Render");
        _getCursor = (delegate* unmanaged[Cdecl]<IntPtr, int*, int*, void>)Get("RynthTE_GetCursor");
        _setCursor = (delegate* unmanaged[Cdecl]<IntPtr, int, int, void>)Get("RynthTE_SetCursor");
        _getCursorScreenPos = (delegate* unmanaged[Cdecl]<IntPtr, float*, float*, void>)Get("RynthTE_GetCursorScreenPos");
        _getLinePrefix = (delegate* unmanaged[Cdecl]<IntPtr, int*, byte*>)Get("RynthTE_GetLinePrefix");
        _replaceBeforeCursor = (delegate* unmanaged[Cdecl]<IntPtr, int, byte*, void>)Get("RynthTE_ReplaceBeforeCursor");
        _setCompletionKeysHeld = (delegate* unmanaged[Cdecl]<IntPtr, int, void>)Get("RynthTE_SetCompletionKeysHeld");
        _requestFocus = (delegate* unmanaged[Cdecl]<IntPtr, void>)Get("RynthTE_RequestFocus");

        _available = _create != null && _destroy != null && _setMetaVocabulary != null && _setLanguage != null
            && _setPalette != null && _setOptions != null && _setText != null && _getText != null
            && _getChangeCount != null && _render != null && _getCursor != null && _setCursor != null
            && _getCursorScreenPos != null && _getLinePrefix != null && _replaceBeforeCursor != null
            && _setCompletionKeysHeld != null && _requestFocus != null;
        if (!_available)
            RynthLog.UI("CodeEditor: this cimgui.dll has no RynthTE_* exports; ImGui source editors are unavailable.");
    }

    private static bool _vocabularySet;

    /// <summary>The Meta language's keywords (all Meta editors share them). Set once.</summary>
    public static void SetMetaVocabulary(string[] structWords, string[] conditionWords, string[] actionWords)
    {
        if (_vocabularySet || !Available) return;
        _vocabularySet = true;
        byte[] s = Utf8Z(string.Join('\n', structWords));
        byte[] c = Utf8Z(string.Join('\n', conditionWords));
        byte[] a = Utf8Z(string.Join('\n', actionWords));
        fixed (byte* ps = s, pc = c, pa = a)
            _setMetaVocabulary(ps, pc, pa);
    }

    // ── Instance ───────────────────────────────────────────────────────────
    private IntPtr _handle;
    private readonly byte[] _id;
    private readonly CodeLanguage _language;
    private readonly uint[] _palette;
    private string? _pendingText;
    private uint _cleanCount;
    private bool _held;

    /// <param name="palette">ImGui colours (ABGR) in ImGuiColorTextEdit's PaletteIndex order.</param>
    public CodeEditor(string id, CodeLanguage language, uint[] palette)
    {
        _id = Utf8Z(id);
        _language = language;
        _palette = palette;
    }

    public bool Created => _handle != IntPtr.Zero;

    private bool Ensure()
    {
        if (_handle != IntPtr.Zero) return true;
        if (!Available) return false;
        _handle = _create();
        if (_handle == IntPtr.Zero) return false;
        _setLanguage(_handle, (int)_language);
        fixed (uint* p = _palette)
            _setPalette(_handle, p, _palette.Length);
        _setOptions(_handle, 0, 0, 1, 4);
        SetTextNow(_pendingText ?? string.Empty);
        _pendingText = null;
        return true;
    }

    /// <summary>Replaces the text (and the undo history); the editor is clean afterwards.</summary>
    public void SetText(string text)
    {
        if (_handle == IntPtr.Zero) { _pendingText = text; return; }
        SetTextNow(text);
    }

    private void SetTextNow(string text)
    {
        byte[] bytes = Utf8Z(text);
        fixed (byte* p = bytes)
            _setText(_handle, p);
        _cleanCount = _getChangeCount(_handle);
    }

    public string GetText()
    {
        if (_handle == IntPtr.Zero) return _pendingText ?? string.Empty;
        int len;
        byte* p = _getText(_handle, &len);
        return len <= 0 ? string.Empty : Encoding.UTF8.GetString(p, len);
    }

    /// <summary>Counts text changes (edits, undo, redo, SetText).</summary>
    public uint ChangeCount => _handle == IntPtr.Zero ? 0 : _getChangeCount(_handle);

    /// <summary>Edited since the last SetText or MarkClean.</summary>
    public bool IsDirty => _handle != IntPtr.Zero && _getChangeCount(_handle) != _cleanCount;

    public void MarkClean()
    {
        if (_handle != IntPtr.Zero) _cleanCount = _getChangeCount(_handle);
    }

    /// <summary>Draws the editor as a child window of <paramref name="size"/>; returns whether it has keyboard focus.</summary>
    public bool Render(Vector2 size)
    {
        if (!Ensure()) return false;
        fixed (byte* id = _id)
            return _render(_handle, id, size.X, size.Y, 1) != 0;
    }

    /// <summary>Zero-based line and (tab-expanded) column of the cursor.</summary>
    public (int Line, int Column) Cursor
    {
        get
        {
            if (_handle == IntPtr.Zero) return (0, 0);
            int line, column;
            _getCursor(_handle, &line, &column);
            return (line, column);
        }
    }

    public void SetCursor(int line, int charIndex)
    {
        if (_handle != IntPtr.Zero) _setCursor(_handle, line, charIndex);
    }

    /// <summary>Screen position of the bottom-left corner of the cursor's cell (after Render).</summary>
    public Vector2 CursorScreenPos
    {
        get
        {
            if (_handle == IntPtr.Zero) return Vector2.Zero;
            float x, y;
            _getCursorScreenPos(_handle, &x, &y);
            return new Vector2(x, y);
        }
    }

    /// <summary>The cursor's line up to the cursor.</summary>
    public string LinePrefix()
    {
        if (_handle == IntPtr.Zero) return string.Empty;
        int len;
        byte* p = _getLinePrefix(_handle, &len);
        return len <= 0 ? string.Empty : Encoding.UTF8.GetString(p, len);
    }

    /// <summary>Replaces the <paramref name="bytes"/> UTF-8 bytes before the cursor with <paramref name="text"/> (one undo step).</summary>
    public void ReplaceBeforeCursor(int bytes, string text)
    {
        if (_handle == IntPtr.Zero) return;
        byte[] t = Utf8Z(text);
        fixed (byte* p = t)
            _replaceBeforeCursor(_handle, bytes, p);
    }

    /// <summary>While true the editor leaves Up/Down/PageUp/PageDown/Enter/Tab/Escape to a completion list.</summary>
    public bool CompletionKeysHeld
    {
        get => _held;
        set
        {
            if (_held == value || _handle == IntPtr.Zero) return;
            _held = value;
            _setCompletionKeysHeld(_handle, value ? 1 : 0);
        }
    }

    public void RequestFocus()
    {
        if (_handle != IntPtr.Zero) _requestFocus(_handle);
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        _pendingText = GetText();
        _destroy(_handle);
        _handle = IntPtr.Zero;
        _held = false;
    }

    private static byte[] Utf8Z(string s)
    {
        byte[] bytes = new byte[Encoding.UTF8.GetByteCount(s) + 1];
        Encoding.UTF8.GetBytes(s, 0, s.Length, bytes, 0);
        return bytes;
    }

    // ── Palettes ───────────────────────────────────────────────────────────

    /// <summary>
    /// The Meta editor's colours (MetaSourceEditor's): STATE:/IF:/DO:/NAV: teal
    /// (Keyword), conditions amber (KnownIdentifier), actions blue
    /// (PreprocIdentifier), braces violet, numbers green, "~~" comments grey.
    /// Lua uses the same slots (keywords teal, built-ins amber).
    /// </summary>
    public static uint[] RynthPalette()
    {
        static uint C(uint argb) => RynthTheme.Argb(argb);
        return new[]
        {
            C(0xFFD9E6F2), // Default
            RynthTheme.Accent, // Keyword
            C(0xFF99E699), // Number
            C(0xFFE6C07B), // String
            C(0xFFE6C07B), // CharLiteral
            C(0xFFCC88CC), // Punctuation
            C(0xFF8CA6BF), // Preprocessor
            C(0xFFD9E6F2), // Identifier
            RynthTheme.Highlight, // KnownIdentifier
            C(0xFF7BB8FF), // PreprocIdentifier
            C(0xFF6A7A8A), // Comment
            C(0xFF6A7A8A), // MultiLineComment
            RynthTheme.Field, // Background
            C(0xFFE6F2FF), // Cursor
            C(0x80305A80), // Selection
            C(0x80B02020), // ErrorMarker
            RynthTheme.BarButtonBorder, // ControlCharacter
            C(0x40F08000), // Breakpoint
            C(0xFF4F6478), // LineNumber
            C(0x40263A50), // CurrentLineFill
            C(0x20263A50), // CurrentLineFillInactive
            RynthTheme.WithAlpha(RynthTheme.DashBorder, 0x40), // CurrentLineEdge
        };
    }
}
