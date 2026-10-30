using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.Engine.Hooking;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.Compatibility;

internal static class ChatCallbackHooks
{
    private enum IncomingChatMode
    {
        WrapperQueuedText,
        WrapperQueuedTextHostEat,
        AddTextToScrollHostEat
    }

    private const int PsRefBufferWideDataOffset = 20;
    private const int MaxIncomingChatChars = 1024;
    private const int MaxDataLineChars = 65536;
    private const int IncomingChatAddTextToScrollVa = 0x005649F0;
    private const int IncomingChatWrapperVa = 0x0058A000;
    private const int OutgoingChatVa = 0x005821A0;
    private const bool EnableIncomingHook = true;
    private const bool EnableOutgoingHook = true;
    private static IncomingChatMode CurrentIncomingMode => IncomingChatMode.AddTextToScrollHostEat;

    // Verified unique + lands at 0x005649F0 offline (tools/pe_pattern.py).
    private static readonly byte?[] IncomingChatAddTextToScrollPattern =
    [
        0x81, 0xEC, 0x48, 0x09, 0x00, 0x00, 0x8A, 0x84
    ];

    // Verified unique + lands at 0x0058A000 offline (tools/pe_pattern.py).
    private static readonly byte?[] IncomingChatWrapperPattern =
    [
        0xA1, 0xE4, 0x0B, 0x87, 0x00, 0x85, 0xC0, 0x75,
        0x06, 0xB8, 0x01, 0x00, 0x00, 0x00, 0xC3, 0x56
    ];

    private static readonly byte?[] OutgoingChatPattern =
    [
        0x83, 0xEC, 0x0C, 0x53, 0x55, 0x56, 0x57, 0x8B,
        0xF9, 0xE8, null, null, null, null, 0x85, 0xC0,
        0x8B, 0x74, 0x24, 0x20, 0x74, 0x30, 0x8B, 0x06,
        0x50, 0xFF, 0x15, null, null, null, null, 0x8B,
        0xD8, 0xC7, 0x44, 0x24, 0x20, 0x00, 0x00, 0x00,
        0x00, 0xE8, null, null, null, null, 0x8B, 0x08,
        0x8D, 0x54, 0x24, 0x20, 0x52, 0x53, 0x50, 0xFF,
        0x51, 0x20, 0x8B, 0x44, 0x24, 0x20, 0x85, 0xC0,
        0x0F, 0x85, null, null, null, null, 0x8B, 0x44,
        0x24, 0x24, 0x6A, 0x00
    ];

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int IncomingChatWrapperDelegate(int flags, IntPtr text, int chatChannel);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int OutgoingChatDelegate(IntPtr thisPtr, IntPtr text, uint commandSource);

    private static IncomingChatWrapperDelegate? _originalIncomingChatWrapper;
    private static IncomingChatWrapperDelegate? _incomingChatWrapperDetour;
    private static OutgoingChatDelegate? _originalOutgoingChat;
    private static OutgoingChatDelegate? _outgoingChatDetour;
    private static IntPtr _incomingAddress;
    private static IntPtr _originalIncomingChatAddTextPtr;
    private static IntPtr _outgoingAddress;
    private static IntPtr _outgoingChatTrampolinePtr;
    private static string _statusMessage = "Not probed yet.";
    private static int _incomingChatSuppressionEnabled;
    private static int _incomingCallCount;

    // ── Held incoming chat (IncomingChatHold.cs) ─────────────────────────────
    // AddTextToScroll lines wait for the plugins' verdict before AC prints them, so a
    // line a plugin eats never reaches AC's chat window. Main-thread only, except the
    // beats and the switch.
    private static readonly HeldChatQueue _held = new();
    private static readonly Action<HeldChatLine> _printHeld = PrintHeldLine;
    private static bool _incomingByPattern;      // AddTextToScroll itself was found by its pattern
    private static bool _replayReady;            // PStringBase<wchar_t> ctor/dtor/null buffer found by pattern
    private static int _holdSwitch = 1;          // /rc chathold on|off (not saved; on at every start)
    private static long _drainBeatMs;            // last AcMainThreadQueue.Drain on AC's main thread
    private static int _eatLogCount;
    [ThreadStatic] private static int _replayDepth;   // inside our own print of a held line

    // Captured live from the outgoing-chat detour so we can re-invoke AC's outgoing
    // chat function DIRECTLY (deterministic) instead of simulating keystrokes into
    // the native chat bar. Keystroke simulation depends on the bar's open/closed
    // state, which the first simulated send corrupts — so the second send silently
    // fails to submit ("works once, then stops"). Calling the function directly has
    // no chat-bar state and is fully repeatable.
    private static IntPtr _lastOutgoingThis;
    private static uint _lastOutgoingSource = 8;

