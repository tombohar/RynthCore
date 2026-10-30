// ============================================================================
//  RynthCore.Engine - ImGui/ImGuiStackGuard.cs
//  Puts ImGui's stacks back after plugin drawing code (audit 2026-10-21, 02 F4).
//  A plugin window's body (PluginWindowFace) is the plugin's own ImGui code; one
//  unbalanced PushStyleColor, a BeginChild left open, or an exception between a
//  Begin and its End used to carry over into every panel drawn after it that
//  frame (soft asserts, so it showed as garbage or a later native fault).
//
//  Take() records the stack depths with ImGui's own ErrorRecoveryStoreState
//  (exported by our cimgui build); Check() compares a fresh record with it;
//  RecoverTo() hands a record back to ErrorRecoveryTryToRecoverState, which ends
//  what was left open (windows and child windows, tables, tab bars, tree nodes,
//  groups, IDs, disabled blocks, colours, item flags, style vars, fonts, focus
//  scopes) down to that record. ImGui's own error report (assert, tooltip, debug
//  log) is switched off around that call: the caller logs once instead, and a
//  broken plugin can't put an error tooltip on the screen every frame.
//
//  Rule from ImGui: a record to recover to must be taken where the current
//  window's ID stack is at its base (the start of a window or child body), since
//  the recovery pops that window's IDs down to its first.
//
//  The record is opaque here (ImGuiStackState reads it for the log). Its size is
//  checked on first use; on a mismatch, or with a cimgui.dll without the exports,
//  Available is false and callers do as before. AC's render thread, inside an
//  ImGui frame and window (StoreState reads the current window).
// ============================================================================

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RynthCore.Engine.ImGuiBackend;

internal static unsafe class ImGuiStackGuard
{
    /// <summary>Room for one record (ImGui 1.91.6 writes 22 bytes).</summary>
    private const int RecordCapacity = 64;

    /// <summary>One stack record. Zeroed before ImGui writes it, so two records compare byte for byte.</summary>
    [StructLayout(LayoutKind.Sequential, Size = RecordCapacity)]
    internal struct Mark
    {
        private fixed byte _bytes[RecordCapacity];
    }

    /// <summary>A record's fields (ImGuiStackState.Names), read in place: no allocation.</summary>
    private static ReadOnlySpan<short> FieldsOf(in Mark m) =>
        MemoryMarshal.Cast<byte, short>(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in m), 1)))
            [..ImGuiStackState.Fields];

    private static delegate* unmanaged[Cdecl]<void*, void> _store;
    private static delegate* unmanaged[Cdecl]<void*, void> _recover;
    private static int _state;   // 0 = not resolved, 1 = available, -1 = off

    /// <summary>True when the exports exist and the record size checked out. Render thread, inside a window.</summary>
    public static bool Available
    {
        get
        {
            if (_state == 0) Resolve();
            return _state > 0;
        }
    }

    private static void Resolve()
    {
        _state = -1;
        IntPtr module = EntryPoint.ImGuiNativeHandle;
        if (module == IntPtr.Zero
            || !NativeLibrary.TryGetExport(module, "igErrorRecoveryStoreState", out IntPtr store)
            || !NativeLibrary.TryGetExport(module, "igErrorRecoveryTryToRecoverState", out IntPtr recover))
        {
            RynthLog.UI("ImGuiStackGuard: this cimgui.dll has no igErrorRecovery* exports; plugin drawing isn't checked for unbalanced stacks.");
            return;
        }
        _store = (delegate* unmanaged[Cdecl]<void*, void>)store;
        _recover = (delegate* unmanaged[Cdecl]<void*, void>)recover;

        const byte fill = 0xCC;
        byte* probe = stackalloc byte[RecordCapacity];
        new Span<byte>(probe, RecordCapacity).Fill(fill);
        _store(probe);
        if (!ImGuiStackState.WroteExpectedSize(new ReadOnlySpan<byte>(probe, RecordCapacity), fill))
        {
            RynthLog.UI($"ImGuiStackGuard: ImGui's stack record isn't the {ImGuiStackState.RecordBytes} bytes this engine expects; plugin drawing isn't checked.");
            return;
        }
        _state = 1;
    }

    /// <summary>The stack depths now. Only when <see cref="Available"/>.</summary>
    public static Mark Take()
    {
        Mark m = default;
        _store(&m);
        return m;
    }

    /// <summary>The stack depths now against <paramref name="before"/>; <paramref name="now"/> for the log.</summary>
    public static StackCheck Check(in Mark before, out Mark now)
    {
        now = Take();
        return ImGuiStackState.Compare(FieldsOf(before), FieldsOf(now));
    }

    /// <summary>True when the record was taken with the window's ID stack at its base: one a recovery may go back to.</summary>
    public static bool AtWindowBase(in Mark mark) => ImGuiStackState.IdsAtBase(FieldsOf(mark));

    /// <summary>What changed between two records, for the log ("colours +2, windows +1").</summary>
    public static string Describe(in Mark before, in Mark after) => ImGuiStackState.Describe(FieldsOf(before), FieldsOf(after));

    /// <summary>
    /// Ends and pops everything above <paramref name="mark"/> (taken where the window's ID stack
    /// was at its base). Windows or pops below the mark can't be put back. When windows below it
    /// were ended (ImGui's recovery then does nothing), only the colours, style vars and fonts
    /// above it are popped, which are the context's and safe from any window.
    /// </summary>
    public static void RecoverTo(in Mark mark)
    {
        Mark now = Take();
        if (!ImGuiStackState.CanRecover(FieldsOf(mark), FieldsOf(now)))
        {
            (int colours, int vars, int fonts) = ImGuiStackState.GlobalPops(FieldsOf(mark), FieldsOf(now));
            if (colours > 0) ImGuiNET.ImGui.PopStyleColor(colours);
            if (vars > 0) ImGuiNET.ImGui.PopStyleVar(vars);
            for (int i = 0; i < fonts; i++) ImGuiNET.ImGui.PopFont();
            return;
        }

        ImGuiNET.ImGuiIOPtr io = ImGuiNET.ImGui.GetIO();
        bool assert = io.ConfigErrorRecoveryEnableAssert, tooltip = io.ConfigErrorRecoveryEnableTooltip;
        bool debugLog = io.ConfigErrorRecoveryEnableDebugLog, recovery = io.ConfigErrorRecovery;
        io.ConfigErrorRecoveryEnableAssert = false;
        io.ConfigErrorRecoveryEnableTooltip = false;
        io.ConfigErrorRecoveryEnableDebugLog = false;
        io.ConfigErrorRecovery = true;
        try
        {
            fixed (Mark* m = &Unsafe.AsRef(in mark)) _recover(m);
        }
        finally
        {
            io.ConfigErrorRecoveryEnableAssert = assert;
            io.ConfigErrorRecoveryEnableTooltip = tooltip;
            io.ConfigErrorRecoveryEnableDebugLog = debugLog;
            io.ConfigErrorRecovery = recovery;
        }
    }
}
