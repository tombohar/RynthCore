// ============================================================================
//  RynthCore.Engine - UI/Data/ChatInputPaste.cs
//  Ctrl+V into RynthChat's input line: clipboard text made into one chat line.
//  Pure (tools\ChatTellTests compiles it in).
// ============================================================================

using System;
using System.Text;

namespace RynthCore.Engine.UI.Data;

internal static class ChatInputPaste
{
    /// <summary>The input line never grows past this by pasting (a stray paste of a whole document).</summary>
    public const int MaxLineChars = 1000;

    /// <summary>
    /// <paramref name="clip"/> as one line: each line break or tab becomes a space (a run of
    /// them one space), other control characters are dropped, leading/trailing breaks go.
    /// </summary>
    public static string Clean(string? clip)
    {
        if (string.IsNullOrEmpty(clip)) return "";
        var sb = new StringBuilder(clip.Length);
        bool pendingSpace = false;
        foreach (char c in clip)
        {
            if (c is '\r' or '\n' or '\t') { pendingSpace = sb.Length > 0; continue; }
            if (c < 0x20 || c == 0x7F) continue;
            if (pendingSpace) { if (c != ' ' && sb[^1] != ' ') sb.Append(' '); pendingSpace = false; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>The line with the clipboard text inserted at the cursor (cut to fit <see cref="MaxLineChars"/>).</summary>
    public static string Insert(string text, int cursor, string? clip, out int newCursor)
    {
        cursor = Math.Clamp(cursor, 0, text.Length);
        string add = Clean(clip);
        int room = Math.Max(0, MaxLineChars - text.Length);
        if (add.Length > room)
        {
            add = add[..room];
            if (add.Length > 0 && char.IsHighSurrogate(add[^1])) add = add[..^1];   // never split a pair
        }
        newCursor = cursor + add.Length;
        return add.Length == 0 ? text : text[..cursor] + add + text[cursor..];
    }
}