    public static bool IncomingInstalled { get; private set; }
    public static bool OutgoingInstalled { get; private set; }
    public static bool IsInstalled => (!EnableIncomingHook || IncomingInstalled) && (!EnableOutgoingHook || OutgoingInstalled);
    public static string StatusMessage => _statusMessage;
    public static bool IsOutgoingHookReady => OutgoingInstalled && _originalOutgoingChat != null;
    internal static IntPtr OutgoingAddress => _outgoingAddress;

    public static void SetIncomingChatSuppression(bool enabled)
    {
        Interlocked.Exchange(ref _incomingChatSuppressionEnabled, enabled ? 1 : 0);
    }

    public static void Initialize()
    {
        if (IsInstalled)
            return;

        if (!AcClientModule.TryReadTextSection(out AcClientTextSection textSection))
        {
            _statusMessage = "acclient.exe not available.";
            return;
        }

        try
        {
            string incomingMode = GetIncomingModeLabel(CurrentIncomingMode);
            string incomingStatus = EnableIncomingHook ? "unavailable" : "disabled";
            string outgoingStatus = EnableOutgoingHook ? "unavailable" : "disabled";
            bool incomingVerified = !EnableIncomingHook;

            if (EnableIncomingHook && !IncomingInstalled)
                IncomingInstalled = TryInstallIncomingHook(textSection, out incomingStatus);

            if (IncomingInstalled)
            {
                incomingVerified = true;
                incomingStatus = $"0x{_incomingAddress.ToInt32():X8} ({incomingMode})";
                if (_originalIncomingChatAddTextPtr != IntPtr.Zero)
                    _replayReady = _incomingByPattern && TryPrepareReplay(textSection);
                RynthLog.Compat($"Compat: incoming chat hold {(_replayReady ? "ready" : "unavailable (lines print before plugins hear them, as before)")}.");
            }

            if (EnableOutgoingHook && !OutgoingInstalled)
            {
                if (TryResolveOutgoingOffset(textSection, incomingVerified, out int outgoingOff, out string outgoingResolution))
                {
                    try
                    {
                        _outgoingAddress = new IntPtr(textSection.TextBaseVa + outgoingOff);
                        _outgoingChatDetour = OutgoingChatDetour;
                        IntPtr outgoingPtr = Marshal.GetFunctionPointerForDelegate(_outgoingChatDetour);
                        _outgoingChatTrampolinePtr = MinHook.HookCreate(_outgoingAddress, outgoingPtr);
                        _originalOutgoingChat = Marshal.GetDelegateForFunctionPointer<OutgoingChatDelegate>(_outgoingChatTrampolinePtr);
                        Thread.MemoryBarrier();
                        MinHook.Enable(_outgoingAddress);
                        OutgoingInstalled = true;
                        outgoingStatus = $"0x{_outgoingAddress.ToInt32():X8} ({outgoingResolution})";
                    }
                    catch (Exception ex)
                    {
                        outgoingStatus = $"hook failed ({ex.Message})";
                        RynthLog.Compat($"Compat: outgoing chat hook unavailable - {ex.Message}");
                    }
                }
                else
                {
                    outgoingStatus = outgoingResolution;
                    if (!outgoingResolution.StartsWith("skipped", StringComparison.Ordinal))
                        RynthLog.Compat($"Compat: outgoing chat hook unavailable - {outgoingResolution}");
                }
            }

            if (OutgoingInstalled && !outgoingStatus.StartsWith("0x", StringComparison.Ordinal))
                outgoingStatus = $"0x{_outgoingAddress.ToInt32():X8}";

            if (!IncomingInstalled && !OutgoingInstalled)
            {
                _statusMessage = $"No chat hooks installed. incoming={incomingStatus}, outgoing={outgoingStatus}.";
                RynthLog.Compat($"Compat: chat callback hook failed - {_statusMessage}");
                return;
            }

            _statusMessage = $"Ready. incoming={incomingStatus}, outgoing={outgoingStatus}.";
            RynthLog.Verbose($"Compat: chat callback hooks ready - incoming={incomingStatus}, outgoing={outgoingStatus}");
        }
        catch (Exception ex)
        {
            _statusMessage = ex.Message;
            RynthLog.Compat($"Compat: chat callback hook failed - {ex.Message}");
        }
    }

    private static bool TryInstallIncomingHook(AcClientTextSection textSection, out string incomingStatus)
    {
        if (TryInstallPreferredIncomingHook(textSection, out incomingStatus))
            return true;

        if (CurrentIncomingMode is not IncomingChatMode.WrapperQueuedText and not IncomingChatMode.WrapperQueuedTextHostEat)
        {
            RynthLog.Verbose("Compat: preferred incoming chat seam unavailable - falling back to wrapper path.");
            return TryInstallIncomingWrapperHook(textSection, out incomingStatus);
        }

        return false;
    }

