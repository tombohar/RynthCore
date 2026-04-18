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
    private const int IncomingChatAddTextToScrollVa = 0x005649F0;
    private const int IncomingChatWrapperVa = 0x0058A000;
    private const int OutgoingChatVa = 0x005821A0;
    private const bool EnableIncomingHook = true;
    private const bool EnableOutgoingHook = true;
    private static IncomingChatMode CurrentIncomingMode => IncomingChatMode.AddTextToScrollHostEat;

    private static readonly byte[] IncomingChatAddTextToScrollSignature =
    [
        0x81, 0xEC, 0x48, 0x09, 0x00, 0x00, 0x8A, 0x84,
        0x24, 0x54, 0x09, 0x00, 0x00, 0x53, 0x55, 0x56,
        0x33, 0xF6, 0x84, 0xC0, 0x57, 0x8B, 0xBC, 0x24,
        0x5C, 0x09, 0x00, 0x00, 0x89, 0x4C, 0x24, 0x24
    ];

    private static readonly byte[] IncomingChatWrapperSignature =
    [
        0xA1, 0xE4, 0x0B, 0x87, 0x00, 0x85, 0xC0, 0x75,
        0x06, 0xB8, 0x01, 0x00, 0x00, 0x00, 0xC3, 0x56,
        0x8B, 0x74, 0x24, 0x10, 0x83, 0xFE, 0x01, 0x74,
        0x0F
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
        int incomingOff = IncomingChatWrapperVa - textSection.TextBaseVa;
        if (!PatternScanner.VerifyBytes(textSection.Bytes, incomingOff, IncomingChatWrapperSignature))
        {
            incomingStatus = $"signature mismatch @ 0x{IncomingChatWrapperVa:X8}";
            RynthLog.Compat($"Compat: incoming chat hook unavailable - {incomingStatus}");
            return false;
        }

        try
        {
            _incomingAddress = new IntPtr(textSection.TextBaseVa + incomingOff);
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
        int incomingOff = IncomingChatAddTextToScrollVa - textSection.TextBaseVa;
        if (!PatternScanner.VerifyBytes(textSection.Bytes, incomingOff, IncomingChatAddTextToScrollSignature))
        {
            incomingStatus = $"signature mismatch @ 0x{IncomingChatAddTextToScrollVa:X8}";
            RynthLog.Compat($"Compat: incoming chat hook unavailable - {incomingStatus}");
            return false;
        }

        try
        {
            unsafe
            {
                _incomingAddress = new IntPtr(textSection.TextBaseVa + incomingOff);
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
        // 1. Read string BEFORE original — buffer may be freed after
        string? line = (text != IntPtr.Zero) ? ReadIncomingChatLine(text, chatType) : null;

        // 2. Call original
        var pOriginal = (delegate* unmanaged[Thiscall]<IntPtr, IntPtr, uint, uint, IntPtr, int>)_originalIncomingChatAddTextPtr;
        int result = pOriginal(thisPtr, text, chatType, unknown, stringInfo);

        // 3. Queue AFTER original returns — game state is consistent
        if (line != null)
            QueueIncomingChatLine(line, chatType);

        return result;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string? ReadIncomingChatLine(IntPtr pStringBase, uint chatType)
    {
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

            string? line = length > 0
                ? Marshal.PtrToStringUni(charData, length)?.TrimEnd('\r', '\n')
                : null;

            int count = Interlocked.Increment(ref _incomingCallCount);
            if (count <= 0)
                RynthLog.Verbose($"Compat: incoming chat #{count} type={chatType} len={length}");

            return line;
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
        try
        {
            string? line = ReadWidePString(text);

            if (PluginManager.DispatchChatBarEnter(line))
                return 1;

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
}
