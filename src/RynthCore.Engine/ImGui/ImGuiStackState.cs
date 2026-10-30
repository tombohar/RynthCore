// ============================================================================
//  RynthCore.Engine - ImGui/ImGuiStackState.cs
//  What a plugin's draw left on ImGui's stacks, from two of Dear ImGui's own
//  stack records (ImGuiErrorRecoveryState, taken by ImGuiStackGuard before and
//  after the call). Pure: no ImGui calls, so tools/SectionsTests compiles it.
//
//  The record is ImGui 1.91.6's ImGuiErrorRecoveryState: 11 shorts in the order
//  of Names below (imgui_internal.h; ImGuiSelfTest pins the version). Checked
//  against our cimgui.dll in a 32-bit probe on 2026-10-21: 22 bytes written,
//  and ImGuiStackGuard re-checks the size before its first use.
// ============================================================================

using System;
using System.Text;

namespace RynthCore.Engine.ImGuiBackend;

/// <summary>How a plugin's draw left ImGui's stacks.</summary>
internal enum StackCheck
{
    /// <summary>As it found them.</summary>
    Balanced,
    /// <summary>It left things open (pushes, a child window, a group...): ImGui can unwind them.</summary>
    LeftOpen,
    /// <summary>It ended or popped something it didn't begin (the host's window, colours): can't be put back.</summary>
    ClosedTooMuch,
}

internal static class ImGuiStackState
{
    /// <summary>Shorts in ImGuiErrorRecoveryState (1.91.6).</summary>
    public const int Fields = 11;

    /// <summary>Bytes ImGui writes for one record.</summary>
    public const int RecordBytes = Fields * sizeof(short);

    /// <summary>Index of the window stack's depth (Begin / BeginChild).</summary>
    public const int WindowStack = 0;

    /// <summary>What each field counts, in the record's order.</summary>
    public static readonly string[] Names =
    {
        "windows", "IDs", "tree nodes", "colours", "style vars", "fonts",
        "focus scopes", "groups", "item flags", "popups", "disabled",
    };

    /// <summary>
    /// Fields that count the current window's own stacks (its IDs, its tree depth): they compare
    /// only at the same window depth. The rest belong to the context.
    /// </summary>
    public static bool PerWindow(int field) => field is 1 or 2;

    /// <summary>The draw's effect: <paramref name="after"/> against <paramref name="before"/>.</summary>
    public static StackCheck Compare(ReadOnlySpan<short> before, ReadOnlySpan<short> after)
    {
        if (after[WindowStack] < before[WindowStack]) return StackCheck.ClosedTooMuch;
        bool sameWindow = after[WindowStack] == before[WindowStack];
        bool changed = !sameWindow;
        for (int i = 0; i < Fields; i++)
        {
            if (i == WindowStack || (!sameWindow && PerWindow(i))) continue;
            if (after[i] < before[i]) return StackCheck.ClosedTooMuch;
            if (after[i] != before[i]) changed = true;
        }
        return changed ? StackCheck.LeftOpen : StackCheck.Balanced;
    }

    /// <summary>
    /// The window's ID stack holds only the window's own ID: a record taken here is one ImGui's
    /// recovery can go back to (it pops a window's IDs down to that one).
    /// </summary>
    public static bool IdsAtBase(ReadOnlySpan<short> record) => record[1] == 1;

    /// <summary>Indexes of the context-wide stacks a pop works on from any window.</summary>
    public const int Colours = 3, StyleVars = 4, Fonts = 5;

    /// <summary>
    /// ImGui's recovery can unwind to <paramref name="mark"/> only from the same window depth or
    /// deeper; when the draw ended windows below the mark (the host's own), it does nothing.
    /// </summary>
    public static bool CanRecover(ReadOnlySpan<short> mark, ReadOnlySpan<short> now) => now[WindowStack] >= mark[WindowStack];

    /// <summary>
    /// When <see cref="CanRecover"/> is false: the colours, style vars and fonts to pop to get back
    /// to <paramref name="mark"/> (those stacks belong to the context, not a window). 0 where none.
    /// </summary>
    public static (int Colours, int StyleVars, int Fonts) GlobalPops(ReadOnlySpan<short> mark, ReadOnlySpan<short> now) =>
        (Math.Max(0, now[Colours] - mark[Colours]), Math.Max(0, now[StyleVars] - mark[StyleVars]), Math.Max(0, now[Fonts] - mark[Fonts]));

    /// <summary>
    /// "windows +1, colours +2, style vars +1": the fields that changed, with the change (a
    /// window's own IDs and tree depth only when the window depth is the same).
    /// </summary>
    public static string Describe(ReadOnlySpan<short> before, ReadOnlySpan<short> after)
    {
        var sb = new StringBuilder();
        bool sameWindow = after[WindowStack] == before[WindowStack];
        for (int i = 0; i < Fields; i++)
        {
            int d = after[i] - before[i];
            if (d == 0 || (!sameWindow && PerWindow(i))) continue;
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(Names[i]).Append(' ').Append(d > 0 ? "+" : "").Append(d);
        }
        return sb.Length > 0 ? sb.ToString() : "nothing";
    }

    /// <summary>
    /// The size check on first use: <paramref name="record"/> was filled with
    /// <paramref name="fill"/> and then written by ImGui. True when exactly the first
    /// <see cref="RecordBytes"/> bytes were written (the layout this file expects).
    /// </summary>
    public static bool WroteExpectedSize(ReadOnlySpan<byte> record, byte fill)
    {
        int written = 0;
        for (int i = 0; i < record.Length; i++)
            if (record[i] != fill) written = i + 1;
        // A short that happens to equal fill|fill is a value no stack reaches (0xCCCC = -13108).
        return written == RecordBytes;
    }
}