    private static bool TryInstallPreferredIncomingHook(AcClientTextSection textSection, out string incomingStatus)
    {
        return CurrentIncomingMode switch
        {
            IncomingChatMode.AddTextToScrollHostEat => TryInstallIncomingAddTextHook(textSection, out incomingStatus),
            IncomingChatMode.WrapperQueuedText => TryInstallIncomingWrapperHook(textSection, out incomingStatus),
            IncomingChatMode.WrapperQueuedTextHostEat => TryInstallIncomingWrapperHook(textSection, out incomingStatus),
            _ => TryInstallIncomingWrapperHook(textSection, out incomingStatus)
        };
    }

    private static bool TryInstallIncomingWrapperHook(AcClientTextSection textSection, out string incomingStatus)
    {
        HookResolver.ResolveResult resolved = HookResolver.Resolve(textSection, "ChatCallback.IncomingChatWrapper", IncomingChatWrapperPattern, IncomingChatWrapperVa);
        if (!resolved.Success)
        {
            incomingStatus = $"unresolved (VA 0x{IncomingChatWrapperVa:X8})";
            RynthLog.Compat($"Compat: incoming chat hook unavailable - {incomingStatus}");
            return false;
        }

        try
        {
            _incomingAddress = resolved.Address;
            _incomingChatWrapperDetour = IncomingChatWrapperDetour;
            IntPtr incomingPtr = Marshal.GetFunctionPointerForDelegate(_incomingChatWrapperDetour);
            _originalIncomingChatWrapper = Marshal.GetDelegateForFunctionPointer<IncomingChatWrapperDelegate>(MinHook.HookCreate(_incomingAddress, incomingPtr));
            Thread.MemoryBarrier();
            MinHook.Enable(_incomingAddress);
            incomingStatus = $"0x{_incomingAddress.ToInt32():X8} ({GetIncomingModeLabel(CurrentIncomingMode)})";
            return true;
        }
        catch (Exception ex)
        {
            incomingStatus = $"hook failed ({ex.Message})";
            RynthLog.Compat($"Compat: incoming chat hook unavailable - {ex.Message}");
            return false;
        }
    }

    private static bool TryInstallIncomingAddTextHook(AcClientTextSection textSection, out string incomingStatus)
    {
        HookResolver.ResolveResult resolved = HookResolver.Resolve(textSection, "ChatCallback.IncomingChatAddTextToScroll", IncomingChatAddTextToScrollPattern, IncomingChatAddTextToScrollVa);
        if (!resolved.Success)
        {
            incomingStatus = $"unresolved (VA 0x{IncomingChatAddTextToScrollVa:X8})";
            RynthLog.Compat($"Compat: incoming chat hook unavailable - {incomingStatus}");
            return false;
        }

        try
        {
            unsafe
            {
                _incomingAddress = resolved.Address;
                _incomingByPattern = resolved.Source == HookResolver.ResolveSource.PatternScan;
                delegate* unmanaged[Thiscall]<IntPtr, IntPtr, uint, uint, IntPtr, int> pDetour = &IncomingChatAddTextDetour;
                MinHook.Hook(_incomingAddress, (IntPtr)pDetour, out _originalIncomingChatAddTextPtr);
            }
            incomingStatus = $"0x{_incomingAddress.ToInt32():X8} ({GetIncomingModeLabel(CurrentIncomingMode)})";
            return true;
        }
        catch (Exception ex)
        {
            incomingStatus = $"hook failed ({ex.Message})";
            RynthLog.Compat($"Compat: incoming chat hook unavailable - {ex.Message}");
            return false;
        }
    }

    private static int IncomingChatWrapperDetour(int flags, IntPtr text, int chatChannel)
    {
        RecursionGuard.Tick("ChatCallbackHooks.IncomingChatWrapper");
        try
        {
            return CurrentIncomingMode switch
            {
                IncomingChatMode.WrapperQueuedText => QueueIncomingAfterOriginal(flags, text, chatChannel),
                IncomingChatMode.WrapperQueuedTextHostEat => QueueIncomingWithHostEat(flags, text, chatChannel),
                _ => _originalIncomingChatWrapper!(flags, text, chatChannel)
            };
        }
        catch (Exception ex)
        {
            try { RynthLog.Compat($"Compat: incoming wrapper detour error - {ex.GetType().Name}: {ex.Message}"); } catch { }
            try { return _originalIncomingChatWrapper!(flags, text, chatChannel); } catch { return 0; }
        }
    }

    // [UnmanagedCallersOnly] generates a true native thiscall entry point — no
    // delegate thunk, no hidden GC-transition overhead that the delegate-based
    // approach adds.  This mirrors Chorizite's working ChatHooks pattern.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvThiscall) })]
    private static unsafe int IncomingChatAddTextDetour(IntPtr thisPtr, IntPtr text, uint chatType, uint unknown, IntPtr stringInfo)
    {
        RecursionGuard.Tick("ChatCallbackHooks.IncomingChatAddText");
        var pOriginal = (delegate* unmanaged[Thiscall]<IntPtr, IntPtr, uint, uint, IntPtr, int>)_originalIncomingChatAddTextPtr;

        // 1. Read string BEFORE original — buffer may be freed after
        bool truncated = false;
        string? raw = (text != IntPtr.Zero) ? ReadIncomingChatRaw(text, chatType, out truncated) : null;
        string? line = raw?.TrimEnd('\r', '\n');

        // 1b. The engine's own server-data replies (Aelrynth /mastery-data it asked for):
        // not drawn, not held and not passed on. Every caller of AddTextToScroll ignores its
        // return value (all 394 call sites in the retail client, checked 2026-10-01).
        // Not inside our own reprint of a held line: that line was already offered here when
        // it first arrived (and wasn't ours, or it wouldn't have been held), so offering it
        // again could take a stale line for the answer to a request sent since.
        if (line != null && _replayDepth == 0 && EatOwnReply(line, chatType))
            return 0;

        // 2. Hold the line until the plugins have heard it (IncomingChatHold.cs): AC prints it
        //    from the next main-thread drain unless a plugin eats it. Anything unexpected here
        //    falls through to the old path: print now, plugins hear it afterwards.
        ChatHoldDecision decision = ChatHoldDecision.PassThrough;
        bool handedOver = false;   // QueueChatWindowText already saw the line (engine HUD feed and plugins)
        try
        {
            decision = DecideHold(raw, truncated, stringInfo);
            if (decision == ChatHoldDecision.Hold)
            {
                var held = new HeldChatLine(raw!, chatType, unknown, thisPtr, Environment.TickCount64);
                handedOver = true;
                if (PluginManager.QueueChatWindowText(line, chatType, held))
                {
                    _held.Add(held);
                    return 1; // what AddTextToScroll returns for a line it handled
                }
                decision = ChatHoldDecision.FlushThenPassThrough; // plugins aren't taking chat right now
            }
            if (decision == ChatHoldDecision.FlushThenPassThrough)
                ReleaseHeld(flushAll: true);
        }
        catch
        {
            // Fall through and print. (If the line was handed over, it isn't handed over twice.)
        }

        // 3. Call original
        int result = pOriginal(thisPtr, text, chatType, unknown, stringInfo);

        // 4. Queue AFTER original returns — game state is consistent
        if (line != null && !handedOver)
            QueueIncomingChatLine(line, chatType);

        return result;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool EatOwnReply(string line, uint chatType)
    {
        // Server replies only: a line in a player's chat type is someone typing "~ael1 ..." or
        // "Unknown command: ..." to feed the readers or hide their own chat.
        if (Net.ChatOrigin.IsPlayerAuthored(chatType)) return false;
        try { return MasteryFeed.OnIncomingLine(line) || VaultFeed.OnIncomingLine(line); }
        catch { return false; }
    }

    /// <summary>Hold this line, print it now, or print everything held and then it. AC's main thread or not.</summary>
    private static ChatHoldDecision DecideHold(string? raw, bool truncated, IntPtr stringInfo)
    {
        bool onMain = MainThreadGuard.IsOnMainThread();
        bool replaying = _replayDepth > 0;

        // Lines whose verdict is in print first, so a line printed now keeps its place after them.
        if (onMain && !replaying && _held.Count > 0)
            ReleaseHeld(flushAll: false);

        var inputs = new ChatHoldInputs(
            Enabled: HoldActive,
            Disarmed: AcMainThreadQueue.IsDisarmed,
            OnMainThread: onMain,
            Replaying: replaying,
            HasText: !string.IsNullOrEmpty(raw),
            Truncated: truncated,
            HasStringInfo: stringInfo != IntPtr.Zero,
            NowMs: Environment.TickCount64,
            PumpBeatMs: PluginManager.ChatPumpBeatMs,
            DrainBeatMs: Volatile.Read(ref _drainBeatMs),
            HeldCount: _held.Count);
        return HeldChatQueue.Decide(inputs);
    }

    private static bool HoldActive => _replayReady && Volatile.Read(ref _holdSwitch) != 0;

    /// <summary>
    /// Called by AcMainThreadQueue.Drain (Client::UseTime and EndScene, AC's main thread): the
    /// beat that says held lines will be printed, then prints the ones that may print now.
    /// After teardown starts it prints everything still held.
    /// </summary>
    internal static void OnMainThreadDrain(bool disarmed)
    {
        if (!MainThreadGuard.IsOnMainThread())
            return;
        if (!disarmed)
            Volatile.Write(ref _drainBeatMs, Environment.TickCount64);
        if (_held.Count == 0 || _replayDepth > 0)
            return;
        try { ReleaseHeld(flushAll: disarmed || !HoldActive); }
        catch { }
    }

    /// <summary>Prints held lines that may print now (AC's main thread, never inside our own print).</summary>
    private static void ReleaseHeld(bool flushAll)
    {
        long eatenBefore = _held.EatenTotal;
        long lateBefore = _held.TimedOutTotal;
        _held.Release(Environment.TickCount64, flushAll, _printHeld);

        if (_held.EatenTotal != eatenBefore && _eatLogCount < 5)
        {
            _eatLogCount++;
            RynthLog.Compat($"Compat: chat hold kept {_held.EatenTotal - eatenBefore} line(s) a plugin ate out of AC's chat window (total {_held.EatenTotal}).");
        }
        if (_held.TimedOutTotal != lateBefore && _held.TimedOutTotal <= 5)
            RynthLog.Compat($"Compat: chat hold printed {_held.TimedOutTotal - lateBefore} line(s) without a plugin verdict after {HeldChatQueue.HoldTimeoutMs} ms (plugin pump slow; total {_held.TimedOutTotal}).");
    }

    /// <summary>Prints one held line through AC's own AddTextToScroll, exactly as it came in.</summary>
    private static unsafe void PrintHeldLine(HeldChatLine h)
    {
        var pOriginal = (delegate* unmanaged[Thiscall]<IntPtr, IntPtr, uint, uint, IntPtr, int>)_originalIncomingChatAddTextPtr;
        _replayDepth++;
        try
        {
            fixed (char* chars = h.Raw) // .NET strings end in a NUL, as the PStringBase ctor needs
            {
                var wide = OutgoingWidePString.Create((ushort*)chars);
                try
                {
                    // StringInfo is always null here: lines that came with one are never held.
                    pOriginal(h.This, (IntPtr)(&wide), h.ChatType, h.Unknown, IntPtr.Zero);
                }
                finally { wide.Dispose(); }
            }
        }
        finally { _replayDepth--; }
    }

    /// <summary>
    /// Holding needs to build AC strings itself to print a held line: the PStringBase&lt;wchar_t&gt;
    /// ctor, dtor and empty buffer, all found by pattern (never a guessed address).
    /// </summary>
    private static bool TryPrepareReplay(AcClientTextSection textSection)
    {
        try
        {
            var ctor = HookResolver.Resolve(textSection, "ChatCallback.PStringW_ctor", PatWidePStringCtor, 0x00402730);
            var dtor = HookResolver.Resolve(textSection, "ChatCallback.PStringW_dtor", PatWidePStringDtor, 0x004011B0);
            var nul = HookResolver.ResolveData(textSection, "ChatCallback.PStringW_NullBuffer", PatXrefWideNullBuffer, 2, 0x00818340);
            if (ctor.Source != HookResolver.ResolveSource.PatternScan
                || dtor.Source != HookResolver.ResolveSource.PatternScan
                || nul.Source != HookResolver.ResolveSource.PatternScan)
                return false;
            // Resolve OutgoingWidePString's statics now, not on AC's thread at the first held line.
            return OutgoingWidePString.Warm();
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"Compat: chat hold setup failed - {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>/rc chathold on|off.</summary>
    internal static void SetHoldEnabled(bool on) => Volatile.Write(ref _holdSwitch, on ? 1 : 0);

    internal static bool HoldSwitchOn => Volatile.Read(ref _holdSwitch) != 0;

    /// <summary>One line for /rc chathold status.</summary>
    internal static string HoldStatusText()
    {
        string state = !IncomingInstalled ? "unavailable (incoming chat isn't hooked; in Decal bridge mode Decal owns it)"
            : !_replayReady ? "unavailable (the client's string functions weren't found, or another hook sits on AddTextToScroll)"
            : HoldSwitchOn ? "ON" : "OFF";
        return $"Chat hold is {state}. Since start: {_held.HeldTotal} held, {_held.ShownTotal} shown, " +
               $"{_held.EatenTotal} eaten by plugins, {_held.TimedOutTotal} shown late (no verdict in {HeldChatQueue.HoldTimeoutMs} ms), " +
               $"{_held.PrintFailedTotal} print errors; {_held.Count} waiting now.";
    }

    /// <summary>
    /// Reads the line exactly as AC got it (line breaks kept). <paramref name="truncated"/> is
    /// true when it is longer than <see cref="MaxIncomingChatChars"/>: plugins get the first
    /// part, as before, but such a line is never held (it couldn't be printed back whole).
    /// A server data line ("~ael1 ...") is read in full instead (up to
    /// <see cref="MaxDataLineChars"/>) but still reports truncated, so it is never held either.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string? ReadIncomingChatRaw(IntPtr pStringBase, uint chatType, out bool truncated)
    {
        truncated = false;
        try
        {
            // PStringBase<ushort> is a 4-byte struct: a single pointer to
            // PSRefBufferCharData<ushort>.  The char data (m_data) starts at
            // offset 0 in PSRefBufferCharData — no header fields before it.
            IntPtr charData = Marshal.ReadIntPtr(pStringBase);
            if (charData == IntPtr.Zero)
                return null;

            int length = 0;
            while (length < MaxIncomingChatChars)
            {
                short ch = Marshal.ReadInt16(charData, length * 2);
                if (ch == 0)
                    break;
                length++;
            }
            // Lines this long are never held (chat-eat's limit, unchanged): they print at once
            // and plugins hear them afterwards, as before. Set from the usual cap BEFORE the
            // data-line read below, so a long data line read in full still counts as too long.
            truncated = length >= MaxIncomingChatChars;

            // A server data line ("~ael1 {json}", one line for every skill) runs past the
            // usual cap: read it to its terminator, up to 64K characters.
            if (length == MaxIncomingChatChars
                && MasteryWire.IsDataLine(Marshal.PtrToStringUni(charData, 40)))
            {
                while (length < MaxDataLineChars)
                {
                    short ch = Marshal.ReadInt16(charData, length * 2);
                    if (ch == 0)
                        break;
                    length++;
                }
            }

            string? raw = length > 0 ? Marshal.PtrToStringUni(charData, length) : null;

            int count = Interlocked.Increment(ref _incomingCallCount);
            if (count <= 0)
                RynthLog.Verbose($"Compat: incoming chat #{count} type={chatType} len={length}");

            return raw;
        }
        catch { return null; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void QueueIncomingChatLine(string line, uint chatType)
    {
        try { PluginManager.QueueChatWindowText(line, chatType); }
        catch { }
    }

    private static int OutgoingChatDetour(IntPtr thisPtr, IntPtr text, uint commandSource)
    {
        RecursionGuard.Tick("ChatCallbackHooks.OutgoingChat");
        try
        {
            string? line = ReadWidePString(text);
            LogoffOriginProbe.RecordChat($"AC OutgoingChat src={commandSource}", line);

            // Capture the chat-manager 'this' + command source from the live submit
            // so ChatCommandDispatcher can re-invoke this function directly later.
            if (thisPtr != IntPtr.Zero)
            {
                _lastOutgoingThis   = thisPtr;
                _lastOutgoingSource = commandSource;
            }

            // RynthCore engine commands (/rc ...) — consume before plugins or
            // AC see the line so the user never broadcasts "/rc resetbar" and
            // a hidden overlay bar stays recoverable. Typed by the player: every
            // reply goes to chat.
            if (RynthCoreChatCommands.TryHandle(line, ChatCommandOrigin.Player))
                return 1;

            // A /rc line a plugin runs while it handles this one (RynthAi's /ra videopatch) is the
            // player's too, so it answers in chat.
            using (RynthCoreChatCommands.PlayerLine())
            {
                if (PluginManager.DispatchChatBarEnter(line))
                    return 1;
            }

            return _originalOutgoingChat!(thisPtr, text, commandSource);
        }
        catch (Exception ex)
        {
            try { RynthLog.Compat($"Compat: outgoing chat detour error - {ex.GetType().Name}: {ex.Message}"); } catch { }
            try { return _originalOutgoingChat!(thisPtr, text, commandSource); } catch { return 0; }
        }
    }

    private static string? ReadWidePString(IntPtr pStringBase)
    {
        if (pStringBase == IntPtr.Zero)
            return null;

        try
        {
            IntPtr firstPtr = Marshal.ReadIntPtr(pStringBase);
            if (firstPtr == IntPtr.Zero)
                return null;

            // Some seams hand us a WidePString whose first field already points at
            // the wchar_t buffer. Others hand us a PSRefBuffer-backed PStringBase
            // that needs the +0x14 data offset. Probe both and keep the more sane one.
            string? direct = TryReadUtf16String(firstPtr);
            string? buffered = TryReadUtf16String(firstPtr + PsRefBufferWideDataOffset);
            return ChooseBestWideString(direct, buffered);
        }
        catch
        {
            return null;
        }
    }

    private static string? TryReadUtf16String(IntPtr data)
    {
        if (data == IntPtr.Zero)
            return null;

        try
        {
            int length = 0;
            while (length < MaxIncomingChatChars)
            {
                short ch = Marshal.ReadInt16(data, length * sizeof(char));
                if (ch == 0)
                    break;

                if (!IsLikelyWideChar((char)ch))
                    return null;

                length++;
            }

            return length > 0 ? Marshal.PtrToStringUni(data, length) : string.Empty;
        }
        catch
        {
            return null;
        }
    }

    private static string? ChooseBestWideString(string? direct, string? buffered)
    {
        int directScore = ScoreCandidate(direct);
        int bufferedScore = ScoreCandidate(buffered);

        if (directScore <= 0 && bufferedScore <= 0)
            return direct ?? buffered;

        return directScore >= bufferedScore ? direct : buffered;
    }

    private static int ScoreCandidate(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return 0;

        int score = value.Length;
        foreach (char ch in value)
        {
            if (char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch))
                score += 2;
            else if (ch is '/' or '-' or '_' or '.' or '\'')
                score += 1;
            else if (char.IsControl(ch))
                score -= 8;
        }

        return score;
    }

    private static bool IsLikelyWideChar(char ch)
    {
        return !char.IsControl(ch) || ch is '\r' or '\n' or '\t';
    }

    private static int QueueIncomingAfterOriginal(int flags, IntPtr text, int chatChannel)
    {
        int result = _originalIncomingChatWrapper!(flags, text, chatChannel);
        if (result != 0)
            PluginManager.QueueChatWindowText(ReadWidePString(text), unchecked((uint)chatChannel));

        return result;
    }

    private static int QueueIncomingWithHostEat(int flags, IntPtr text, int chatChannel)
    {
        string? line = ReadWidePString(text);
        if (Volatile.Read(ref _incomingChatSuppressionEnabled) != 0)
        {
            PluginManager.QueueChatWindowText(line, unchecked((uint)chatChannel));
            return 1;
        }

        int result = _originalIncomingChatWrapper!(flags, text, chatChannel);
        if (result != 0)
            PluginManager.QueueChatWindowText(line, unchecked((uint)chatChannel));

        return result;
    }

    private static string GetIncomingModeLabel(IncomingChatMode mode)
    {
        return mode switch
        {
            IncomingChatMode.WrapperQueuedText => "wrapper-queued-text",
            IncomingChatMode.WrapperQueuedTextHostEat => "wrapper-queued-text-host-eat",
            IncomingChatMode.AddTextToScrollHostEat => "add-text-to-scroll-host-eat",
            _ => "wrapper-unknown"
        };
    }

    private static bool TryResolveOutgoingOffset(
        AcClientTextSection textSection,
        bool fixedVaTrusted,
        out int outgoingOff,
        out string resolution)
    {
        outgoingOff = -1;
        resolution = "unresolved";

        if (fixedVaTrusted)
        {
            int fixedOff = OutgoingChatVa - textSection.TextBaseVa;
            if (fixedOff >= 0 && fixedOff < textSection.Bytes.Length)
            {
                if (PatternScanner.VerifyPattern(textSection.Bytes, fixedOff, OutgoingChatPattern))
                {
                    outgoingOff = fixedOff;
                    resolution = "fixed-va";
                    return true;
                }
                else
                {
                    byte entryByte = textSection.Bytes[fixedOff];
                    if (IsLikelyExternalHookOpcode(entryByte))
                    {
                        resolution = $"skipped (prepatched-0x{entryByte:X2})";
                        RynthLog.Verbose($"Compat: outgoing chat entry at 0x{OutgoingChatVa:X8} appears prepatched (opcode 0x{entryByte:X2}) - skipping RynthCore outgoing hook to avoid conflicting with another injector.");
                        return false;
                    }
                }
            }
        }

        int scannedOff = PatternScanner.FindPattern(textSection.Bytes, OutgoingChatPattern);
        if (scannedOff >= 0)
        {
            outgoingOff = scannedOff;
            resolution = "pattern-scan";
            return true;
        }

        resolution = "resolution failed";
        return false;
    }

    private static bool IsLikelyExternalHookOpcode(byte opcode)
    {
        return opcode is 0xE9 or 0xE8 or 0xEB or 0x68 or 0xFF;
    }

    /// <summary>True once a chat-manager 'this' has been captured from a live submit.</summary>
    public static bool CanDispatchDirect =>
        OutgoingInstalled && _originalOutgoingChat != null && _lastOutgoingThis != IntPtr.Zero;

    /// <summary>
    /// Dispatches a chat line by calling AC's outgoing-chat function directly — the
    /// same path AC takes when you press Enter on the native chat bar, but with no
    /// keystroke simulation and no chat-bar state, so it is repeatable. Uses the
    /// 'this' + command source captured by <see cref="OutgoingChatDetour"/> from a
    /// prior real submit. Returns false if the hook isn't installed or no 'this' has
    /// been captured yet — the caller then falls back to keystroke simulation, whose
    /// first successful send seeds the capture for all subsequent calls.
    /// Plugin pre-dispatch / engine-command handling is the caller's responsibility
    /// (already done in ChatCommandDispatcher.Dispatch), so this goes straight to AC.
    /// </summary>
    public static unsafe bool TryDispatchDirect(string? text)
    {
        if (!OutgoingInstalled || _originalOutgoingChat == null)
            return false;

        IntPtr self = _lastOutgoingThis;
        if (self == IntPtr.Zero || string.IsNullOrWhiteSpace(text))
            return false;

        string line = text!.TrimEnd('\r', '\n');
        if (line.Length == 0)
            return false;
        foreach (char c in line)
            if (c < 0x20 && c != '\t')
                return false; // never feed AC control chars
        LogoffOriginProbe.RecordChat("OutgoingChat direct", line);

        try
        {
            ushort[] chars = new ushort[line.Length + 1]; // +1 null terminator (wcslen)
            for (int i = 0; i < line.Length; i++)
                chars[i] = line[i];

            var wide = OutgoingWidePString.Create(chars);
            try
            {
                // 'wide' is a stack local (unmanaged struct) — its address is stable
                // for the synchronous call, so no 'fixed' pin is needed (or allowed).
                _originalOutgoingChat(self, (IntPtr)(&wide), _lastOutgoingSource);
                return true;
            }
            finally { wide.Dispose(); }
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"OutgoingChat direct failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Clears the cached chat-manager 'this'. Called on logout: AC frees the object
    /// during the world→charselect transition, so the stale pointer must not be
    /// reused. It is re-captured from the next real submit after the next login.
    /// </summary>
    public static void ResetOutgoingTarget()
    {
        _lastOutgoingThis = IntPtr.Zero;
    }

    // Minimal duplicate of ClientHelperHooks.WidePString (AC1Legacy::PStringBase<wchar_t>).
    // Duplicated deliberately so the actively-used WriteToChat path stays untouched.
    // Phase B / 1a-dup: resolve the PStringBase<wchar_t> ctor/dtor (fn VAs) by pattern and the
    // s_NullBuffer (data VA) by code-xref. These were duplicate raw VAs left by 1a's per-file pass.
    private static readonly byte?[] PatWidePStringCtor = [ 0x56, 0x57, 0x8B, 0x7C, 0x24, 0x0C, 0x85, 0xFF, 0x8B, 0xF1, 0x74, 0x2C ];
    private static readonly byte?[] PatWidePStringDtor = [ 0x56, 0x8B, 0x31, 0x83, 0xEE, 0x14, 0x8D, 0x46 ];
    private static readonly byte?[] PatXrefWideNullBuffer = [ 0x3B, 0x05, null, null, null, null, 0x74, 0xE7 ];
    private static unsafe void* ResolveFnVa(string name, byte?[] pattern, int fallbackVa)
    {
        if (!AcClientModule.TryReadTextSection(out AcClientTextSection text)) return (void*)fallbackVa;
        HookResolver.ResolveResult r = HookResolver.Resolve(text, name, pattern, fallbackVa);
        return (void*)(r.Success ? r.Address : new IntPtr(fallbackVa));
    }
    private static IntPtr ResolveDataVa(string name, byte?[] pattern, int operandOffset, int fallbackVa)
    {
        if (!AcClientModule.TryReadTextSection(out AcClientTextSection text)) return new IntPtr(fallbackVa);
        return HookResolver.ResolveData(text, name, pattern, operandOffset, fallbackVa).Address;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct OutgoingWidePString
    {
        private static readonly IntPtr NullWideBufferVa = ResolveDataVa("ChatCallback.PStringW_NullBuffer", PatXrefWideNullBuffer, 2, 0x00818340);
        private static readonly delegate* unmanaged[Thiscall]<OutgoingWidePString*, ushort*, void> Ctor =
            (delegate* unmanaged[Thiscall]<OutgoingWidePString*, ushort*, void>)ResolveFnVa("ChatCallback.PStringW_ctor", PatWidePStringCtor, 0x00402730);
        private static readonly delegate* unmanaged[Thiscall]<OutgoingWidePString*, void> Dtor =
            (delegate* unmanaged[Thiscall]<OutgoingWidePString*, void>)ResolveFnVa("ChatCallback.PStringW_dtor", PatWidePStringDtor, 0x004011B0);

        public IntPtr CharBuffer;

        public static OutgoingWidePString Create(ushort[] chars)
        {
            var value = new OutgoingWidePString { CharBuffer = Marshal.ReadIntPtr(NullWideBufferVa) };
            fixed (ushort* pChars = chars)
                Ctor(&value, pChars);
            return value;
        }

        /// <summary>From a NUL-terminated buffer the caller keeps pinned for the call.</summary>
        public static OutgoingWidePString Create(ushort* chars)
        {
            var value = new OutgoingWidePString { CharBuffer = Marshal.ReadIntPtr(NullWideBufferVa) };
            Ctor(&value, chars);
            return value;
        }

        /// <summary>Runs the static resolution now; true when all three addresses are set.</summary>
        public static bool Warm() => NullWideBufferVa != IntPtr.Zero && Ctor != null && Dtor != null;

        public void Dispose()
        {
            fixed (OutgoingWidePString* ptr = &this)
                Dtor(ptr);
        }
    }
}
